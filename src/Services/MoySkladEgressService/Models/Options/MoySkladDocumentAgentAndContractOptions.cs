namespace MsContractor.MoySkladEgressService.Models.Options;

public sealed class MoySkladDocumentAgentAndContractOptions
{
    public required IReadOnlyList<string> DocumentTypes { get; init; }

    public static MoySkladDocumentAgentAndContractOptions Parse(string? value)
    {
        var options = MoySkladDocumentChangeOptions.Parse(value, "DOCUMENTS_CHANGE_AGENT_AND_CONTRACT");
        return new MoySkladDocumentAgentAndContractOptions { DocumentTypes = options.DocumentTypes };
    }
}
