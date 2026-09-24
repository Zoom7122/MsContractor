using System.Text.Json.Nodes;
using DocumentRelationsGenerator.Documents;
using DocumentRelationsGenerator.MoySklad;
using DocumentRelationsGenerator.Relations;

namespace DocumentRelationsGenerator.Tests;

/// <summary>
/// In-memory MoySklad: stores created objects, builds templates from bases, computes documented reverse
/// fields on GET and rejects POST bodies with fields the document type does not have.
/// </summary>
public sealed class FakeMoySkladApi : IMoySkladApi
{
    public const string Base = "https://api.moysklad.ru/api/remap/1.2/";
    private static readonly HashSet<string> EntityTypes =
        ["counterparty", "contract", "product", "service", "project", "store", "expenseitem"];

    private readonly Dictionary<string, JsonObject> objects = new(StringComparer.OrdinalIgnoreCase);
    private int counter;

    public FakeMoySkladApi(bool withStore = true, bool withRetailStore = false)
    {
        Organization = Add("organization", new JsonObject { ["name"] = "ООО Тест" });
        if (withStore) Store = Add("store", new JsonObject { ["name"] = "Основной склад" });
        Employee = Add("employee", new JsonObject
        {
            ["name"] = "Менеджер",
            ["group"] = MetaReference.Create(Base + "entity/group/00000000-0000-0000-0000-00000000aaaa", "group")
        });
        ExpenseItem = Add("expenseitem", new JsonObject { ["name"] = "Закупка товаров" });
        if (withRetailStore)
            RetailStore = Add("retailstore", new JsonObject
            {
                ["name"] = "Точка продаж", ["active"] = true, ["organization"] = MetaReference.To(Organization)
            });
    }

    public Uri BaseUrl => new(Base);
    public JsonObject Organization { get; }
    public JsonObject? Store { get; }
    public JsonObject Employee { get; }
    public JsonObject ExpenseItem { get; }
    public JsonObject? RetailStore { get; }
    public List<string> Calls { get; } = [];
    public List<string> Deleted { get; } = [];
    public Func<string, JsonObject, bool>? FailCreate { get; set; }

    public IReadOnlyCollection<JsonObject> Objects => objects.Values;

    public Task<JsonObject> GetAsync(string pathOrHref, CancellationToken cancellationToken)
    {
        var path = Relative(pathOrHref);
        Calls.Add("GET " + path);
        var query = path.IndexOf('?');
        var bare = query < 0 ? path : path[..query];
        return Task.FromResult(bare switch
        {
            "entity/organization" => List(Organization),
            "entity/store" => Store is null ? List() : List(Store),
            "entity/expenseitem" => List(ExpenseItem),
            "entity/taxrate" => List(new JsonObject { ["rate"] = 0 }, new JsonObject { ["rate"] = 10 },
                new JsonObject { ["rate"] = 20 }, new JsonObject { ["rate"] = 7, ["archived"] = true }),
            "entity/currency" => List(new JsonObject { ["name"] = "руб", ["default"] = true }),
            "context/employee" => Employee,
            _ when bare.EndsWith("/accounts", StringComparison.Ordinal) => Accounts(bare),
            _ => Get(bare)
        });
    }

    public Task<JsonObject> GetTemplateAsync(string entityType, JsonObject baseDocuments, CancellationToken cancellationToken)
    {
        Calls.Add($"PUT entity/{entityType}/new");
        var template = (JsonObject)baseDocuments.DeepClone();
        var bases = baseDocuments.SelectMany(pair => pair.Value switch
        {
            JsonArray array => array.Select(MetaReference.Href),
            JsonObject obj => [MetaReference.Href(obj)],
            _ => []
        }).OfType<string>().Select(href => objects[Key(href)]).ToList();
        var main = bases.FirstOrDefault(document => document["agent"] is not null) ?? bases.First();

        foreach (var field in new[] { "organization", "agent", "contract", "agentAccount", "store", "rate" })
            if (main[field] is { } value && DocumentFieldSupport.Supports(entityType, field)) template[field] = value.DeepClone();
        if (entityType == "purchaseorder") template.Remove("agent");
        if (entityType == "retaildemand") template["retailStore"] = MetaReference.To(RetailStore!);
        if (DocumentFieldSupport.Supports(entityType, "positions") && main["positions"] is JsonArray positions)
            template["positions"] = new JsonObject { ["rows"] = positions.DeepClone() };

        var sum = (long)JsonNumbers.Read(main["sum"]);
        if (template["operations"] is JsonArray operations)
        {
            var paid = objects.Values.Where(document => document["operations"] is JsonArray ops &&
                    ops.Any(op => MetaReference.SameEntity(MetaReference.Href(op), MetaReference.Href(main))))
                .Sum(document => (long)JsonNumbers.Read(document["sum"]));
            sum = Math.Max(0, sum - paid);
            foreach (var operation in operations.OfType<JsonObject>()) operation["linkedSum"] = sum;
        }

        template["sum"] = sum;
        if (template["operations"] is not null) template["vatSum"] = sum / 6; // VAT of the full unpaid amount
        template["meta"] = new JsonObject { ["type"] = entityType };
        template["accountId"] = Guid.Empty.ToString();
        return Task.FromResult(template);
    }

