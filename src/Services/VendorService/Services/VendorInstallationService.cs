using System.Text.Json;
using Microsoft.Extensions.Options;
using MsContractor.VendorService.Contracts;
using MsContractor.VendorService.Repo;
using MsContractor.VendorService.Services.Exceptions;

namespace MsContractor.VendorService.Services;

public sealed record VendorActivationCommand(
    Guid AppId,
    Guid AccountId,
    string Authorization,
    string RequestId,
    VendorActivationRequest Request);

public sealed record VendorActivationResult(string Status, Guid AccountId, bool IdempotentReplay);

public sealed record VendorDeactivationCommand(
    Guid AppId,
    Guid AccountId,
    string Authorization,
    string RequestId,
    VendorDeactivationRequest Request);

public sealed record VendorDeactivationResult(
    string Status,
    Guid AccountId,
    bool InstallationFound,
    bool IdempotentReplay);

public sealed class VendorInstallationService(
    VendorJwtValidator jwtValidator,
    VendorJwtReplayStore replayStore,
    VendorInstallationRepository repository,
    AccessTokenProtector tokenProtector,
    IOptions<VendorOptions> options,
    TimeProvider timeProvider,
    ILogger<VendorInstallationService> logger)
{
    private const string JsonApiResource = "https://api.moysklad.ru/api/remap/1.2";

    public async Task<VendorActivationResult> ActivateAsync(
        VendorActivationCommand command,
        CancellationToken cancellationToken)
    {
        var jwt = jwtValidator.Validate(command.Authorization);
        await replayStore.EnsureUnusedAsync(jwt, cancellationToken);

        if (string.IsNullOrWhiteSpace(command.RequestId))
            throw new VendorValidationException("X-Lognex-RequestId header is required.");
        if (command.RequestId.Length > 128)
            throw new VendorValidationException("Request ID is too long.");
        if (command.AppId != options.Value.AppId || command.Request.AppUid != options.Value.AppUid)
            throw new VendorForbiddenException();

        var cause = command.Request.Cause switch
        {
            "Install" or "Resume" or "TariffChanged" or "Autoprolongation" => command.Request.Cause,
            _ => throw new VendorValidationException("Unsupported activation cause.")
        };
        var now = timeProvider.GetUtcNow();
        var installation = await repository.GetByAccountIdAsync(command.AccountId, cancellationToken);

        if (cause is "TariffChanged" or "Autoprolongation")
        {
            if (installation is null)
                throw new VendorNotFoundException();
            UpdateCommon(installation, command, now);
        }
        else
        {
            var access = command.Request.Access?.SingleOrDefault(item =>
                item.Resource == JsonApiResource || item.Resource == $"{JsonApiResource}/");
            if (string.IsNullOrWhiteSpace(access?.AccessToken) ||
                access.Scope is null || !access.Scope.Any(scope => scope is "admin" or "custom"))
                throw new VendorValidationException("A JSON API access token with admin or custom scope is required.");

            installation ??= new Installation
            {
                AccountId = command.AccountId,
                InstalledAt = now,
                CreatedAt = now
            };
            UpdateCommon(installation, command, now);
            installation.Status = "Active";
            installation.ActivatedAt = now;
            installation.DeactivatedAt = null;
            var protectedToken = tokenProtector.Protect(access.AccessToken);
            installation.AccessTokenCiphertext = protectedToken.Ciphertext;
            installation.AccessTokenNonce = protectedToken.Nonce;
            installation.AccessTokenTag = protectedToken.Tag;
            installation.TokenKeyVersion = protectedToken.KeyVersion;
            installation.AccessScope = ToJsonDocument(access.Scope);
        }

        var eventType = cause switch
        {
            "Install" => "InstallationActivated",
            "Resume" => "InstallationResumed",
            "TariffChanged" => "InstallationTariffChanged",
            _ => "InstallationAutoprolonged"
        };
        var outbox = new OutboxMessage
        {
            Id = Guid.NewGuid(), RequestId = command.RequestId, AccountId = command.AccountId,
            EventType = eventType, CreatedAt = now, PublishedAt = null, PublishAttempts = 0,
            Payload = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                eventId = Guid.NewGuid(), requestId = command.RequestId, accountId = command.AccountId,
                appId = command.AppId, cause, occurredAt = now
            }))
        };
        var saved = await repository.SaveAsync(
            new SaveVendorInstallationCommand(command.RequestId, installation, outbox), cancellationToken);
        logger.LogInformation("Vendor activation handled. RequestId={RequestId}, AccountId={AccountId}, AppId={AppId}, Cause={Cause}, IdempotentReplay={IdempotentReplay}",
            command.RequestId, command.AccountId, command.AppId, cause, saved.IdempotentReplay);
        return new VendorActivationResult("Activated", command.AccountId, saved.IdempotentReplay);
    }

    public async Task<VendorDeactivationResult> DeactivateAsync(
        VendorDeactivationCommand command,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Vendor deactivation received. RequestId={RequestId}, AccountId={AccountId}, AppId={AppId}, Cause={Cause}",
            command.RequestId, command.AccountId, command.AppId, command.Request.Cause);

        var jwt = jwtValidator.Validate(command.Authorization);
        await replayStore.EnsureUnusedAsync(jwt, cancellationToken);

        if (string.IsNullOrWhiteSpace(command.RequestId))
            throw new VendorValidationException("X-Lognex-RequestId header is required.");
        if (command.RequestId.Length > 128)
            throw new VendorValidationException("Request ID is too long.");
        if (command.AppId != options.Value.AppId || command.Request.AppUid != options.Value.AppUid)
            throw new VendorForbiddenException();

        var cause = command.Request.Cause switch
        {
            "Uninstall" or "Suspend" => command.Request.Cause,
            _ => throw new VendorValidationException("Unsupported deactivation cause.")
        };
        var now = timeProvider.GetUtcNow();
        var installation = await repository.GetByAccountIdAsync(command.AccountId, cancellationToken);
        if (installation is null)
        {
            await repository.SaveAsync(
                new SaveVendorInstallationCommand(command.RequestId, null, null), cancellationToken);
            logger.LogInformation("Vendor deactivation completed. RequestId={RequestId}, AccountId={AccountId}, AppId={AppId}, Cause={Cause}, InstallationFound={InstallationFound}, IdempotentReplay={IdempotentReplay}, Status={Status}",
                command.RequestId, command.AccountId, command.AppId, cause, false, false, "NotFound");
            return new VendorDeactivationResult("NotFound", command.AccountId, false, false);
        }

        installation.Status = cause == "Uninstall" ? "Uninstalled" : "Suspended";
        installation.DeactivatedAt = now;
        installation.UpdatedAt = now;
        installation.AccessTokenCiphertext = null;
        installation.AccessTokenNonce = null;
        installation.AccessTokenTag = null;
        installation.TokenKeyVersion = null;

        var eventType = cause == "Uninstall" ? "InstallationUninstalled" : "InstallationSuspended";
        var outbox = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            RequestId = command.RequestId,
            AccountId = command.AccountId,
            EventType = eventType,
            CreatedAt = now,
            PublishedAt = null,
            PublishAttempts = 0,
            Payload = CreateOutboxPayload(command.RequestId, command.AccountId, command.AppId, cause, now)
        };
        var saved = await repository.SaveAsync(
            new SaveVendorInstallationCommand(command.RequestId, installation, outbox), cancellationToken);
        logger.LogInformation("Vendor deactivation completed. RequestId={RequestId}, AccountId={AccountId}, AppId={AppId}, Cause={Cause}, InstallationFound={InstallationFound}, IdempotentReplay={IdempotentReplay}, Status={Status}",
            command.RequestId, command.AccountId, command.AppId, cause, true, saved.IdempotentReplay, installation.Status);
        return new VendorDeactivationResult(installation.Status, command.AccountId, true, saved.IdempotentReplay);
    }

    private static void UpdateCommon(Installation installation, VendorActivationCommand command, DateTimeOffset now)
    {
        installation.AppId = command.AppId;
        installation.AppUid = command.Request.AppUid!;
        installation.AccountName = command.Request.AccountName;
        installation.Subscription = command.Request.Subscription is null ? null : ToJsonDocument(command.Request.Subscription);
        installation.UpdatedAt = now;
    }

    private static JsonDocument ToJsonDocument<T>(T value) => JsonDocument.Parse(JsonSerializer.Serialize(value));

    private static JsonDocument CreateOutboxPayload(
        string requestId,
        Guid accountId,
        Guid appId,
        string cause,
        DateTimeOffset occurredAt) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(), requestId, accountId, appId, cause, occurredAt
        }));
}
