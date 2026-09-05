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

public sealed record MoySkladDocumentPageRow(
    Guid DocumentId,
    string AgentHref,
    string? AgentType,
    Guid? ContractId = null,
    string? RawJson = null);

public sealed record MoySkladDocumentPage(
    int Size,
    int Limit,
    int Offset,
    IReadOnlyList<MoySkladDocumentPageRow> Rows,
    int StatusCode);

public sealed class MoySkladDocumentChangeOptions
{
    public required IReadOnlyList<string> DocumentTypes { get; init; }

    public static MoySkladDocumentChangeOptions Parse(string? value, string variableName = "DOCUMENTS_PUT_CHANGE")
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{variableName} must contain a comma-separated document type list.");
        var types = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (types.Length == 0 || types.Distinct(StringComparer.Ordinal).Count() != types.Length ||
            types.Any(type => !SupportedMoySkladDocumentTypes.Contains(type)))
        {
            throw new InvalidOperationException($"{variableName} contains duplicate or unsupported document types.");
        }
        return new MoySkladDocumentChangeOptions { DocumentTypes = types };
    }
}

public sealed class MoySkladDocumentAgentAndContractOptions
{
    public required IReadOnlyList<string> DocumentTypes { get; init; }

    public static MoySkladDocumentAgentAndContractOptions Parse(string? value)
    {
        var options = MoySkladDocumentChangeOptions.Parse(value, "DOCUMENTS_CHANGE_AGENT_AND_CONTRACT");
        return new MoySkladDocumentAgentAndContractOptions { DocumentTypes = options.DocumentTypes };
    }
}

public sealed class MoySkladDocumentDiscoveryOptions
{
    public required IReadOnlyList<string> DocumentTypes { get; init; }

    public static MoySkladDocumentDiscoveryOptions Parse(string? value)
    {
        var options = MoySkladDocumentChangeOptions.Parse(value, "DOCUMENTS_DISCOVERY");
        return new MoySkladDocumentDiscoveryOptions { DocumentTypes = options.DocumentTypes };
    }
}

public sealed record MoySkladDocumentChangeChunkResult(
    IReadOnlyList<MsContractor.Contracts.Internal.MoySkladDocumentChangeItem> ChangedDocuments,
    IReadOnlyList<MsContractor.Contracts.Internal.MoySkladDocumentChangeFailure> Failures);
