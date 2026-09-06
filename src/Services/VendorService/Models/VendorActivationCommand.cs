using MsContractor.VendorService.Contracts;

namespace MsContractor.VendorService.Models;

public sealed record VendorActivationCommand(
    Guid AppId,
    Guid AccountId,
    string Authorization,
    string RequestId,
    VendorActivationRequest Request);
