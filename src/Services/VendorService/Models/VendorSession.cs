namespace MsContractor.VendorService.Models;

public sealed record VendorSession(
    Guid AccountId,
    Guid EmployeeId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);
