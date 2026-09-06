namespace MsContractor.DuplicatesMergeService.Models.Exceptions;

public sealed class MergeRequestException(MergeRequestError error, string code, string safeMessage)
    : Exception(safeMessage)
{
    public MergeRequestError Error { get; } = error;
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
}
