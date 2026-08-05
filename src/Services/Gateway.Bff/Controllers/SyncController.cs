using Confluent.Kafka;
using Microsoft.AspNetCore.Mvc;
using MsContractor.Contracts.Sync;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Gateway.Bff.Controllers;

[ApiController]
[Route("api/sync")]
public sealed class SyncController(
    IGatewaySessionReader sessionReader,
    ISyncCommandPublisher publisher,
    IConfiguration configuration,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<SyncAccepted>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateAsync(CancellationToken cancellationToken)
        => await CreateAsync(SyncMode.Full, cancellationToken);

    [HttpPost("incremental")]
    [ProducesResponseType<SyncAccepted>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateIncrementalAsync(CancellationToken cancellationToken)
        => await CreateAsync(SyncMode.Incremental, cancellationToken);

    private async Task<IActionResult> CreateAsync(
        SyncMode mode,
        CancellationToken cancellationToken)
    {
        var cookieName = configuration["Session:CookieName"] ?? "mscontractor.session";
        Request.Cookies.TryGetValue(cookieName, out var token);
        var session = await sessionReader.ReadAsync(token, cancellationToken);
        if (session is null)
            return Unauthorized(new { code = "SESSION_UNAUTHORIZED", message = "Session is missing or expired." });

        var command = new SyncRequested(
            Guid.NewGuid(),
            Guid.NewGuid(),
            session.AccountId,
            session.EmployeeId,
            timeProvider.GetUtcNow(),
            mode);

        try
        {
            await publisher.PublishAsync(command, cancellationToken);
        }
        catch (ProduceException<string, string>)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { code = "KAFKA_UNAVAILABLE", message = "Synchronization queue is unavailable." });
        }

        return Accepted(new SyncAccepted(command.SyncRunId, "queued"));
    }
}
