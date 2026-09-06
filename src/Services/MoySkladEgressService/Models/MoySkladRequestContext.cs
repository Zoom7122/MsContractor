namespace MsContractor.MoySkladEgressService.Models;

public sealed record MoySkladRequestContext(
    Guid AccountId,
    string CorrelationId,
    Guid? MergeJobId,
    Guid? MergeOperationId,
    string HttpMethod,
    string Endpoint,
    string EntityType,
    Guid? EntityId,
    int RetryAttempt = 0);
