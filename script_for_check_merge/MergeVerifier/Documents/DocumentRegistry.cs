namespace MergeVerifier.Documents;

public enum DocumentTransferMode { PutAgent, Recreate, Unsupported }

public sealed record DocumentRule(string EntityType, DocumentTransferMode TransferMode)
{
    public string[] PositionCollections { get; init; } = [];
    public bool AgentFilterSupported { get; init; } = true;
    public string[] UnorderedCollections { get; init; } = [];
}

public static class DocumentRegistry
{
    // Evidence and conditional API restrictions: docs/document-transfer-rules.md.
    // Unknown fields are retained. These profiles describe collections, not a lossy field whitelist.
    public static IReadOnlyList<DocumentRule> All { get; } =
    [
        Put("customerorder", true, "purchaseOrders", "demands", "payments", "productionTasks", "invoicesOut", "moves", "prepayments"),
        Put("demand", true, "payments", "returns", "productionTasks", "invoicesOut"),
        Put("purchaseorder", true, "customerOrders", "supplies", "payments", "productionTasks", "invoicesIn"),
        Put("supply", true, "payments", "returns", "invoicesIn"),
        Put("paymentin", false, "operations"),
        Put("paymentout", false, "operations"),
        Put("cashin", false, "operations"),
        Put("cashout", false, "operations"),
        Put("retaildemand", true),
        Put("invoiceout", true, "demands", "payments"),
        Put("invoicein", true, "supplies", "payments"),
        Put("counterpartyadjustment", false),
        Put("commissionreportin", true, "payments") with
        { PositionCollections = ["positions", "returnToCommissionerPositions"] },
        Put("commissionreportout", true, "payments"),
        Recreate("salesreturn", true, "payments", "losses"),
        Recreate("purchasereturn", true, "payments"),
        Recreate("retailsalesreturn", true),
        // Factures cannot change agent in place: a live PUT {agent} on factureout answers 200 but keeps the old agent
        // (checked by GET). MSContractor deletes and recreates facturein (MergeProcessor.RecreateFactureInsAsync) and
        // prepares the same for factureout (Egress FactureOutRecreationOrchestrator).
        Recreate("factureout", false, "demands", "payments", "returns"),
        Recreate("facturein", false, "supplies", "payments"),
        new("retireorder", DocumentTransferMode.Unsupported)
        { AgentFilterSupported = false, PositionCollections = ["positions"] }
    ];

    public static DocumentRule Get(string type) => All.SingleOrDefault(x => x.EntityType == type)
        ?? throw new VerifierException("Unknown document type in dataset.");

    private static DocumentRule Put(string type, bool positions, params string[] unordered) =>
        new(type, DocumentTransferMode.PutAgent)
        { PositionCollections = positions ? ["positions"] : [], UnorderedCollections = unordered };

    private static DocumentRule Recreate(string type, bool positions, params string[] unordered) =>
        new(type, DocumentTransferMode.Recreate)
        { PositionCollections = positions ? ["positions"] : [], UnorderedCollections = unordered };
}
