using System.Text.Json;
using System.Text.Json.Nodes;
using MsContractor.MoySkladEgressService.Gateways.Documents.Purchasereturn;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Purchasereturn;

public interface IPurchaseReturnVerifier
{
    Task<PurchaseReturnVerificationResult> VerifyAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<PurchaseReturnVerificationInput> documents,
        CancellationToken cancellationToken);
}

public sealed class PurchaseReturnVerifier : IPurchaseReturnVerifier
{
    private const string Verified = "Verified";
    private const string Mismatch = "Mismatch";
    private const string NeedsManualReview = "NeedsManualReview";
    private const string VerificationFailed = "VerificationFailed";

    private static readonly HashSet<string> ComparedDocumentFields =
    [
        "organization", "organizationAccount", "store", "moment", "applicable", "shared", "name", "code",
        "externalCode", "description", "project", "state", "vatEnabled", "vatIncluded", "rate", "owner",
        "group", "attributes", "supply"
    ];

    private static readonly HashSet<string> IgnoredPositionFields = ["id", "meta", "accountId"];

    private static readonly string[] PositionFields =
    ["assortment", "quantity", "price", "discount", "vat", "vatEnabled", "pack", "slot", "things"];

    private readonly IPurchaseReturnPreparationRepository _repository;
    private readonly IMoySkladPurchaseReturnGateway _purchaseReturns;
    private readonly IMoySkladPurchaseReturnPositionsGateway _positions;
    private readonly ILogger<PurchaseReturnVerifier> _logger;

    public PurchaseReturnVerifier(
        IPurchaseReturnPreparationRepository repository,
        IMoySkladPurchaseReturnGateway purchaseReturns,
        IMoySkladPurchaseReturnPositionsGateway positions,
        ILogger<PurchaseReturnVerifier> logger)
    {
        _repository = repository;
        _purchaseReturns = purchaseReturns;
        _positions = positions;
        _logger = logger;
    }

    public async Task<PurchaseReturnVerificationResult> VerifyAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<PurchaseReturnVerificationInput> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
            return new PurchaseReturnVerificationResult([]);

        var sourceIds = documents.Select(item => item.SourceDocumentId).Distinct().ToArray();
        var newIds = documents.Select(item => item.NewDocumentId).Distinct().ToArray();
        IReadOnlyDictionary<Guid, string> oldDocuments;
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>> oldPositions;
        IReadOnlyDictionary<Guid, string> newDocuments;