    public Task<JsonObject> CreateAsync(string entityType, JsonObject payload, CancellationToken cancellationToken)
    {
        Calls.Add($"POST entity/{entityType}");
        if (FailCreate?.Invoke(entityType, payload) == true)
            throw new MoySkladApiException("POST", $"/api/remap/1.2/entity/{entityType}", 400, 3000, "Ошибка сохранения объекта");

        if (!EntityTypes.Contains(entityType))
        {
            var allowed = RelationCatalog.All.Where(relation => relation.SourceType == entityType)
                .Select(relation => relation.Kind == RelationKind.Operation ? "operations" : relation.Field)
                .Append("sum")
                .ToHashSet();
            var unknown = payload.Select(pair => pair.Key)
                .Where(key => !DocumentFieldSupport.Supports(entityType, key) && !allowed.Contains(key)).ToList();
            if (unknown.Count > 0)
                throw new MoySkladApiException("POST", $"/api/remap/1.2/entity/{entityType}", 400, 1001,
                    $"unknown fields: {string.Join(", ", unknown)}");
        }

        // Real API: 412 / 3007 "поле 'vatSum' не может быть больше 'sum'".
        if (JsonNumbers.Read(payload["vatSum"]) > JsonNumbers.Read(payload["sum"]))
            throw new MoySkladApiException("POST", $"/api/remap/1.2/entity/{entityType}", 412, 3007, "vatSum > sum");

        var entity = (JsonObject)payload.DeepClone();
        if (entity["positions"] is JsonArray positions)
            entity["sum"] = (long)positions.OfType<JsonObject>().Sum(position =>
                JsonNumbers.Read(position["price"]) * JsonNumbers.Read(position["quantity"]) *
                (1 - JsonNumbers.Read(position["discount"]) / 100m));
        if (entityType == "counterparty" && entity["accounts"] is JsonArray accounts)
            foreach (var account in accounts.OfType<JsonObject>())
                account["meta"] = new JsonObject
                {
                    ["href"] = $"{Base}entity/counterparty/x/accounts/{Guid.NewGuid()}", ["type"] = "account"
                };
        return Task.FromResult(Add(entityType, entity));
    }

    public Task DeleteAsync(string pathOrHref, CancellationToken cancellationToken)
    {
        var path = Relative(pathOrHref);
        Calls.Add("DELETE " + path);
        if (!objects.Remove(path)) throw new MoySkladApiException("DELETE", path, 404, 1021, "not found");
        Deleted.Add(path);
        return Task.CompletedTask;
    }

    private JsonObject Get(string path)
    {
        if (!objects.TryGetValue(path, out var stored)) throw new MoySkladApiException("GET", path, 404, 1021, "not found");
        var document = (JsonObject)stored.DeepClone();
        var type = MetaReference.Type(document)!;
        var href = MetaReference.Href(document);
        foreach (var relation in RelationCatalog.All.Where(item => item.TargetType == type && item.ReverseField is not null))
        {
            var sources = objects.Values.Where(source => MetaReference.Type(source) == relation.SourceType &&
                                                         References(source, relation).Any(item => MetaReference.SameEntity(item, href)))
                .Select(MetaReference.To).ToList();
            if (sources.Count == 0) continue;
            if (relation.ReverseField!.EndsWith('s'))
            {
                var array = document[relation.ReverseField] as JsonArray ?? new JsonArray();
                foreach (var source in sources) array.Add(source);
                document[relation.ReverseField] = array;
            }
            else document[relation.ReverseField] = sources[0];
        }

        return document;
    }

    private JsonObject Accounts(string path)
    {
        var owner = objects.GetValueOrDefault(path[..^"/accounts".Length]);
        return owner?["accounts"] is JsonArray accounts ? new JsonObject { ["rows"] = accounts.DeepClone() } : List();
    }

    private static IEnumerable<string> References(JsonObject source, RelationDefinition relation) =>
        source[relation.Kind == RelationKind.Operation ? "operations" : relation.Field] switch
        {
            JsonArray array => array.Select(MetaReference.Href).OfType<string>(),
            JsonObject obj => MetaReference.Href(obj) is { } href ? [href] : [],
            _ => []
        };

    private JsonObject Add(string type, JsonObject entity)
    {
        var id = $"00000000-0000-0000-0000-{++counter:000000000000}";
        entity["id"] = id;
        entity["meta"] = new JsonObject { ["href"] = $"{Base}entity/{type}/{id}", ["type"] = type, ["mediaType"] = "application/json" };
        objects[$"entity/{type}/{id}"] = entity;
        return (JsonObject)entity.DeepClone();
    }

    private static JsonObject List(params JsonObject[] rows) =>
        new() { ["rows"] = new JsonArray(rows.Select(row => (JsonNode)row.DeepClone()).ToArray()) };

    private static string Relative(string pathOrHref) =>
        pathOrHref.StartsWith(Base, StringComparison.Ordinal) ? pathOrHref[Base.Length..] : pathOrHref;

    private static string Key(string href) => Relative(href);
}
