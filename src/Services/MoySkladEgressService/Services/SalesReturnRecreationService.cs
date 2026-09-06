using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MsContractor.MoySkladEgressService.Contracts;
using MsContractor.MoySkladEgressService.Gateways;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services;

public sealed class SalesReturnRecreationService(ISalesReturnOperationRepository repository,
    IMoySkladSalesReturnGateway gateway, SalesReturnPayloadBuilder payloads, TimeProvider timeProvider)
{
    public async Task<RecreateSalesReturnsResponse> ExecuteAsync(SalesReturnCallContext context,
        RecreateSalesReturnsRequest request, CancellationToken cancellationToken)
    {
        payloads.Validate(request);
        await using var lease = await repository.TryLockAccountAsync(context.AccountId, cancellationToken)
            ?? throw new EgressException(409, "SALESRETURN_OPERATION_BUSY", "A recreation operation is already running for this account.", retryable: true);
        var requestJson = JsonSerializer.Serialize(request, SalesReturnPayloadBuilder.JsonOptions);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(JsonNode.Parse(requestJson)).ToJsonString())));
        var operation = await repository.GetAsync(context.AccountId, context.OperationId, cancellationToken);
        if (operation is not null && (operation.Fingerprint != fingerprint || operation.JobId != context.JobId || operation.UserId != context.UserId))
            throw new EgressException(409, "SALESRETURN_OPERATION_CONFLICT", "Operation id was already used with different content or context.");
        if (operation is null)
        {
            operation = new SalesReturnOperation
            {
                AccountId = context.AccountId, OperationId = context.OperationId, JobId = context.JobId,
                UserId = context.UserId, CorrelationId = context.CorrelationId, MainCounterpartyId = request.MainCounterpartyId,
                Fingerprint = fingerprint, RequestJson = requestJson, NextAttemptAt = timeProvider.GetUtcNow(),
                UpdatedAt = timeProvider.GetUtcNow(),
                Items = request.Documents.Select(item =>
                {
                    var syncId = Guid.NewGuid();
                    return new SalesReturnOperationItem { OldDocumentId = item.OldDocumentId, SyncId = syncId,
                        Payload = payloads.Build(item, request.MainCounterpartyId, syncId) };
                }).ToList()
            };
            await repository.CreateAsync(operation, cancellationToken);
        }
        await ProcessAsync(operation, cancellationToken);
        return Result(operation);
    }

    public async Task ResumeAsync(SalesReturnOperationKey key, CancellationToken cancellationToken)
    {
        await using var lease = await repository.TryLockAccountAsync(key.AccountId, cancellationToken);
        if (lease is null) return;
        var operation = await repository.GetAsync(key.AccountId, key.OperationId, cancellationToken);
        if (operation?.NextAttemptAt is { } due && due <= timeProvider.GetUtcNow())
            await ProcessAsync(operation, cancellationToken);
    }

    private async Task ProcessAsync(SalesReturnOperation operation, CancellationToken cancellationToken)
    {
        if (!operation.Items.Any(x => x.Pending)) return;
        var request = JsonSerializer.Deserialize<RecreateSalesReturnsRequest>(operation.RequestJson, SalesReturnPayloadBuilder.JsonOptions)!;
        var originals = request.Documents.ToDictionary(x => x.OldDocumentId);
        var context = new SalesReturnCallContext(operation.AccountId, operation.UserId, operation.JobId, operation.OperationId, operation.CorrelationId);
        operation.Attempts++;
        await SaveAsync(operation, cancellationToken);
        foreach (var item in operation.Items.Where(x => x.Pending && x.Stage == "Validate"))
        {
            try
            {
                await gateway.ValidateAsync(context, operation.MainCounterpartyId, originals[item.OldDocumentId], cancellationToken);
                Advance(item, "Delete");
            }
            catch (EgressException exception) { Fail(item, exception); }
            await SaveAsync(operation, cancellationToken);
        }
        // Complete the deletion pass for all eligible documents before creating any replacements.
        foreach (var item in operation.Items.Where(x => x.Pending && x.Stage is "Delete" or "Deleting"))
        {
            try
            {
                if (item.Stage == "Deleting" && !await gateway.ExistsAsync(context, item.OldDocumentId, cancellationToken))
                    Advance(item, "Create");
                else
                {
                    // Recheck ownership and demand immediately before a deletion, including resumed attempts.
                    await gateway.ValidateAsync(context, operation.MainCounterpartyId, originals[item.OldDocumentId], cancellationToken);
                    Advance(item, "Deleting");
                    await SaveAsync(operation, cancellationToken);
                    try { await gateway.DeleteAsync(context, item.OldDocumentId, cancellationToken); }
                    catch (EgressException exception) when (exception.Retryable || exception.StatusCode == 404)
                    {
                        if (await gateway.ExistsAsync(context, item.OldDocumentId, cancellationToken)) throw;
                    }
                    Advance(item, "Create");
                }
            }
            catch (EgressException exception) { Fail(item, exception); }
            await SaveAsync(operation, cancellationToken);
        }
        var pending = operation.Items.Where(x => x.Pending && x.Stage is "Create" or "Creating").ToArray();
        foreach (var chunk in pending.Chunk(operation.Attempts == 1 ? 1000 : 1))
        {
            foreach (var item in chunk) Advance(item, "Creating");
            await SaveAsync(operation, cancellationToken);
            try
            {
                var results = await gateway.CreateAsync(context, operation.MainCounterpartyId, chunk, cancellationToken);
                foreach (var item in chunk)
                {
                    var matches = results.Where(x => x.SyncId == item.SyncId).ToArray();
                    if (matches.Length != 1)
                        Fail(item, new EgressException(502, "CREATE_OUTCOME_UNKNOWN", "Missing or duplicate creation result.", retryable: true));
                    else
                    {
                        var result = matches[0];
                        item.NewDocumentId = result.DocumentId;
                        if (result.ErrorCode is null && result.DocumentId is { } id && id != Guid.Empty && id != item.OldDocumentId)
                            Advance(item, "Completed");
                        else
                            Fail(item, new EgressException(502, result.ErrorCode ?? "INVALID_CREATE_RESPONSE",
                                result.Error ?? "Invalid new document id.", retryable: result.Retryable));
                    }
                }
            }
            catch (EgressException exception) { foreach (var item in chunk) Fail(item, exception); }
            await SaveAsync(operation, cancellationToken);
        }
        await SaveAsync(operation, cancellationToken);
    }

    private async Task SaveAsync(SalesReturnOperation operation, CancellationToken cancellationToken)
    {
        operation.UpdatedAt = timeProvider.GetUtcNow();
        // Bounded automatic retries; the same authenticated request can explicitly resume later.
        operation.NextAttemptAt = operation.Items.Any(x => x.Pending) && operation.Attempts < 8
            ? operation.UpdatedAt.AddSeconds(Math.Min(300, 5 * Math.Pow(2, operation.Attempts))) : null;
        await repository.SaveAsync(operation, cancellationToken);
    }
    private static void Advance(SalesReturnOperationItem item, string stage)
    { item.Stage = stage; item.ErrorCode = null; item.Error = null; item.Retryable = stage != "Completed"; }
    private static void Fail(SalesReturnOperationItem item, EgressException exception)
    { item.ErrorCode = exception.Code; item.Error = exception.SafeMessage; item.Retryable = exception.Retryable; }
    private static RecreateSalesReturnsResponse Result(SalesReturnOperation operation) => new(operation.OperationId, operation.MainCounterpartyId,
        operation.Items.Select(x => new RecreateSalesReturnResult(x.OldDocumentId, x.NewDocumentId, x.Stage,
            x.Stage == "Completed" ? "Completed" : x.Pending ? "Pending" : "Failed", x.ErrorCode, x.Error, x.Pending)).ToArray());
    private static JsonNode Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x =>
            KeyValuePair.Create<string, JsonNode?>(x.Key, x.Value is null ? null : Canonical(x.Value)))),
        JsonArray array => new JsonArray(array.Select(x => x is null ? null : Canonical(x)).ToArray()),
        _ => node!.DeepClone()
    };
}
