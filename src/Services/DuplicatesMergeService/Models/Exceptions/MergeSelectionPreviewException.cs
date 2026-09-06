namespace MsContractor.DuplicatesMergeService.Models.Exceptions;

public sealed class MergeSelectionPreviewException(
    MergeSelectionPreviewError error,
    string code,
    string safeMessage) : Exception(safeMessage)
{
    public MergeSelectionPreviewError Error { get; } = error;
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
}
