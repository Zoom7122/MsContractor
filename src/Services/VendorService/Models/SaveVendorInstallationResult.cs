namespace MsContractor.VendorService.Models;

public sealed record SaveVendorInstallationResult(Installation? Installation, bool IdempotentReplay);
