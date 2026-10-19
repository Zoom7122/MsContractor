namespace MsContractor.Contracts.Internal;

public sealed record DocumentsInOperationContent(
    string OperationType,
    string RawJson);
