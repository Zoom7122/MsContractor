using MsContractor.VendorService.Contracts;

namespace MsContractor.VendorService.Models;

public sealed record VendorDeactivationCommand(
    Guid AppId,
    Guid AccountId,
    string Authorization,
    string RequestId,
    VendorDeactivationRequest Request);
