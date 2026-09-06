namespace MsContractor.DuplicatesMergeService.Models;

public sealed record RecreatedDocumentSnapshot(Guid OldId, Guid NewId, Guid DuplicateId, string RawJson, DateTimeOffset UpdatedAt);
