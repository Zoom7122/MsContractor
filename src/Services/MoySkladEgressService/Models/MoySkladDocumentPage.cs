namespace MsContractor.MoySkladEgressService.Models;

public sealed record MoySkladDocumentPage(
    int Size,
    int Limit,
    int Offset,
    IReadOnlyList<MoySkladDocumentPageRow> Rows,
    int StatusCode);
