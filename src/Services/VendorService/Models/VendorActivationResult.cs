namespace MsContractor.VendorService.Models;

public sealed record VendorActivationResult(string Status, Guid AccountId, bool IdempotentReplay);
