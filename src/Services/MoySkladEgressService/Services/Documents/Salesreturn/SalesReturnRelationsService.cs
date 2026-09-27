using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MsContractor.MoySkladEgressService.Gateways.Documents.Salesreturn;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Salesreturn;

public interface ISalesReturnRelationsService
{
    Task PrepareAndDetachAsync(
        SalesReturnRecreationOperation operation,
        string correlationId,
        CancellationToken cancellationToken);

    Task ReattachAsync(
        SalesReturnRecreationOperation operation,
        string correlationId,
        CancellationToken cancellationToken);
}

public sealed class SalesReturnRelationsService : ISalesReturnRelationsService
{
    private const string RelationsDetached = "RelationsDetached";
    private const string RelationsRestored = "RelationsRestored";
    private const string RelationsReattached = "RelationsReattached";
    private const string RelationsSkipped = "Skipped";
    private const string RelationsFailed = "Failed";

    private readonly IMoySkladSalesReturnRelationsGateway _gateway;
    private readonly ISalesReturnRelationsRepository _repository;
    private readonly ILogger<SalesReturnRelationsService> _logger;
    private readonly Dictionary<Guid, RelationState> _states = [];

    public SalesReturnRelationsService(
        IMoySkladSalesReturnRelationsGateway gateway,
        ISalesReturnRelationsRepository repository,
        ILogger<SalesReturnRelationsService> logger)
    {
        _gateway = gateway;
        _repository = repository;
        _logger = logger;
    }

