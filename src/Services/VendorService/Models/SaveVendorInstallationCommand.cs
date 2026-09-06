namespace MsContractor.VendorService.Models;

public sealed record SaveVendorInstallationCommand(
    string RequestId,
    Installation? Installation,
    OutboxMessage? OutboxMessage);
