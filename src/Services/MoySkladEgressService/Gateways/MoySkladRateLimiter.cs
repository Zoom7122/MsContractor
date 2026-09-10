namespace MsContractor.MoySkladEgressService.Gateways;

public interface IMoySkladRateLimiter
{
    Task WaitAsync(Guid accountId, Guid? userId, CancellationToken cancellationToken);

    Task ObserveAsync(
        Guid accountId,
        HttpResponseMessage response,
        CancellationToken cancellationToken);
}

public sealed class ObservingMoySkladRateLimiter : IMoySkladRateLimiter
{
    private readonly ILogger<ObservingMoySkladRateLimiter> _logger;

    public ObservingMoySkladRateLimiter(
        ILogger<ObservingMoySkladRateLimiter> logger)
    {
        _logger = logger;
    }

    public Task WaitAsync(Guid accountId, Guid? userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ObserveAsync(
        Guid accountId,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation(
            "MoySklad rate limit observed for account {AccountId}: status={StatusCode}, limit={Limit}, remaining={Remaining}, retryAfterMs={RetryAfterMs}, resetMs={ResetMs}",
            accountId,
            (int)response.StatusCode,
            Header(response, "X-RateLimit-Limit"),
            Header(response, "X-RateLimit-Remaining"),
            Header(response, "X-Lognex-Retry-After"),
            Header(response, "X-Lognex-Reset"));
        return Task.CompletedTask;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()
            : null;
}
