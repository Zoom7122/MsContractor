using MsContractor.MoySkladEgressService.Gateways.Documents.Salesreturn;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Salesreturn;

public interface ISalesReturnRecreationOrchestrator
{
    Task<SalesReturnRecreationResult> RecreateAsync(
        Guid accountId,
        Guid mainAgentId,
        IReadOnlyCollection<Guid> salesReturnIds,
        CancellationToken cancellationToken);
}

public sealed class SalesReturnRecreationOrchestrator : ISalesReturnRecreationOrchestrator
{
    private readonly ISalesReturnRawDataRepository _documents;
    private readonly ISalesReturnPositionRawDataRepository _positions;
    private readonly IMoySkladSalesReturnServiceGetData _sourceDataLoader;
    private readonly IMoySkladSalesReturnPositionsService _positionsLoader;
    private readonly ISalesReturnRecreationOperationRepository _operations;
    private readonly IMoySkladSalesReturnGateway _gateway;
    private readonly SalesReturnRecreationPayloadBuilder _payloads;
    private readonly TimeProvider _timeProvider;
    private readonly ISalesReturnRelationsService _relations;

    public SalesReturnRecreationOrchestrator(
        ISalesReturnRawDataRepository documents,
        ISalesReturnPositionRawDataRepository positions,
        IMoySkladSalesReturnServiceGetData sourceDataLoader,
        IMoySkladSalesReturnPositionsService positionsLoader,
        ISalesReturnRecreationOperationRepository operations,
        IMoySkladSalesReturnGateway gateway,
        SalesReturnRecreationPayloadBuilder payloads,
        TimeProvider timeProvider,
        ISalesReturnRelationsService relations)
    {
        _documents = documents;
        _positions = positions;
        _sourceDataLoader = sourceDataLoader;
        _positionsLoader = positionsLoader;
        _operations = operations;
        _gateway = gateway;
        _payloads = payloads;
        _timeProvider = timeProvider;
        _relations = relations;
    }

    public async Task<SalesReturnRecreationResult> RecreateAsync(
        Guid accountId,
        Guid mainAgentId,
        IReadOnlyCollection<Guid> salesReturnIds,
        CancellationToken cancellationToken)
    {
        ValidateInput(accountId, mainAgentId, salesReturnIds);
        cancellationToken.ThrowIfCancellationRequested();

        var ids = salesReturnIds.ToArray();
        var operationId = Guid.NewGuid();
        var correlationId = operationId.ToString("D");
        await LoadSourceDataAsync(accountId, correlationId, ids, cancellationToken);
        var sourceDocuments = await _documents.GetRequiredAsync(accountId, ids, cancellationToken);
        var sourcePositions = await _positions.GetRequiredAsync(accountId, ids, cancellationToken);
        var agentAccounts = await _gateway.GetAgentAccountsAsync(
            accountId, mainAgentId, correlationId, cancellationToken);
        var targetAgentAccountId = SelectAgentAccount(agentAccounts);
        var now = _timeProvider.GetUtcNow();
        var operation = new SalesReturnRecreationOperation
        {
            AccountId = accountId,
            OperationId = operationId,
            MainAgentId = mainAgentId,
            Status = "Prepared",
            CreatedAt = now,
            UpdatedAt = now
        };

        foreach (var documentId in ids)
        {
            var source = sourceDocuments[documentId];
            var positions = sourcePositions[documentId];
            _payloads.ValidateSource(source, positions);
            var newSyncId = Guid.NewGuid();
            operation.Items.Add(new SalesReturnRecreationItem
            {
                AccountId = accountId,
                OperationId = operationId,
                SourceDocumentId = documentId,
                DemandId = _payloads.ReadDemandId(source),
                TargetAgentAccountId = targetAgentAccountId,
                NewSyncId = newSyncId,
                SourceRawJson = source,
                NewPayloadJson = _payloads.BuildNewPayload(
                    source, positions, mainAgentId, targetAgentAccountId, newSyncId),
                Stage = "Prepared"
            });
        }

        await _operations.CreateAsync(operation, cancellationToken);
        await _relations.PrepareAndDetachAsync(operation, correlationId, cancellationToken);
        await DeleteSourcesAsync(operation, correlationId, cancellationToken);
        await CreateNewDocumentsAsync(operation, correlationId, cancellationToken);
        await _relations.ReattachAsync(operation, correlationId, cancellationToken);

        operation.Status = operation.Items.All(item => item.Stage == "Completed")
            ? "Completed"
            : "Failed";
        await _operations.SaveAsync(operation, cancellationToken);
        return Result(operation);
    }

