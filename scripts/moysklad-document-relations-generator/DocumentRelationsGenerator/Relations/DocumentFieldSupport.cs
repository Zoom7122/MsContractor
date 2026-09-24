namespace DocumentRelationsGenerator.Relations;

/// <summary>
/// Writable business fields per document type, taken from the "Атрибуты сущности" tables of JSON API 1.2
/// (see DOCUMENT_RELATIONS.md, "Поля документов для рандомизации"). Payload builders may only add
/// fields listed here; relation fields are validated separately against <see cref="RelationCatalog"/>.
/// </summary>
public static class DocumentFieldSupport
{
    private static readonly string[] Common =
        ["name", "description", "externalCode", "code", "moment", "applicable", "rate", "group", "owner", "syncId"];

    private static readonly string[] Trade =
    [
        "shared", "organization", "agent", "contract", "agentAccount", "organizationAccount", "store", "project",
        "vatEnabled", "vatIncluded", "positions"
    ];

    private static readonly Dictionary<string, HashSet<string>> Fields = new(StringComparer.Ordinal)
    {
        ["customerorder"] = Set(Common, Trade, ["deliveryPlannedMoment"]),
        ["demand"] = Set(Common, Trade),
        ["invoiceout"] = Set(Common, Trade, ["paymentPlannedMoment"]),
        ["invoicein"] = Set(Common, Trade, ["incomingNumber", "incomingDate", "paymentPlannedMoment"]),
        ["supply"] = Set(Common, Trade, ["incomingNumber", "incomingDate"]),
        ["purchaseorder"] = Set(Common, Trade, ["deliveryPlannedMoment"]),
        ["salesreturn"] = Set(Common, Trade),
        ["purchasereturn"] = Set(Common, Trade),
        ["paymentin"] = Set(Common,
        [
            "shared", "organization", "agent", "contract", "agentAccount", "organizationAccount", "project", "sum",
            "vatSum", "paymentPurpose", "incomingNumber", "incomingDate"
        ]),
        ["paymentout"] = Set(Common,
        [
            "shared", "organization", "agent", "contract", "agentAccount", "organizationAccount", "project", "sum",
            "vatSum", "paymentPurpose", "expenseItem"
        ]),
        ["cashin"] = Set(Common,
            ["shared", "organization", "agent", "contract", "project", "sum", "vatSum", "paymentPurpose"]),
        ["cashout"] = Set(Common,
            ["shared", "organization", "agent", "contract", "project", "sum", "vatSum", "paymentPurpose", "expenseItem"]),
        // syncId is read-only for commission reports; shared and vatIncluded are read-only for commissionreportout.
        ["commissionreportin"] = Set(Without(Common, "syncId"),
        [
            "shared", "organization", "agent", "contract", "agentAccount", "organizationAccount", "project",
            "vatEnabled", "vatIncluded", "positions", "commissionPeriodStart", "commissionPeriodEnd"
        ]),
        ["commissionreportout"] = Set(Without(Common, "syncId"),
        [
            "organization", "agent", "contract", "agentAccount", "organizationAccount", "project", "vatEnabled",
            "positions", "commissionPeriodStart", "commissionPeriodEnd"
        ]),
        ["factureout"] = Set(Common, ["shared", "organization", "agent", "contract", "paymentPurpose"]),
        ["facturein"] = Set(Common, ["shared", "organization", "agent", "contract", "incomingNumber", "incomingDate"]),
        // loss has no agent, contract or VAT fields; expenseItem is read-only there.
        ["loss"] = Set(Common, ["shared", "organization", "store", "project", "positions"]),
        // retaildemand.organization is read-only (taken from the retail store); no contract/project.
        ["retaildemand"] = Set(Common,
        [
            "shared", "agent", "agentAccount", "organizationAccount", "store", "vatEnabled", "vatIncluded",
            "positions", "cashSum", "noCashSum", "retailShift", "retailStore"
        ]),
        ["retailsalesreturn"] = Set(Common,
        [
            "shared", "organization", "agent", "contract", "agentAccount", "organizationAccount", "store",
            "vatEnabled", "vatIncluded", "positions", "cashSum", "noCashSum", "retailShift", "retailStore"
        ]),
        ["retailshift"] = Set(["name", "description", "externalCode", "moment", "syncId"],
            ["organization", "retailStore", "store"])
    };

    /// <summary>Documents whose agent must be the scenario counterparty.</summary>
    public static readonly IReadOnlySet<string> AgentBearing = Fields.Keys
        .Where(type => type is not ("loss" or "retailshift"))
        .ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyCollection<string> DocumentTypes => Fields.Keys;

    public static bool Supports(string documentType, string field) =>
        Fields.TryGetValue(documentType, out var fields) && fields.Contains(field);

    private static HashSet<string> Set(params string[][] groups) =>
        groups.SelectMany(group => group).ToHashSet(StringComparer.Ordinal);

    private static string[] Without(string[] fields, string field) => fields.Where(item => item != field).ToArray();
}
