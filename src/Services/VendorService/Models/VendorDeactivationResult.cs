namespace MsContractor.VendorService.Models;

public sealed record VendorDeactivationResult(
    string Status,
    Guid AccountId,
    bool InstallationFound,
    bool IdempotentReplay);
