namespace MsContractor.MoySkladEgressService.Services;

public static class SupportedMoySkladDocumentTypes
{
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        "customerorder",
        "demand",
        "invoiceout",
        "invoicein",
        "supply",
        "purchaseorder",
        "paymentin",
        "paymentout",
        "cashin",
        "cashout",
        "retaildemand",
        "counterpartyadjustment",
        "commissionreportin",
        "commissionreportout"
    ]);

    public static bool Contains(string documentType) =>
        All.Contains(documentType, StringComparer.Ordinal);
}

public sealed record MoySkladDocumentPageRow(
    Guid DocumentId,
    string AgentHref,
    string? AgentType);

public sealed record MoySkladDocumentPage(
    int Size,
    int Limit,
    int Offset,
    IReadOnlyList<MoySkladDocumentPageRow> Rows,
    int StatusCode);
