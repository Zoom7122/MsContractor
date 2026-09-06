using MsContractor.VendorService.Contracts;

namespace MsContractor.VendorService.Models;

public sealed record CreatedMoyskladSession(
    string Token,
    MoyskladSessionResponse Response);
