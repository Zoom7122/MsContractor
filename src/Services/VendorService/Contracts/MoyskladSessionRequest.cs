namespace MsContractor.VendorService.Contracts;

public sealed record MoyskladSessionRequest(
    string? ContextKey,
    string? AppId,
    string? AppUid,
    string? UserLocale);
