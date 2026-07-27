using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MsContractor.VendorService.Repo;
using MsContractor.VendorService.Services;

namespace MsContractor.VendorService.Tests;

internal static class TestSupport
{
    public static readonly Guid AppId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public const string AppUid = "mscontractor.test";
    public const string SecretKey = "test-secret-key";

    public static IOptions<VendorOptions> Options(
        TimeSpan? sessionLifetime = null) =>
        Microsoft.Extensions.Options.Options.Create(new VendorOptions
        {
            AppId = AppId,
            AppUid = AppUid,
            SecretKey = SecretKey,
            AccessTokenEncryptionKey = Convert.ToBase64String(new byte[32]),
            TokenKeyVersion = 1,
            AppsApiBaseUrl = new Uri("https://apps-api.moysklad.ru/api/vendor/1.0/"),
            SessionLifetime = sessionLifetime ?? TimeSpan.FromHours(8),
            SessionCookieName = "mscontractor.session"
        });

}

internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal sealed class TestWebHostEnvironment(string environmentName) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "VendorService.Tests";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = environmentName;
    public string ContentRootPath { get; set; } = string.Empty;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class FakeContextClient(MoyskladEmployeeContext context) : IMoyskladContextClient
{
    public int Calls { get; private set; }

    public Task<MoyskladEmployeeContext> GetAsync(
        string contextKey,
        Guid appId,
        string appUid,
        CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(context);
    }
}

internal sealed class FakeSessionStore : IVendorSessionStore
{
    private readonly Dictionary<string, VendorSession> _sessions = [];
    private int _nextToken;

    public List<string> DeletedTokens { get; } = [];
    public List<Guid> RevokedAccounts { get; } = [];

    public Task<string> CreateAsync(
        Guid accountId,
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        var token = $"token-{++_nextToken}";
        var now = DateTimeOffset.UtcNow;
        _sessions[token] = new VendorSession(accountId, employeeId, now, now);
        return Task.FromResult(token);
    }

    public Task<VendorSession?> GetAndRefreshAsync(
        string token,
        CancellationToken cancellationToken) =>
        Task.FromResult(_sessions.GetValueOrDefault(token));

    public Task DeleteAsync(string token, CancellationToken cancellationToken)
    {
        DeletedTokens.Add(token);
        _sessions.Remove(token);
        return Task.CompletedTask;
    }

    public Task RevokeAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        RevokedAccounts.Add(accountId);
        foreach (var token in _sessions
                     .Where(item => item.Value.AccountId == accountId)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _sessions.Remove(token);
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeInstallationRepository : IVendorInstallationRepository
{
    private readonly Dictionary<Guid, Installation> _installations = [];

    public int SaveChangesCalls { get; private set; }

    public void Add(Installation installation) =>
        _installations[installation.AccountId] = installation;

    public Task<Installation?> GetByAccountIdAsync(
        Guid accountId,
        CancellationToken cancellationToken) =>
        Task.FromResult(_installations.GetValueOrDefault(accountId));

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveChangesCalls++;
        return Task.FromResult(1);
    }

    public Task<SaveVendorInstallationResult> SaveAsync(
        SaveVendorInstallationCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Installation is not null)
            _installations[command.Installation.AccountId] = command.Installation;
        return Task.FromResult(new SaveVendorInstallationResult(command.Installation, false));
    }
}
