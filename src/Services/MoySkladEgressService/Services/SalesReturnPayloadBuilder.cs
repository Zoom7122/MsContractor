using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Models.Options;

namespace MsContractor.MoySkladEgressService.Services;

public sealed class SalesReturnPayloadBuilder(IOptions<EgressOptions> options)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly Uri baseUri = options.Value.JsonApiBaseUrl;
    private static readonly string[] PositionFields =
        ["assortment", "quantity", "price", "discount", "vat", "vatEnabled", "pack", "things", "trackingCodes", "cost", "country", "gtd", "overhead", "slot"];

    public void Validate(RecreateSalesReturnsRequest request)
    {
        if (request.MainCounterpartyId == Guid.Empty || request.Documents is null || request.Documents.Count == 0 ||
            request.Documents.Any(x => x is null || x.OldDocumentId == Guid.Empty || x.DuplicateCounterpartyId == Guid.Empty ||
                x.DuplicateCounterpartyId == request.MainCounterpartyId || x.NewAgentAccountId == Guid.Empty ||
                x.NewContractId == Guid.Empty || x.Data is null) ||
            request.Documents.Select(x => x.OldDocumentId).Distinct().Count() != request.Documents.Count)
            throw Invalid("A main counterparty and unique source documents on duplicate counterparties are required.");
        foreach (var item in request.Documents)
        {
            var data = item.Data;
            if (data.Organization is null || data.Store is null || data.Positions is null || data.Positions.Length == 0)
                throw Invalid("organization, store and a complete non-empty positions array are required.");
            if (data.Moment is not null && !DateTime.TryParseExact(data.Moment,
                    ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw Invalid("moment must be a MoySklad date/time string.");
            foreach (var (reference, type) in new (JsonObject?, string)[]
            {
                (data.Organization, "organization"), (data.Store, "store"), (data.Demand, "demand"),
                (data.Project, "project"), (data.SalesChannel, "saleschannel"), (data.Owner, "employee"), (data.Group, "group")
            }) if (reference is not null) ValidateReference(reference, type);
            if (data.OrganizationAccount is not null)
            {
                ValidateReference(data.OrganizationAccount, "account");
                var organizationPath = new Uri(data.Organization["meta"]!["href"]!.GetValue<string>()).AbsolutePath;
                var accountPath = new Uri(data.OrganizationAccount["meta"]!["href"]!.GetValue<string>()).AbsolutePath;
                if (!accountPath.StartsWith(organizationPath + "/accounts/", StringComparison.Ordinal))
                    throw Invalid("organizationAccount must belong to the supplied organization.");
            }
            if (data.State is not null)
            {
                ValidateReference(data.State, "state");
                var statePath = new Uri(data.State["meta"]!["href"]!.GetValue<string>()).AbsolutePath;
                if (!statePath.StartsWith(baseUri.AbsolutePath.TrimEnd('/') + "/entity/salesreturn/metadata/states/", StringComparison.Ordinal))
                    throw Invalid("state must be a salesreturn state.");
            }
            if (data.Rate is not null)
            {
                if (data.Rate["currency"] is not JsonObject currency) throw Invalid("rate requires currency metadata.");
                ValidateReference(currency, "currency");
                if (data.Rate["value"] is { } rate && (!TryNumber(rate, out var value) || value <= 0))
                    throw Invalid("rate.value must be positive.");
            }
            foreach (var attribute in data.Attributes ?? [])
            {
                if (attribute is null || !attribute.ContainsKey("value"))
                    throw Invalid("Each attribute requires metadata and a value.");
                ValidateReference(attribute, "attributemetadata");
            }
            foreach (var position in data.Positions)
            {
                if (position is null || position["assortment"] is not JsonObject assortment ||
                    !TryNumber(position["quantity"], out var q) || q <= 0 ||
                    !TryNumber(position["price"], out var p) || p < 0)
                    throw Invalid("Every position requires assortment, positive quantity and non-negative price.");
                ValidateReference(assortment, "product", "variant", "service", "bundle", "consignment");
                foreach (var name in new[] { "discount", "vat", "cost" })
                    if (position[name] is { } number && !TryNumber(number, out _))
                        throw Invalid($"Position {name} must be numeric.");
                if (position["vatEnabled"] is { } flag && flag.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                    throw Invalid("Position vatEnabled must be boolean.");
                if (position["gtd"] is { } gtd && (gtd is not JsonObject declaration ||
                    declaration["name"] is not JsonValue declarationName || !declarationName.TryGetValue<string>(out _)))
                    throw Invalid("Position gtd requires a declaration name.");
                foreach (var name in new[] { "country", "slot" })
                    if (position[name] is { } reference)
                    {
                        if (reference is not JsonObject obj) throw Invalid($"Position {name} requires metadata.");
                        ValidateReference(obj, name);
                    }
            }
        }
    }

    private static bool TryNumber(JsonNode? node, out decimal value)
    {
        value = 0;
        return node?.GetValueKind() == JsonValueKind.Number &&
            decimal.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private void ValidateReference(JsonObject reference, params string[] types)
    {
        if (reference["meta"] is not JsonObject meta || meta["href"] is not JsonValue hrefValue ||
            !hrefValue.TryGetValue<string>(out var href) || !Uri.TryCreate(href, UriKind.Absolute, out var uri) ||
            uri.Scheme != baseUri.Scheme || uri.Authority != baseUri.Authority ||
            !uri.AbsolutePath.StartsWith(baseUri.AbsolutePath.TrimEnd('/') + "/entity/", StringComparison.Ordinal) ||
            meta["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type) || !types.Contains(type) ||
            !Guid.TryParse(uri.AbsolutePath.TrimEnd('/').Split('/').Last(), out var id) || id == Guid.Empty)
            throw Invalid("A document reference has an invalid MoySklad URL, type or id.");
        if (type == "attributemetadata" && uri.AbsolutePath != baseUri.AbsolutePath.TrimEnd('/') + $"/entity/salesreturn/metadata/attributes/{id:D}")
            throw Invalid("Attribute must belong to salesreturn metadata.");
        if (type == "slot")
        {
            var relative = uri.AbsolutePath[baseUri.AbsolutePath.TrimEnd('/').Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (relative.Length != 5 || relative[1] != "store" || !Guid.TryParse(relative[2], out _) || relative[3] != "slots")
                throw Invalid("slot must reference a store slot.");
        }
        if (type is not "account" and not "state" and not "attributemetadata" and not "slot" &&
            uri.AbsolutePath != baseUri.AbsolutePath.TrimEnd('/') + $"/entity/{type}/{id:D}")
            throw Invalid("Reference URL does not match its entity type.");
    }

    public string Build(RecreateSalesReturnItem item, Guid mainId, Guid syncId)
    {
        var payload = JsonSerializer.SerializeToNode(item.Data, JsonOptions)!.AsObject();
        payload["agent"] = Reference($"entity/counterparty/{mainId:D}", "counterparty");
        payload.Remove("agentAccount");
        payload.Remove("contract");
        if (item.NewAgentAccountId is { } account)
            payload["agentAccount"] = Reference($"entity/counterparty/{mainId:D}/accounts/{account:D}", "account");
        if (item.NewContractId is { } contract)
            payload["contract"] = Reference($"entity/contract/{contract:D}", "contract");
        foreach (var name in new[] { "organization", "organizationAccount", "store", "demand", "project", "state", "salesChannel", "owner", "group" })
            if (payload[name] is JsonObject reference) payload[name] = CleanReference(reference);
        if (payload["rate"] is JsonObject rate && rate["currency"] is JsonObject currency)
            rate["currency"] = CleanReference(currency);
        if (payload["attributes"] is JsonArray attributes)
            foreach (var attribute in attributes.OfType<JsonObject>())
                attribute["meta"] = CleanReference(attribute)["meta"]!.DeepClone();
        var positions = new JsonArray();
        foreach (var original in item.Data.Positions!)
        {
            var position = new JsonObject();
            foreach (var field in PositionFields)
                if (original.TryGetPropertyValue(field, out var value)) position[field] = value?.DeepClone();
            position["assortment"] = CleanReference(original["assortment"]!.AsObject());
            foreach (var name in new[] { "country", "slot" })
                if (position[name] is JsonObject reference && reference["meta"] is JsonObject)
                    position[name] = CleanReference(reference);
            positions.Add(position);
        }
        payload["positions"] = positions;
        payload["syncId"] = syncId;
        return payload.ToJsonString(JsonOptions);
    }

    private JsonObject Reference(string path, string type) => new()
    {
        ["meta"] = new JsonObject { ["href"] = new Uri(baseUri, path).AbsoluteUri, ["type"] = type, ["mediaType"] = "application/json" }
    };
    private static JsonObject CleanReference(JsonObject reference)
    {
        var meta = reference["meta"]!;
        var uri = new Uri(meta["href"]!.GetValue<string>());
        return new JsonObject { ["meta"] = new JsonObject
            { ["href"] = uri.GetLeftPart(UriPartial.Path), ["type"] = meta["type"]!.DeepClone(), ["mediaType"] = "application/json" } };
    }
    private static EgressException Invalid(string message) => new(400, "INVALID_SALESRETURN_REQUEST", message);
}