    public async Task PrepareAndDetachAsync(
        SalesReturnRecreationOperation operation,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var states = new List<RelationState>();
        foreach (var item in operation.Items.Where(item => item.Stage == "Prepared"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var snapshot = await LoadSnapshotAsync(
                    operation.AccountId, item.SourceDocumentId, correlationId, cancellationToken);
                states.Add(new RelationState(item, snapshot));
                _logger.LogInformation(
                    "Salesreturn relations loaded: operation_id={OperationId}, account_id={AccountId}, source_salesreturn_id={SourceSalesReturnId}, paymentout_count={PaymentOutCount}, cashout_count={CashOutCount}, loss_count={LossCount}",
                    operation.OperationId,
                    operation.AccountId,
                    item.SourceDocumentId,
                    snapshot.PaymentOuts.Count,
                    snapshot.CashOuts.Count,
                    snapshot.Losses.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Salesreturn relations load failed: operation_id={OperationId}, account_id={AccountId}, source_salesreturn_id={SourceSalesReturnId}",
                    operation.OperationId,
                    operation.AccountId,
                    item.SourceDocumentId);
                states.Add(new RelationState(
                    item,
                    new SalesReturnRelationsSnapshot { SourceSalesReturnId = item.SourceDocumentId },
                    "SALESRETURN_RELATIONS_LOAD_FAILED",
                    exception.Message));
            }
        }

        _states.Clear();
        foreach (var state in states)
            _states[state.Item.SourceDocumentId] = state;

        await _repository.CreateSnapshotsAsync(
            operation.AccountId,
            operation.OperationId,
            states.Select(state => state.Snapshot).ToArray(),
            cancellationToken);

        foreach (var state in states.Where(state => state.HasFailure))
        {
            await MarkSkippedAsync(
                operation,
                state,
                state.ErrorCode!,
                state.Error!,
                cancellationToken);
        }

        var validStates = states.Where(state => !state.HasFailure).ToArray();
        await DetachMoneyAsync(operation, validStates, SalesReturnRelationDocumentType.PaymentOut, correlationId, cancellationToken);
        await DetachMoneyAsync(operation, validStates, SalesReturnRelationDocumentType.CashOut, correlationId, cancellationToken);
        await DetachLossesAsync(operation, validStates, correlationId, cancellationToken);

        foreach (var state in validStates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!state.HasFailure)
            {
                state.Item.RelationsStatus = RelationsDetached;
                await _repository.UpdateSourceStatusAsync(
                    operation.AccountId,
                    operation.OperationId,
                    state.Item.SourceDocumentId,
                    RelationsDetached,
                    null,
                    null,
                    cancellationToken);
                _logger.LogInformation(
                    "Salesreturn relations detached: operation_id={OperationId}, account_id={AccountId}, source_salesreturn_id={SourceSalesReturnId}",
                    operation.OperationId,
                    operation.AccountId,
                    state.Item.SourceDocumentId);
                continue;
            }

            var rollbackSucceeded = await RollbackAsync(operation, state, correlationId, cancellationToken);
            if (rollbackSucceeded)
            {
                _logger.LogWarning(
                    "Salesreturn relations detach rolled back: operation_id={OperationId}, account_id={AccountId}, source_salesreturn_id={SourceSalesReturnId}, error_code={ErrorCode}",
                    operation.OperationId,
                    operation.AccountId,
                    state.Item.SourceDocumentId,
                    state.ErrorCode);
                await MarkSkippedAsync(
                    operation,
                    state,
                    state.ErrorCode ?? "SALESRETURN_RELATIONS_DETACH_FAILED",
                    state.Error ?? "One or more related documents could not be detached.",
                    cancellationToken,
                    RelationsRestored);
            }
            else
            {
                _logger.LogError(
                    "Salesreturn relations rollback failed: operation_id={OperationId}, account_id={AccountId}, source_salesreturn_id={SourceSalesReturnId}, error_code={ErrorCode}",
                    operation.OperationId,
                    operation.AccountId,
                    state.Item.SourceDocumentId,
                    state.ErrorCode);
                state.Item.RelationsStatus = RelationsFailed;
                state.Item.Stage = "Failed";
                SetError(
                    state.Item,
                    "SALESRETURN_RELATIONS_ROLLBACK_FAILED",
                    "Related documents were detached partially and could not be fully restored.");
                await _repository.UpdateSourceStatusAsync(
                    operation.AccountId,
                    operation.OperationId,
                    state.Item.SourceDocumentId,
                    RelationsFailed,
                    state.Item.ErrorCode,
                    state.Item.Error,
                    cancellationToken);
            }
        }
    }

    public async Task ReattachAsync(
        SalesReturnRecreationOperation operation,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var states = operation.Items
            .Where(item => item.Stage == "Created" && item.RelationsStatus == RelationsDetached)
            .Select(item => _states.TryGetValue(item.SourceDocumentId, out var state)
                ? state
                : throw new InvalidOperationException(
                    $"Relations snapshot is missing for salesreturn {item.SourceDocumentId:D}."))
            .ToArray();

        await ReattachMoneyAsync(operation, states, SalesReturnRelationDocumentType.PaymentOut, correlationId, cancellationToken);
        await ReattachMoneyAsync(operation, states, SalesReturnRelationDocumentType.CashOut, correlationId, cancellationToken);
        await ReattachLossesAsync(operation, states, correlationId, cancellationToken);

        foreach (var state in states)
        {
            if (state.HasFailure)
            {
                state.Item.RelationsStatus = RelationsFailed;
                state.Item.Stage = "Failed";
                if (state.Item.ErrorCode is null)
                    SetError(state.Item, "SALESRETURN_RELATIONS_REATTACH_FAILED", state.Error ?? "A related document could not be reattached.");
                await _repository.UpdateSourceStatusAsync(
                    operation.AccountId,
                    operation.OperationId,
                    state.Item.SourceDocumentId,
                    RelationsFailed,
                    state.Item.ErrorCode,
                    state.Item.Error,
                    cancellationToken);
            }
            else
            {
                state.Item.RelationsStatus = RelationsReattached;
                state.Item.Stage = "Completed";
                state.Item.ErrorCode = null;
                state.Item.Error = null;
                await _repository.UpdateSourceStatusAsync(
                    operation.AccountId,
                    operation.OperationId,
                    state.Item.SourceDocumentId,
                    RelationsReattached,
                    null,
                    null,
                    cancellationToken);
                _logger.LogInformation(
                    "Salesreturn relations reattached: operation_id={OperationId}, account_id={AccountId}, source_salesreturn_id={SourceSalesReturnId}, new_salesreturn_id={NewSalesReturnId}",
                    operation.OperationId,
                    operation.AccountId,
                    state.Item.SourceDocumentId,
                    state.Item.NewDocumentId);
            }
        }
    }

    private async Task<SalesReturnRelationsSnapshot> LoadSnapshotAsync(
        Guid accountId,
        Guid sourceSalesReturnId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var raw = await _gateway.GetSalesReturnRelationsAsync(
            accountId, sourceSalesReturnId, correlationId, cancellationToken);
        var source = ParseObject(raw, "salesreturn");
        var sourceId = TryReadId(source);
        if (sourceId is not null && sourceId != sourceSalesReturnId)
            throw new InvalidOperationException("MoySklad returned a different salesreturn.");

        var snapshot = new SalesReturnRelationsSnapshot { SourceSalesReturnId = sourceSalesReturnId };
        var paymentIds = new HashSet<Guid>();
        var cashOutIds = new HashSet<Guid>();
        var lossIds = new HashSet<Guid>();

        // MoySklad may omit payments when the salesreturn has no linked payment documents.
        foreach (var link in ReadOptionalCollection(source, "payments"))
        {
            var type = ReadMetaType(link);
            if (!string.Equals(type, "paymentout", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(type, "cashout", StringComparison.OrdinalIgnoreCase))
                continue;
            var relationType = type!;

            var documentId = TryReadId(link) ?? throw new InvalidOperationException("A related payment has no valid id.");
            var full = HasArrayProperty(link, "operations")
                ? link
                : ParseObject(
                    string.Equals(relationType, "paymentout", StringComparison.OrdinalIgnoreCase)
                        ? await _gateway.GetPaymentOutAsync(accountId, documentId, correlationId, cancellationToken)
                        : await _gateway.GetCashOutAsync(accountId, documentId, correlationId, cancellationToken),
                    relationType);
            ValidateDocumentIdAndType(full, documentId, relationType);

            var operations = ReadRequiredArray(full, "operations");
            

            var matching = operations
                .Where(operation => IsSalesReturnReference(operation, sourceSalesReturnId))
                .ToArray();

            if (matching.Length == 0)
                throw new InvalidOperationException(
                    $"Related {type} {documentId:D} does not contain an operation for salesreturn {sourceSalesReturnId:D}.");

            var linkedSum = 0m;
            for (var index = 0; index < matching.Length; index++)
            {
                if (!TryReadDecimal(matching[index], "linkedSum", out var matchingLinkedSum))
                    throw new InvalidOperationException(
                        $"Related {type} {documentId:D} has no valid linkedSum on its salesreturn operation.");

                if (index == 0)
                    linkedSum = matchingLinkedSum;
            }

            var operationSnapshot = operations.Select(item => item.Clone()).ToArray();
            if (string.Equals(relationType, "paymentout", StringComparison.OrdinalIgnoreCase))
            {
                if (!paymentIds.Add(documentId))
                    throw new InvalidOperationException($"Duplicate paymentout relation {documentId:D} was returned.");
                snapshot.PaymentOuts.Add(new PaymentOutRelationSnapshot
                {
                    DocumentId = documentId,
                    OperationsBefore = operationSnapshot,
                    LinkedSum = linkedSum
                });
            }
            else
            {
                if (!cashOutIds.Add(documentId))
                    throw new InvalidOperationException($"Duplicate cashout relation {documentId:D} was returned.");
                snapshot.CashOuts.Add(new CashOutRelationSnapshot
                {
                    DocumentId = documentId,
                    OperationsBefore = operationSnapshot,
                    LinkedSum = linkedSum
                });
            }
        }

        // MoySklad may omit losses when the salesreturn has no linked loss documents.
        foreach (var link in ReadOptionalCollection(source, "losses"))
        {
            if (!string.Equals(ReadMetaType(link), "loss", StringComparison.OrdinalIgnoreCase))
                continue;

            var documentId = TryReadId(link) ?? throw new InvalidOperationException("A related loss has no valid id.");
            var full = link.TryGetProperty("salesReturn", out _)
                ? link
                : ParseObject(
                    await _gateway.GetLossAsync(accountId, documentId, correlationId, cancellationToken),
                    "loss");
            ValidateDocumentIdAndType(full, documentId, "loss");
            if (!full.TryGetProperty("salesReturn", out var salesReturn) ||
                salesReturn.ValueKind == JsonValueKind.Null ||
                !IsSalesReturnLink(salesReturn, sourceSalesReturnId))
                throw new InvalidOperationException(
                    $"Related loss {documentId:D} does not reference salesreturn {sourceSalesReturnId:D}: " +
                    (full.TryGetProperty("salesReturn", out var actualSalesReturn)
                        ? actualSalesReturn.GetRawText()
                        : "salesReturn is missing"));
            if (!lossIds.Add(documentId))
                throw new InvalidOperationException($"Duplicate loss relation {documentId:D} was returned.");

            snapshot.Losses.Add(new LossRelationSnapshot
            {
                DocumentId = documentId,
                SalesReturnBefore = salesReturn.Clone()
            });
        }

        return snapshot;
    }

    private async Task DetachMoneyAsync(
        SalesReturnRecreationOperation operation,
        IReadOnlyCollection<RelationState> states,
        SalesReturnRelationDocumentType documentType,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var work = states.SelectMany(state => documentType switch
        {
            SalesReturnRelationDocumentType.PaymentOut => state.Snapshot.PaymentOuts.Select(snapshot =>
                RelationWorkItem.ForPayment(state, snapshot, BuildMoneyPayload("paymentout", snapshot.DocumentId,
                    snapshot.OperationsBefore.Where(item => !IsSalesReturnReference(item, state.Item.SourceDocumentId)).ToArray()))),
            SalesReturnRelationDocumentType.CashOut => state.Snapshot.CashOuts.Select(snapshot =>
                RelationWorkItem.ForCashOut(state, snapshot, BuildMoneyPayload("cashout", snapshot.DocumentId,
                    snapshot.OperationsBefore.Where(item => !IsSalesReturnReference(item, state.Item.SourceDocumentId)).ToArray()))),
            _ => throw new ArgumentOutOfRangeException(nameof(documentType), documentType, null)
        }).ToArray();

        foreach (var chunk in work.Chunk(MoySkladSalesReturnRelationsGateway.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var results = documentType == SalesReturnRelationDocumentType.PaymentOut
                    ? await _gateway.PaymentOutBatchAsync(operation.AccountId, correlationId,
                        chunk.Select(item => new MoySkladRelationBatchItem(item.DocumentId, item.PayloadJson)).ToArray(), cancellationToken)
                    : await _gateway.CashOutBatchAsync(operation.AccountId, correlationId,
                        chunk.Select(item => new MoySkladRelationBatchItem(item.DocumentId, item.PayloadJson)).ToArray(), cancellationToken);
                var byId = results.ToDictionary(item => item.DocumentId);
                foreach (var item in chunk)
                {
                    if (!byId.TryGetValue(item.DocumentId, out var result))
                    {
                        MarkFailure(item.State, "SALESRETURN_RELATIONS_DETACH_FAILED", "The detach batch response omitted a related document.");
                        continue;
                    }
                    if (!result.Succeeded)
                    {
                        MarkFailure(item.State, result.ErrorCode ?? "SALESRETURN_RELATIONS_DETACH_FAILED",
                            result.Error ?? "MoySklad rejected relation detachment.");
                        await SaveDocumentFailureAsync(operation, item, result.ErrorCode, result.Error, cancellationToken);
                        continue;
                    }
                    if (!VerifyDetached(result.RawJson, stateId: item.State.Item.SourceDocumentId, documentType))
                    {
                        MarkFailure(item.State, "SALESRETURN_RELATIONS_DETACH_VERIFY_FAILED", "The old salesreturn link is still present after detach.");
                        await SaveDocumentFailureAsync(operation, item, item.State.ErrorCode, item.State.Error, cancellationToken);
                        continue;
                    }

                    item.State.Detached.Add(item);
                    await SaveDocumentStatusAsync(operation, item, "RelationsDetached", "Pending", null, null, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                foreach (var item in chunk)
                    MarkFailure(item.State, "SALESRETURN_RELATIONS_DETACH_FAILED", exception.Message);
            }
        }
    }

    private async Task DetachLossesAsync(
        SalesReturnRecreationOperation operation,
        IReadOnlyCollection<RelationState> states,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var work = states.SelectMany(state => state.Snapshot.Losses.Select(snapshot =>
            RelationWorkItem.ForLoss(state, snapshot, BuildLossPayload(snapshot.DocumentId, (Guid?)null)))).ToArray();
        foreach (var chunk in work.Chunk(MoySkladSalesReturnRelationsGateway.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var results = await _gateway.LossBatchAsync(
                    operation.AccountId,
                    correlationId,
                    chunk.Select(item => new MoySkladRelationBatchItem(item.DocumentId, item.PayloadJson)).ToArray(),
                    cancellationToken);
                var byId = results.ToDictionary(item => item.DocumentId);
                foreach (var item in chunk)
                {
                    if (!byId.TryGetValue(item.DocumentId, out var result))
                    {
                        MarkFailure(item.State, "SALESRETURN_RELATIONS_DETACH_FAILED", "The detach batch response omitted a related loss.");
                        continue;
                    }
                    if (!result.Succeeded)
                    {
                        MarkFailure(item.State, result.ErrorCode ?? "SALESRETURN_RELATIONS_DETACH_FAILED",
                            result.Error ?? "MoySklad rejected relation detachment.");
                        await SaveDocumentFailureAsync(operation, item, result.ErrorCode, result.Error, cancellationToken);
                        continue;
                    }
                    if (!VerifyDetached(result.RawJson, stateId: item.State.Item.SourceDocumentId, SalesReturnRelationDocumentType.Loss))
                    {
                        MarkFailure(item.State, "SALESRETURN_RELATIONS_DETACH_VERIFY_FAILED", "The old salesreturn link is still present after loss detach.");
                        await SaveDocumentFailureAsync(operation, item, item.State.ErrorCode, item.State.Error, cancellationToken);
                        continue;
                    }

                    item.State.Detached.Add(item);
                    await SaveDocumentStatusAsync(operation, item, "RelationsDetached", "Pending", null, null, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                foreach (var item in chunk)
                    MarkFailure(item.State, "SALESRETURN_RELATIONS_DETACH_FAILED", exception.Message);
            }
        }
    }

    private async Task<bool> RollbackAsync(
        SalesReturnRecreationOperation operation,
        RelationState state,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var succeeded = true;
        foreach (var type in new[]
                 {
                     SalesReturnRelationDocumentType.PaymentOut,
                     SalesReturnRelationDocumentType.CashOut,
                     SalesReturnRelationDocumentType.Loss
                 })
        {
            var work = state.Detached.Where(item => item.DocumentType == type).ToArray();
            foreach (var chunk in work.Chunk(MoySkladSalesReturnRelationsGateway.BatchSize))
            {
                try
                {
                    var request = chunk.Select(item => new MoySkladRelationBatchItem(
                        item.DocumentId,
                        item.BuildRollbackPayload())).ToArray();
                    var results = type switch
                    {
                        SalesReturnRelationDocumentType.PaymentOut => await _gateway.PaymentOutBatchAsync(operation.AccountId, correlationId, request, cancellationToken),
                        SalesReturnRelationDocumentType.CashOut => await _gateway.CashOutBatchAsync(operation.AccountId, correlationId, request, cancellationToken),
                        SalesReturnRelationDocumentType.Loss => await _gateway.LossBatchAsync(operation.AccountId, correlationId, request, cancellationToken),
                        _ => throw new ArgumentOutOfRangeException()
                    };
                    var byId = results.ToDictionary(item => item.DocumentId);
                    foreach (var item in chunk)
                    {
                        if (!byId.TryGetValue(item.DocumentId, out var result) || !result.Succeeded ||
                            !VerifyRestored(result.RawJson, state.Item.SourceDocumentId, item))
                        {
                            succeeded = false;
                            MarkFailure(state, "SALESRETURN_RELATIONS_ROLLBACK_FAILED", "MoySklad did not restore a detached relation.");
                            continue;
                        }

                        await SaveDocumentStatusAsync(operation, item, "RelationsRestored", "Pending", null, null, cancellationToken);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    succeeded = false;
                    MarkFailure(state, "SALESRETURN_RELATIONS_ROLLBACK_FAILED", exception.Message);
                }
            }
        }

        return succeeded;
    }

    private async Task ReattachMoneyAsync(
        SalesReturnRecreationOperation operation,
        IReadOnlyCollection<RelationState> states,
        SalesReturnRelationDocumentType documentType,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var work = new List<RelationWorkItem>();
        foreach (var state in states)
        {
            try
            {
                if (documentType == SalesReturnRelationDocumentType.PaymentOut)
                {
                    work.AddRange(state.Snapshot.PaymentOuts.Select(snapshot =>
                        RelationWorkItem.ForPayment(state, snapshot, BuildReattachMoneyPayload(
                            "paymentout", snapshot, state.Item.NewDocumentId!.Value, state.Item.SourceDocumentId))));
                }
                else if (documentType == SalesReturnRelationDocumentType.CashOut)
                {
                    work.AddRange(state.Snapshot.CashOuts.Select(snapshot =>
                        RelationWorkItem.ForCashOut(state, snapshot, BuildReattachMoneyPayload(
                            "cashout", snapshot, state.Item.NewDocumentId!.Value, state.Item.SourceDocumentId))));
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(documentType), documentType, null);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                MarkFailure(state, "SALESRETURN_RELATIONS_REATTACH_FAILED", exception.Message);
            }
        }

        foreach (var chunk in work.Chunk(MoySkladSalesReturnRelationsGateway.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var request = chunk.Select(item => new MoySkladRelationBatchItem(item.DocumentId, item.PayloadJson)).ToArray();
                var results = documentType == SalesReturnRelationDocumentType.PaymentOut
                    ? await _gateway.PaymentOutBatchAsync(operation.AccountId, correlationId, request, cancellationToken)
                    : await _gateway.CashOutBatchAsync(operation.AccountId, correlationId, request, cancellationToken);
                var byId = results.ToDictionary(item => item.DocumentId);
                foreach (var item in chunk)
                {
                    if (!byId.TryGetValue(item.DocumentId, out var result) || !result.Succeeded ||
                        !VerifyReattached(result.RawJson, item.State.Item.NewDocumentId!.Value, item))
                    {
                        MarkFailure(item.State, "SALESRETURN_RELATIONS_REATTACH_FAILED", "MoySklad did not restore the new salesreturn relation.");
                        await SaveDocumentFailureAsync(operation, item, item.State.ErrorCode, item.State.Error, cancellationToken);
                        continue;
                    }

                    await SaveDocumentStatusAsync(operation, item, "RelationsDetached", RelationsReattached, null, null, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                foreach (var item in chunk)
                    MarkFailure(item.State, "SALESRETURN_RELATIONS_REATTACH_FAILED", exception.Message);
            }
        }
    }

    private async Task ReattachLossesAsync(
        SalesReturnRecreationOperation operation,
        IReadOnlyCollection<RelationState> states,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var work = new List<RelationWorkItem>();
        foreach (var state in states)
        {
            try
            {
                work.AddRange(state.Snapshot.Losses.Select(snapshot =>
                    RelationWorkItem.ForLoss(state, snapshot, BuildLossPayload(snapshot.DocumentId, state.Item.NewDocumentId!.Value))));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                MarkFailure(state, "SALESRETURN_RELATIONS_REATTACH_FAILED", exception.Message);
            }
        }
        foreach (var chunk in work.Chunk(MoySkladSalesReturnRelationsGateway.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var request = chunk.Select(item => new MoySkladRelationBatchItem(item.DocumentId, item.PayloadJson)).ToArray();
                var results = await _gateway.LossBatchAsync(operation.AccountId, correlationId, request, cancellationToken);
                var byId = results.ToDictionary(item => item.DocumentId);
                foreach (var item in chunk)
                {
                    if (!byId.TryGetValue(item.DocumentId, out var result) || !result.Succeeded ||
                        !VerifyReattached(result.RawJson, item.State.Item.NewDocumentId!.Value, item))
                    {
                        MarkFailure(item.State, "SALESRETURN_RELATIONS_REATTACH_FAILED", "MoySklad did not restore the new loss relation.");
                        await SaveDocumentFailureAsync(operation, item, item.State.ErrorCode, item.State.Error, cancellationToken);
                        continue;
                    }

                    await SaveDocumentStatusAsync(operation, item, "RelationsDetached", RelationsReattached, null, null, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                foreach (var item in chunk)
                    MarkFailure(item.State, "SALESRETURN_RELATIONS_REATTACH_FAILED", exception.Message);
            }
        }
    }

    private async Task MarkSkippedAsync(
        SalesReturnRecreationOperation operation,
        RelationState state,
        string errorCode,
        string error,
        CancellationToken cancellationToken,
        string status = RelationsSkipped)
    {
        state.Item.RelationsStatus = status;
        state.Item.Stage = "Skipped";
        SetError(state.Item, errorCode, error);
        await _repository.UpdateSourceStatusAsync(
            operation.AccountId,
            operation.OperationId,
            state.Item.SourceDocumentId,
            status,
            errorCode,
            error,
            cancellationToken);
    }

    private async Task SaveDocumentFailureAsync(
        SalesReturnRecreationOperation operation,
        RelationWorkItem item,
        string? errorCode,
        string? error,
        CancellationToken cancellationToken)
    {
        await _repository.UpdateDocumentStatusAsync(
            operation.AccountId,
            operation.OperationId,
            item.State.Item.SourceDocumentId,
            item.DocumentType,
            item.DocumentId,
            "Failed",
            item.State.Item.RelationsStatus,
            errorCode,
            error,
            cancellationToken);
    }

    private async Task SaveDocumentStatusAsync(
        SalesReturnRecreationOperation operation,
        RelationWorkItem item,
        string detachStatus,
        string reattachStatus,
        string? errorCode,
        string? error,
        CancellationToken cancellationToken)
    {
        await _repository.UpdateDocumentStatusAsync(
            operation.AccountId,
            operation.OperationId,
            item.State.Item.SourceDocumentId,
            item.DocumentType,
            item.DocumentId,
            detachStatus,
            reattachStatus,
            errorCode,
            error,
            cancellationToken);
    }

    private static string BuildMoneyPayload(string type, Guid documentId, IReadOnlyCollection<JsonElement> operations)
    {
        var operationsNode = new JsonArray();
        foreach (var operation in operations)
            operationsNode.Add(JsonNode.Parse(operation.GetRawText()));
        var payload = new JsonObject
        {
            ["meta"] = BuildMeta(type, documentId),
            ["operations"] = operationsNode
        };
        return payload.ToJsonString();
    }

    private static string BuildReattachMoneyPayload(
        string type,
        PaymentOutRelationSnapshot snapshot,
        Guid newSalesReturnId,
        Guid oldSalesReturnId) => BuildReattachMoneyPayloadCore(type, snapshot.DocumentId, snapshot.OperationsBefore, newSalesReturnId, oldSalesReturnId);

    private static string BuildReattachMoneyPayload(
        string type,
        CashOutRelationSnapshot snapshot,
        Guid newSalesReturnId,
        Guid oldSalesReturnId) => BuildReattachMoneyPayloadCore(type, snapshot.DocumentId, snapshot.OperationsBefore, newSalesReturnId, oldSalesReturnId);

    private static string BuildReattachMoneyPayloadCore(
        string type,
        Guid documentId,
        IReadOnlyCollection<JsonElement> operationsBefore,
        Guid newSalesReturnId,
        Guid oldSalesReturnId)
    {
        var operationsNode = new JsonArray();
        var matches = 0;
        foreach (var operation in operationsBefore)
        {
            var node = JsonNode.Parse(operation.GetRawText())?.AsObject()
                ?? throw new InvalidOperationException("A saved money operation is not an object.");
            if (IsSalesReturnReference(operation, oldSalesReturnId))
            {
                matches++;
                node["meta"] = BuildMeta("salesreturn", newSalesReturnId);
            }
            operationsNode.Add(node);
        }

        if (matches == 0)
            throw new InvalidOperationException("The saved money operations do not contain a source salesreturn relation.");

        return new JsonObject
        {
            ["meta"] = BuildMeta(type, documentId),
            ["operations"] = operationsNode
        }.ToJsonString();
    }

    private static string BuildLossPayload(Guid documentId, Guid? salesReturnId)
    {
        var payload = new JsonObject
        {
            ["meta"] = BuildMeta("loss", documentId)
        };
        payload["salesReturn"] = salesReturnId is null
            ? null
            : BuildReference("salesreturn", salesReturnId.Value);
        return payload.ToJsonString();
    }

    private static string BuildLossPayload(Guid documentId, JsonElement? salesReturnBefore)
    {
        var payload = new JsonObject
        {
            ["meta"] = BuildMeta("loss", documentId),
            ["salesReturn"] = salesReturnBefore is null
                ? null
                : JsonNode.Parse(salesReturnBefore.Value.GetRawText())
        };
        return payload.ToJsonString();
    }

    private static JsonObject BuildMeta(string type, Guid id) => new()
    {
        ["href"] = $"https://api.moysklad.ru/api/remap/1.2/entity/{type}/{id:D}",
        ["type"] = type,
        ["mediaType"] = "application/json"
    };

    private static JsonObject BuildReference(string type, Guid id) => new()
    {
        ["meta"] = BuildMeta(type, id)
    };

    private static bool VerifyDetached(string rawJson, Guid stateId, SalesReturnRelationDocumentType documentType)
    {
        var root = ParseObject(rawJson, "relation batch response");
        if (documentType == SalesReturnRelationDocumentType.Loss)
        {
            // MoySklad omits a nullable salesReturn field when the relation is removed.
            return !root.TryGetProperty("salesReturn", out var salesReturn) ||
                   salesReturn.ValueKind == JsonValueKind.Null;
        }

        // MoySklad omits an empty operations collection after the last operation is removed.
        return !root.TryGetProperty("operations", out var operations) ||
               (operations.ValueKind == JsonValueKind.Array &&
                !operations.EnumerateArray().Any(item => IsSalesReturnReference(item, stateId)));
    }

    private static bool VerifyRestored(string rawJson, Guid sourceSalesReturnId, RelationWorkItem item)
    {
        if (item.DocumentType != SalesReturnRelationDocumentType.Loss)
            return VerifyMoneyReference(rawJson, sourceSalesReturnId);

        var loss = ParseObject(rawJson, "relation rollback response");
        return loss.TryGetProperty("salesReturn", out var salesReturn) &&
               IsSalesReturnLink(salesReturn, sourceSalesReturnId);
    }

    private static bool VerifyReattached(string rawJson, Guid newSalesReturnId, RelationWorkItem item)
    {
        if (item.DocumentType != SalesReturnRelationDocumentType.Loss)
            return VerifyMoneyReference(rawJson, newSalesReturnId);

        var loss = ParseObject(rawJson, "relation reattach response");
        return loss.TryGetProperty("salesReturn", out var salesReturn) &&
               IsSalesReturnLink(salesReturn, newSalesReturnId);
    }

    private static bool VerifyMoneyReference(string rawJson, Guid salesReturnId)
    {
        var root = ParseObject(rawJson, "money relation response");
        if (!root.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
            return false;
        var matching = operations.EnumerateArray()
            .Where(item => IsSalesReturnReference(item, salesReturnId))
            .ToArray();
        return matching.Length > 0;
    }

private static bool IsSalesReturnReference(
    JsonElement operation,
    Guid expectedSalesReturnId)
{
    if (operation.ValueKind != JsonValueKind.Object)
        return false;

    if (!operation.TryGetProperty("meta", out var meta) ||
        meta.ValueKind != JsonValueKind.Object)
    {
        return false;
    }

    if (!meta.TryGetProperty("type", out var typeElement) ||
        !string.Equals(
            typeElement.GetString(),
            "salesreturn",
            StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (!meta.TryGetProperty("href", out var hrefElement) ||
        hrefElement.ValueKind != JsonValueKind.String)
    {
        return false;
    }

        return TryExtractEntityId(
                hrefElement.GetString(),
                "salesreturn",
                out var actualSalesReturnId)
            && actualSalesReturnId == expectedSalesReturnId;
    }
    
    private static bool TryExtractEntityId(
    string? href,
    string entityType,
    out Guid id)
{
    id = Guid.Empty;

    if (string.IsNullOrWhiteSpace(href) ||
        !Uri.TryCreate(href, UriKind.Absolute, out var uri))
    {
        return false;
    }

    var segments = uri.AbsolutePath
        .Split('/', StringSplitOptions.RemoveEmptyEntries);

    var entityIndex = Array.FindLastIndex(
        segments,
        segment => string.Equals(
            segment,
            entityType,
            StringComparison.OrdinalIgnoreCase));

    return entityIndex >= 0 &&
           entityIndex + 1 < segments.Length &&
           Guid.TryParse(segments[entityIndex + 1], out id);
}

    private static bool IsSalesReturnLink(JsonElement reference, Guid salesReturnId) =>
        reference.ValueKind == JsonValueKind.Object &&
        string.Equals(ReadMetaType(reference), "salesreturn", StringComparison.OrdinalIgnoreCase) &&
        TryReadId(reference) == salesReturnId;

    private static bool TryReadDecimal(JsonElement element, string propertyName, out decimal value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out value))
            return true;
        if (property.ValueKind == JsonValueKind.String &&
            decimal.TryParse(property.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            return true;
        value = 0;
        return false;
    }

    private static JsonElement ParseObject(string raw, string documentName)
    {
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"MoySklad returned a non-object {documentName}.");
        return document.RootElement.Clone();
    }

    private static JsonElement[] ReadCollection(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
            throw new InvalidOperationException($"MoySklad response does not contain {propertyName}.");
        if (property.ValueKind == JsonValueKind.Array)
            return property.EnumerateArray().Select(item => item.Clone()).ToArray();
        if (property.ValueKind == JsonValueKind.Object && property.TryGetProperty("rows", out var rows) &&
            rows.ValueKind == JsonValueKind.Array)
            return rows.EnumerateArray().Select(item => item.Clone()).ToArray();
        throw new InvalidOperationException($"MoySklad response contains an invalid {propertyName} collection.");
    }

    private static JsonElement[] ReadOptionalCollection(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out _)
            ? ReadCollection(root, propertyName)
            : [];

    private static JsonElement[] ReadRequiredArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"MoySklad response does not contain an array {propertyName}.");
        return property.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static bool HasArrayProperty(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array;

    private static void ValidateDocumentIdAndType(JsonElement document, Guid expectedId, string expectedType)
    {
        if (TryReadId(document) != expectedId ||
            !string.Equals(ReadMetaType(document), expectedType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"MoySklad returned an invalid {expectedType} document.");
    }

    private static string? ReadMetaType(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("meta", out var meta) &&
        meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    private static Guid? TryReadId(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        if (element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
            Guid.TryParse(id.GetString(), out var parsedId))
            return parsedId;
        if (!element.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("href", out var href) || href.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(href.GetString(), UriKind.Absolute, out var uri))
            return null;
        return Guid.TryParse(uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault(), out var hrefId)
            ? hrefId
            : null;
    }

    private static void MarkFailure(RelationState state, string code, string error)
    {
        state.HasFailure = true;
        state.ErrorCode ??= code;
        state.Error ??= error;
        if (state.Item.ErrorCode is null)
            SetError(state.Item, code, error);
    }

    private static void SetError(SalesReturnRecreationItem item, string code, string error)
    {
        item.ErrorCode = code;
        item.Error = error;
    }

    private sealed class RelationState
    {
        public RelationState(
            SalesReturnRecreationItem item,
            SalesReturnRelationsSnapshot snapshot,
            string? errorCode = null,
            string? error = null)
        {
            Item = item;
            Snapshot = snapshot;
            ErrorCode = errorCode;
            Error = error;
            HasFailure = errorCode is not null;
        }

        public SalesReturnRecreationItem Item { get; }

        public SalesReturnRelationsSnapshot Snapshot { get; }

        public bool HasFailure { get; set; }

        public string? ErrorCode { get; set; }

        public string? Error { get; set; }

        public List<RelationWorkItem> Detached { get; } = [];
    }

    private sealed class RelationWorkItem
    {
        private RelationWorkItem(
            RelationState state,
            SalesReturnRelationDocumentType documentType,
            Guid documentId,
            string payloadJson,
            Func<string> rollbackPayload)
        {
            State = state;
            DocumentType = documentType;
            DocumentId = documentId;
            PayloadJson = payloadJson;
            RollbackPayloadFactory = rollbackPayload;
        }

        public RelationState State { get; }

        public SalesReturnRelationDocumentType DocumentType { get; }

        public Guid DocumentId { get; }

        public string PayloadJson { get; }

        private Func<string> RollbackPayloadFactory { get; }

        public string BuildRollbackPayload() => RollbackPayloadFactory();

        public static RelationWorkItem ForPayment(
            RelationState state,
            PaymentOutRelationSnapshot snapshot,
            string payloadJson) => new(
            state,
            SalesReturnRelationDocumentType.PaymentOut,
            snapshot.DocumentId,
            payloadJson,
            () => BuildMoneyPayload("paymentout", snapshot.DocumentId, snapshot.OperationsBefore));

        public static RelationWorkItem ForCashOut(
            RelationState state,
            CashOutRelationSnapshot snapshot,
            string payloadJson) => new(
            state,
            SalesReturnRelationDocumentType.CashOut,
            snapshot.DocumentId,
            payloadJson,
            () => BuildMoneyPayload("cashout", snapshot.DocumentId, snapshot.OperationsBefore));

        public static RelationWorkItem ForLoss(
            RelationState state,
            LossRelationSnapshot snapshot,
            string payloadJson) => new(
            state,
            SalesReturnRelationDocumentType.Loss,
            snapshot.DocumentId,
            payloadJson,
            () => BuildLossPayload(snapshot.DocumentId, snapshot.SalesReturnBefore));
    }
}
