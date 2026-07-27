using Microsoft.Extensions.Options;
using MsContractor.VendorService.Contracts;
using MsContractor.VendorService.Repo;
using MsContractor.VendorService.Services.Exceptions;

namespace MsContractor.VendorService.Services;

public sealed record CreatedMoyskladSession(
    string Token,
    MoyskladSessionResponse Response);

public sealed class MoyskladSessionService(
    IMoyskladContextClient contextClient,
    IVendorSessionStore sessionStore,
    IVendorInstallationRepository repository,
    IOptions<VendorOptions> options,
    TimeProvider timeProvider)
{
    private static readonly HashSet<string> SupportedLocales =
        new(StringComparer.Ordinal) { "ru_RU", "en_US" };

    public async Task<CreatedMoyskladSession> CreateAsync(
        MoyskladSessionRequest request,
        string? priorToken,
        CancellationToken cancellationToken)
    {
        var contextKey = Required(request.ContextKey, "contextKey", 512);
        var appUid = Required(request.AppUid, "appUid", 255);
        var userLocale = Required(request.UserLocale, "userLocale", 16);
        if (!Guid.TryParse(request.AppId, out var appId))
            throw new VendorValidationException("appId must be a UUID.");
        if (!SupportedLocales.Contains(userLocale))
            throw new VendorValidationException("Unsupported userLocale.");
        if (appId != options.Value.AppId || !string.Equals(appUid, options.Value.AppUid, StringComparison.Ordinal))
            throw new VendorForbiddenException();

        var context = await contextClient.GetAsync(
            contextKey,
            appId,
            appUid,
            cancellationToken);
        var installation = await repository.GetByAccountIdAsync(context.AccountId, cancellationToken);
        if (installation is null ||
            installation.Status != "Active" ||
            installation.AppId != appId ||
            !string.Equals(installation.AppUid, appUid, StringComparison.Ordinal))
        {
            throw new VendorForbiddenException();
        }

        installation.LastContextAt = timeProvider.GetUtcNow();
        await repository.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(priorToken))
            await sessionStore.DeleteAsync(priorToken, cancellationToken);
        var token = await sessionStore.CreateAsync(
            context.AccountId,
            context.Id,
            cancellationToken);

        return new CreatedMoyskladSession(
            token,
            new MoyskladSessionResponse(context.AccountId, context.Id));
    }

    public async Task<MoyskladSessionResponse?> GetAsync(
        string? token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        var session = await sessionStore.GetAndRefreshAsync(token, cancellationToken);
        return session is null
            ? null
            : new MoyskladSessionResponse(session.AccountId, session.EmployeeId);
    }

    public Task LogoutAsync(string? token, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(token)
            ? Task.CompletedTask
            : sessionStore.DeleteAsync(token, cancellationToken);

    private static string Required(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new VendorValidationException($"{name} is required.");
        if (value.Length > maxLength)
            throw new VendorValidationException($"{name} is too long.");
        return value;
    }
}
