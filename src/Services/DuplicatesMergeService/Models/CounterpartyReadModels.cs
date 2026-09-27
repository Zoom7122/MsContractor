namespace MsContractor.DuplicatesMergeService.Models;

public sealed record DuplicateCandidate(Guid Id, string? NormalizedName, string? NormalizedEmail, string? NormalizedPhone);
public sealed record CounterpartyDisplayItem(Guid Id, string Name, string? Email, string? Phone, string? Description, bool Archived, string RawJson, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record CounterpartyAvailability(Guid Id, bool Archived);
public sealed record CounterpartySelectionItem(Guid Id, string Name, string? Description, string? Email, string? Phone, bool Archived, DateTimeOffset UpdatedAt);
