namespace MsContractor.CatalogSyncService.Models;

public static class DocumentsInOperationTypes
{
    public const string ChangeCounterparty = "change_counterparty";
    public const string ChangeAgentAndContract = "change_agent_and_contract";
    public const string RecreateSalesReturn = "recreate_salesreturn";
    public const string RecreatePurchaseReturn = "recreate_purchasereturn";
    public const string RecreateFactureIn = "recreate_facturein";
    public const string RecreateFactureOut = "recreate_factureout";
}

public sealed class DocumentsInOperation
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid MergeJobId { get; set; }
    public string OperationType { get; set; } = null!;
    public string RawJson { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public MergeJob MergeJob { get; set; } = null!;
}