    private async Task LoadSourceDataAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<Guid> salesReturnIds,
        CancellationToken cancellationToken)
    {
        await _sourceDataLoader.LoadAsync(
            accountId,
            Guid.Empty,
            correlationId,
            salesReturnIds,
            cancellationToken);
        await _positionsLoader.LoadAsync(
            accountId,
            Guid.Empty,
            correlationId,
            salesReturnIds,
            cancellationToken);
    }

    private async Task DeleteSourcesAsync(
        SalesReturnRecreationOperation operation,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var deletableItems = operation.Items
            .Where(item => item.Stage == "Prepared" && item.RelationsStatus == "RelationsDetached")
            .ToArray();
        foreach (var chunk in deletableItems.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var results = await _gateway.DeleteBatchAsync(
                    operation.AccountId, correlationId, chunk.Select(item => item.SourceDocumentId).ToArray(), cancellationToken);
                foreach (var item in chunk)
                {
                    var result = results.Single(result => result.DocumentId == item.SourceDocumentId);
                    if (result.Succeeded)
                        item.Stage = "Deleted";
                    else
                        Fail(item, "SALESRETURN_DELETE_FAILED", result.Error ?? "MoySklad rejected source deletion.");
                }
            }
            catch (Exception exception) when (exception is EgressException or InvalidOperationException)
            {
                foreach (var item in chunk)
                    Fail(item, "SALESRETURN_DELETE_FAILED", exception.Message);
            }
            await _operations.SaveAsync(operation, cancellationToken);
        }
    }

    private async Task CreateNewDocumentsAsync(
        SalesReturnRecreationOperation operation,
        string correlationId,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in operation.Items.Where(item => item.Stage == "Deleted").Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var results = await _gateway.CreateBatchAsync(
                    operation.AccountId,
                    correlationId,
                    chunk.Select(item => new MoySkladSalesReturnBatchCreateItem(
                        item.SourceDocumentId, item.NewSyncId, item.NewPayloadJson)).ToArray(),
                    cancellationToken);
                foreach (var item in chunk)
                {
                    var result = results.Single(result => result.SourceDocumentId == item.SourceDocumentId);
                    item.NewDocumentId = result.DocumentId;
                    if (result.ErrorCode is not null)
                    {
                        Fail(item, "SALESRETURN_NEW_CREATE_FAILED", result.Error ?? result.ErrorCode);
                        item.Stage = "Failed";
                    }
                    else if (result.DocumentId is null)
                    {
                        Fail(item, "SALESRETURN_NEW_CREATE_FAILED",
                            result.Error ?? "MoySklad did not return the created salesreturn id.");
                        item.Stage = "Failed";
                    }
                    else
                    {
                        item.Stage = "Created";
                        item.ErrorCode = null;
                        item.Error = null;
                    }
                }
            }
            catch (Exception exception) when (exception is EgressException or InvalidOperationException)
            {
                foreach (var item in chunk)
                {
                    Fail(item, "SALESRETURN_NEW_CREATE_FAILED", exception.Message);
                    item.Stage = "Failed";
                }
            }
            await _operations.SaveAsync(operation, cancellationToken);
        }
    }

    private static Guid? SelectAgentAccount(IReadOnlyList<MoySkladSalesReturnAgentAccount> accounts)
    {
        var defaults = accounts.Where(account => account.IsDefault).ToArray();
        if (defaults.Length == 1)
            return defaults[0].Id;
        return accounts.Count == 1 ? accounts[0].Id : null;
    }

    private static void ValidateInput(Guid accountId, Guid mainAgentId, IReadOnlyCollection<Guid> salesReturnIds)
    {
        if (accountId == Guid.Empty || mainAgentId == Guid.Empty || salesReturnIds.Count == 0 ||
            salesReturnIds.Count > 100_000 || salesReturnIds.Any(id => id == Guid.Empty) ||
            salesReturnIds.Distinct().Count() != salesReturnIds.Count)
            throw new ArgumentException("A non-empty account, main agent and unique salesreturn ids are required.");
    }

    private static void Fail(SalesReturnRecreationItem item, string code, string error)
    {
        item.ErrorCode = code;
        item.Error = error;
    }

    private static SalesReturnRecreationResult Result(SalesReturnRecreationOperation operation) => new(
        operation.OperationId,
        operation.MainAgentId,
        operation.Items.Select(item => new SalesReturnRecreationDocumentResult(
            item.SourceDocumentId,
            item.NewDocumentId,
            item.Stage,
            item.Stage == "Completed" ? "Completed" : "Failed",
            item.ErrorCode,
            item.Error)).ToArray());

    private const int BatchSize = 1000;
}