        try
        {
            oldDocuments = await _repository.GetRequiredDocumentsAsync(accountId, sourceIds, cancellationToken);
            oldPositions = await _repository.GetRequiredPositionsAsync(accountId, sourceIds, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or EgressException)
        {
            _logger.LogError(exception, "Unable to load purchasereturn snapshots for verification: account_id={AccountId}", accountId);
            return new PurchaseReturnVerificationResult(documents.Select(item => Failed(
                item, "PURCHASERETURN_OLD_SNAPSHOT_UNAVAILABLE", exception.Message)).ToArray());
        }

        try
        {
            newDocuments = await _purchaseReturns.GetAsync(
                accountId,
                Guid.Empty,
                Guid.NewGuid().ToString("D"),
                newIds,
                cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or EgressException)
        {
            _logger.LogError(exception, "Unable to load recreated purchasereturns for verification: account_id={AccountId}", accountId);
            return new PurchaseReturnVerificationResult(documents.Select(item => Failed(
                item, "PURCHASERETURN_NEW_DOCUMENT_UNAVAILABLE", exception.Message)).ToArray());
        }

        var results = new List<PurchaseReturnDocumentVerificationResult>(documents.Count);
        foreach (var input in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!oldDocuments.TryGetValue(input.SourceDocumentId, out var oldRaw) ||
                !oldPositions.TryGetValue(input.SourceDocumentId, out var oldPositionRows))
            {
                results.Add(Failed(
                    input,
                    "PURCHASERETURN_OLD_SNAPSHOT_MISSING",
                    "The old purchasereturn snapshot or its positions are missing."));
                continue;
            }

            if (!newDocuments.TryGetValue(input.NewDocumentId, out var newRaw))
            {
                results.Add(Failed(
                    input,
                    "PURCHASERETURN_NEW_DOCUMENT_MISSING",
                    "The recreated purchasereturn was not returned by MoySklad."));
                continue;
            }

            try
            {
                var oldDocument = ParseObject(oldRaw);
                if (HasMeaningfulValue(oldDocument["payments"]))
                {
                    _logger.LogInformation(
                        "Old purchasereturn contains payments; automatic payment relation rebinding was processed: account_id={AccountId}, source_id={SourceId}, new_id={NewId}",
                        accountId,
                        input.SourceDocumentId,
                        input.NewDocumentId);
                }

                var newPositionRows = await LoadPositionsAsync(
                    accountId, input.NewDocumentId, cancellationToken);
                results.Add(VerifyDocument(
                    input,
                    mainCounterpartyId,
                    oldRaw,
                    oldPositionRows,
                    newRaw,
                    newPositionRows));
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException or EgressException)
            {
                _logger.LogError(
                    exception,
                    "Unable to verify purchasereturn: account_id={AccountId}, source_id={SourceId}, new_id={NewId}",
                    accountId,
                    input.SourceDocumentId,
                    input.NewDocumentId);
                results.Add(Failed(input, "PURCHASERETURN_POSITIONS_UNAVAILABLE", exception.Message));
            }
        }

        return new PurchaseReturnVerificationResult(results);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> LoadPositionsAsync(
        Guid accountId,
        Guid purchaseReturnId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        int? expectedSize = null;
        for (var offset = 0; expectedSize is null || offset < expectedSize.Value; offset += MoySkladPurchaseReturnPositionsGateway.PageSize)
        {
            var page = await _positions.GetPageAsync(
                accountId,
                Guid.Empty,
                Guid.NewGuid().ToString("D"),
                purchaseReturnId,
                MoySkladPurchaseReturnPositionsGateway.PageSize,
                offset,
                cancellationToken);
            expectedSize ??= page.Size;
            if (page.Size != expectedSize || page.Offset != offset || page.Limit != MoySkladPurchaseReturnPositionsGateway.PageSize)
                throw new InvalidOperationException("MoySklad returned changing or invalid purchasereturn positions pagination.");

            foreach (var position in page.Positions)
            {
                if (!result.TryAdd(position.Key, position.Value))
                    throw new InvalidOperationException($"MoySklad returned duplicate purchasereturn position {position.Key:D}.");
            }
        }

        if (expectedSize is null || result.Count != expectedSize.Value)
            throw new InvalidOperationException("MoySklad returned an incomplete purchasereturn positions response.");
        return result;
    }

    private static PurchaseReturnDocumentVerificationResult VerifyDocument(
        PurchaseReturnVerificationInput input,
        Guid mainCounterpartyId,
        string oldRaw,
        IReadOnlyDictionary<Guid, string> oldPositions,
        string newRaw,
        IReadOnlyDictionary<Guid, string> newPositions)
    {
        var oldDocument = ParseObject(oldRaw);
        var newDocument = ParseObject(newRaw);
        var fieldMismatches = new List<PurchaseReturnFieldMismatch>();
        var positionMismatches = ComparePositions(oldPositions, newPositions);
        var warnings = new List<string>();

        foreach (var oldProperty in oldDocument)
        {
            if (!ComparedDocumentFields.Contains(oldProperty.Key))
                continue;

            if (!newDocument.TryGetPropertyValue(oldProperty.Key, out var actual))
            {
                if (oldProperty.Key == "supply" && !HasMeaningfulValue(oldProperty.Value))
                    continue;

                fieldMismatches.Add(new PurchaseReturnFieldMismatch(
                    oldProperty.Key,
                    Canonicalize(oldProperty.Value),
                    null));
                continue;
            }

            var expected = Canonicalize(oldProperty.Value);
            var actualCanonical = Canonicalize(actual);
            if (!string.Equals(expected, actualCanonical, StringComparison.Ordinal))
            {
                fieldMismatches.Add(new PurchaseReturnFieldMismatch(
                    oldProperty.Key,
                    expected,
                    actualCanonical));
            }
        }

        CompareAgent(newDocument, mainCounterpartyId, fieldMismatches);
        CompareChangedReference(
            "contract",
            oldDocument,
            newDocument,
            input.ContractId,
            "contract",
            fieldMismatches);
        var hadAgentAccount = HasMeaningfulValue(oldDocument["agentAccount"]);
        CompareChangedReference(
            "agentAccount",
            oldDocument,
            newDocument,
            input.AgentAccountId,
            "account",
            fieldMismatches);
        if (hadAgentAccount && input.AgentAccountId is null)
            warnings.Add("The old agentAccount exists but an account for the main counterparty was not found.");

        var syncId = ReadString(newDocument["syncId"]);
        if (!string.Equals(syncId, input.SyncId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            fieldMismatches.Add(new PurchaseReturnFieldMismatch(
                "syncId", input.SyncId.ToString("D"), syncId));

        if (HasMeaningfulValue(oldDocument["files"]))
            warnings.Add("The old purchasereturn has files; file copying requires manual review.");

        var status = fieldMismatches.Count > 0 || positionMismatches.Count > 0
            ? Mismatch
            : warnings.Count > 0 ? NeedsManualReview : Verified;
        return new PurchaseReturnDocumentVerificationResult(
            input.SourceDocumentId,
            input.NewDocumentId,
            status,
            fieldMismatches,
            positionMismatches,
            warnings);
    }

    private static void CompareAgent(
        JsonObject newDocument,
        Guid mainCounterpartyId,
        ICollection<PurchaseReturnFieldMismatch> mismatches)
    {
        var actual = newDocument["agent"];
        if (!TryReadReference(actual, out var actualId, out var actualType) ||
            actualId != mainCounterpartyId || !string.Equals(actualType, "counterparty", StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(new PurchaseReturnFieldMismatch(
                "agent",
                mainCounterpartyId.ToString("D"),
                ReadReferenceDescription(actual)));
        }
    }

    private static void CompareChangedReference(
        string field,
        JsonObject oldDocument,
        JsonObject newDocument,
        Guid? expectedId,
        string expectedType,
        ICollection<PurchaseReturnFieldMismatch> mismatches)
    {
        var hadOldValue = HasMeaningfulValue(oldDocument[field]);
        var actual = newDocument[field];
        if (!hadOldValue)
        {
            if (HasMeaningfulValue(actual))
                mismatches.Add(new PurchaseReturnFieldMismatch(field, null, ReadReferenceDescription(actual)));
            return;
        }

        if (expectedId is null)
        {
            if (HasMeaningfulValue(actual))
                mismatches.Add(new PurchaseReturnFieldMismatch(field, null, ReadReferenceDescription(actual)));
            return;
        }

        if (!TryReadReference(actual, out var actualId, out var actualType) ||
            actualId != expectedId || !string.Equals(actualType, expectedType, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(new PurchaseReturnFieldMismatch(
                field,
                expectedId.Value.ToString("D"),
                ReadReferenceDescription(actual)));
        }
    }

    private static IReadOnlyList<PurchaseReturnPositionMismatch> ComparePositions(
        IReadOnlyDictionary<Guid, string> oldPositions,
        IReadOnlyDictionary<Guid, string> newPositions)
    {
        var expected = BuildMultiset(oldPositions.Values);
        var actual = BuildMultiset(newPositions.Values);
        var mismatches = new List<PurchaseReturnPositionMismatch>();

        foreach (var item in expected)
        {
            actual.TryGetValue(item.Key, out var actualCount);
            for (var index = actualCount; index < item.Value; index++)
                mismatches.Add(new PurchaseReturnPositionMismatch("Missing", item.Key, null));
        }

        foreach (var item in actual)
        {
            expected.TryGetValue(item.Key, out var expectedCount);
            for (var index = expectedCount; index < item.Value; index++)
                mismatches.Add(new PurchaseReturnPositionMismatch("Extra", null, item.Key));
        }

        return mismatches;
    }

    private static Dictionary<string, int> BuildMultiset(IEnumerable<string> rawPositions)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rawPosition in rawPositions)
        {
            var position = ParseObject(rawPosition);
            var selected = new JsonObject();
            foreach (var field in PositionFields)
            {
                if (IgnoredPositionFields.Contains(field))
                    continue;
                if (position.TryGetPropertyValue(field, out var value))
                    selected[field] = value?.DeepClone();
            }

            var canonical = Canonicalize(selected);
            result[canonical] = result.TryGetValue(canonical, out var count) ? count + 1 : 1;
        }
        return result;
    }

    private static string Canonicalize(JsonNode? node)
    {
        if (node is null)
            return "null";
        if (node is JsonValue value)
            return value.ToJsonString();
        if (node is JsonArray array)
            return "[" + string.Join(',', array.Select(Canonicalize)) + "]";
        var obj = node.AsObject();
        return "{" + string.Join(',', obj.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => JsonSerializer.Serialize(item.Key) + ":" + Canonicalize(NormalizeReference(item.Value)))) + "}";
    }

    private static JsonNode? NormalizeReference(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return node;
        if (obj["href"] is JsonValue directHref && obj["type"] is JsonValue directType)
        {
            return new JsonObject
            {
                ["href"] = directHref.DeepClone(),
                ["type"] = directType.DeepClone(),
                ["mediaType"] = obj["mediaType"]?.DeepClone() ?? "application/json"
            };
        }
        if (obj["meta"] is JsonObject meta &&
            meta["href"] is JsonValue href && meta["type"] is JsonValue type)
        {
            var normalizedMeta = new JsonObject
            {
                ["href"] = href.DeepClone(),
                ["type"] = type.DeepClone(),
                ["mediaType"] = meta["mediaType"]?.DeepClone() ?? "application/json"
            };
            return new JsonObject { ["meta"] = normalizedMeta };
        }

        var result = new JsonObject();
        foreach (var property in obj)
            result[property.Key] = NormalizeReference(property.Value?.DeepClone());
        return result;
    }

    private static JsonObject ParseObject(string rawJson) =>
        JsonNode.Parse(rawJson)?.AsObject()
        ?? throw new JsonException("The purchasereturn JSON is not an object.");

    private static bool HasMeaningfulValue(JsonNode? node) =>
        node is not null && node is not JsonValue value ||
        node is JsonValue scalar && scalar.ToJsonString() is not "null";

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

    private static bool TryReadReference(JsonNode? node, out Guid id, out string? type)
    {
        id = Guid.Empty;
        type = null;
        if (node is not JsonObject reference || reference["meta"] is not JsonObject meta ||
            meta["href"] is not JsonValue href || !href.TryGetValue<string>(out var hrefValue) ||
            !Uri.TryCreate(hrefValue, UriKind.Absolute, out var uri))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || !Guid.TryParse(segments[^1], out id) || id == Guid.Empty)
            return false;
        type = meta["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeString)
            ? typeString
            : null;
        return true;
    }

    private static string? ReadReferenceDescription(JsonNode? node)
    {
        if (TryReadReference(node, out var id, out var type))
            return $"{type}:{id:D}";
        return node is null ? null : Canonicalize(node);
    }

    private static PurchaseReturnDocumentVerificationResult Failed(
        PurchaseReturnVerificationInput input,
        string code,
        string message) => new(
            input.SourceDocumentId,
            input.NewDocumentId,
            VerificationFailed,
            [],
            [],
            [],
            code,
            message);
}
