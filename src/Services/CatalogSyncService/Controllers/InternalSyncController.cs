using Confluent.Kafka;
using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.Contracts.Sync;
using MsContractor.CatalogSyncService.Services;

namespace MsContractor.CatalogSyncService.Controllers;

[ApiController]
[Route("internal/sync")]
public sealed class InternalSyncController(
    ISyncKafkaPublisher publisher,
    IConfiguration configuration,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<SyncAccepted>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] SyncStartRequest request,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, configuration))
            return Unauthorized(new { code = "INTERNAL_UNAUTHORIZED", message = "Internal authentication failed." });

        if (request.AccountId == Guid.Empty ||
            request.RequestedByUserId == Guid.Empty ||
            !Enum.IsDefined(request.Mode))
        {
            return BadRequest(new { code = "INVALID_SYNC_COMMAND", message = "Sync command contains invalid fields." });
        }

        try
        {
            var command = new SyncRequested(
                Guid.NewGuid(),
                Guid.NewGuid(),
                request.AccountId,
                request.RequestedByUserId,
                timeProvider.GetUtcNow(),
                request.Mode);
            await publisher.PublishAsync(command, cancellationToken);
            return Accepted(new SyncAccepted(command.SyncRunId, "queued"));
        }
        catch (ProduceException<string, string>)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { code = "KAFKA_UNAVAILABLE", message = "Synchronization queue is unavailable." });
        }
    }
}
