namespace MsContractor.MoySkladEgressService.Models;

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
        "commissionreportout",
        "salesreturn",
        "purchasereturn",
        "retailsalesreturn",
        "factureout",
        "facturein"
    ]);

    public static bool Contains(string documentType) =>
        All.Contains(documentType, StringComparer.Ordinal);
}
