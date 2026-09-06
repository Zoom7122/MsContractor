using MsContractor.Contracts.Internal;

namespace MsContractor.MoySkladEgressService.Models;

public sealed record MoySkladDocumentValidationFailure(
    MoySkladDocumentChangeItem Document,
    string Message,
    string Code = "MOYSKLAD_RESPONSE_VALIDATION_FAILED");
