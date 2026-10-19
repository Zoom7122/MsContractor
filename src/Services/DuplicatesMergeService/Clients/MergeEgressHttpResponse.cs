namespace MsContractor.DuplicatesMergeService.Clients;

public sealed record MergeEgressHttpResponse(int StatusCode, string Body)
{
    public bool IsSuccessStatusCode => StatusCode is >= 200 and < 300;
}
