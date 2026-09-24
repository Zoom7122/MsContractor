namespace DocumentRelationsGenerator.Relations;

/// <summary>
/// Confirmed JSON API 1.2 relations. Must match the "Подтверждённые связи" table in
/// DOCUMENT_RELATIONS.md row for row (enforced by DocumentRelationsMarkdownTests).
/// </summary>
public static class RelationCatalog
{
    private static readonly string[] IncomingOperationTargets =
        ["customerorder", "demand", "invoiceout", "purchasereturn", "commissionreportin"];

    private static readonly string[] OutgoingOperationTargets =
        ["purchaseorder", "supply", "invoicein", "salesreturn", "commissionreportout"];

    public static IReadOnlyList<RelationDefinition> All { get; } = Build();

    private static readonly Dictionary<string, RelationDefinition> ById =
        All.ToDictionary(relation => relation.Id, StringComparer.Ordinal);

    public static RelationDefinition Get(string id) =>
        ById.TryGetValue(id, out var relation)
            ? relation
            : throw new KeyNotFoundException($"Relation {id} is not in DOCUMENT_RELATIONS.md.");

    public static bool Contains(string id) => ById.ContainsKey(id);

    private static List<RelationDefinition> Build()
    {
        var relations = new List<RelationDefinition>
        {
            Template("demand", "customerOrder", "customerorder", RelationKind.Reference, "demands"),
            Template("demand", "invoicesOut", "invoiceout", RelationKind.Collection, "demands"),
            Template("invoiceout", "customerOrder", "customerorder", RelationKind.Reference, "invoicesOut"),
            Template("invoiceout", "demands", "demand", RelationKind.Collection, "invoicesOut"),
            Template("salesreturn", "demand", "demand", RelationKind.Reference, "returns"),
            Template("loss", "salesReturn", "salesreturn", RelationKind.Reference, "losses"),
            Template("factureout", "demands", "demand", RelationKind.Collection, "factureOut"),
            Template("factureout", "payments", "paymentin", RelationKind.Collection, "factureOut"),
            Template("factureout", "payments", "cashin", RelationKind.Collection, "factureOut"),
            Template("factureout", "returns", "purchasereturn", RelationKind.Collection, "factureOut"),
            Template("purchaseorder", "customerOrders", "customerorder", RelationKind.Collection, "purchaseOrders"),
            Template("supply", "purchaseOrder", "purchaseorder", RelationKind.Reference, "supplies"),
            Template("supply", "invoicesIn", "invoicein", RelationKind.Collection, "supplies"),
            Template("invoicein", "purchaseOrder", "purchaseorder", RelationKind.Reference, "invoicesIn"),
            Template("invoicein", "supplies", "supply", RelationKind.Collection, "invoicesIn"),
            Template("purchasereturn", "supply", "supply", RelationKind.Reference, "returns"),
            Template("facturein", "supplies", "supply", RelationKind.Collection, "factureIn"),
            Template("facturein", "payments", "paymentout", RelationKind.Collection, "factureIn"),
            Template("facturein", "payments", "cashout", RelationKind.Collection, "factureIn")
        };

        foreach (var payment in new[] { "paymentin", "cashin" })
        {
            relations.AddRange(IncomingOperationTargets.Select(target => Operation(payment, target)));
            relations.Add(new(payment, "operations", "retailshift", RelationKind.Operation, CreationPath.PostOperations, null));
        }

        foreach (var payment in new[] { "paymentout", "cashout" })
            relations.AddRange(OutgoingOperationTargets.Select(target => Operation(payment, target)));

        relations.AddRange(
        [
            Template("retaildemand", "retailShift", "retailshift", RelationKind.Reference, null),
            Template("retaildemand", "customerOrder", "customerorder", RelationKind.Reference, null),
            Template("retailsalesreturn", "demand", "retaildemand", RelationKind.Reference, null),
            Template("retailsalesreturn", "retailShift", "retailshift", RelationKind.Reference, null)
        ]);
        return relations;
    }

    private static RelationDefinition Template(string source, string field, string target, RelationKind kind,
        string? reverse) => new(source, field, target, kind, CreationPath.Template, reverse);

    private static RelationDefinition Operation(string payment, string target) =>
        new(payment, "operations", target, RelationKind.Operation, CreationPath.Template, "payments");
}
