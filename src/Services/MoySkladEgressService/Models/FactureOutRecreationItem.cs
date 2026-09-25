namespace MsContractor.MoySkladEgressService.Models;

public sealed class FactureOutRecreationItem
{
    public Guid AccountId { get; set; }

    public Guid SourceDocumentId { get; set; }

    public Guid? SourceSyncId { get; set; }

    public Guid? NewSyncId { get; set; }

    public Guid? NewDocumentId { get; set; }

    public string Status { get; set; } = string.Empty;
}

public static class FactureOutRecreationStatuses
{
    public const string Prepared = "Prepared";

    public const string CreatePayload = "CreatePayload";

    public const string Skipped = "Skipped";

    public const string Created = "Created";

    public const string Failed = "Failed";
}
