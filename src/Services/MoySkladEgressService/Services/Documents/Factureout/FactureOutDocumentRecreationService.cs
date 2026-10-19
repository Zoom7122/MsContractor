using System.Text.Json;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Gateways.Documents.Factureout;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Factureout;

public sealed record FactureOutDocumentRecreationResult(
    IReadOnlyList<Guid> TransferredDocumentIds,
    IReadOnlyList<FactureOutFailedDocumentResult> FailedDocuments)
{
    public IReadOnlyDictionary<Guid, Guid> CreatedDocumentIds { get; init; } = new Dictionary<Guid, Guid>();
    public IReadOnlyList<DocumentRecreationTransferredDocumentResult> TransferredDocuments { get; init; } = [];
    public IReadOnlyList<FactureOutSkippedDocumentResult> SkippedDocuments { get; init; } = [];
    public IReadOnlyList<FactureOutCreatedWithErrorResult> CreatedWithErrors { get; init; } = [];
    public IReadOnlyList<DocumentRecreationUnCreatedButDeletedResult> UnCreatedButDeleted { get; init; } = [];
}

public sealed record FactureOutDocumentDeletionResult(
    IReadOnlyList<FactureOutPayload> ReadyToCreatePayloads,
    IReadOnlyList<FactureOutFailedDocumentResult> FailedDocuments);

public interface IFactureOutDocumentRecreationService
{
    Task<FactureOutDocumentRecreationResult> RecreateAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<FactureOutPayload> documentsPayload,
        CancellationToken cancellationToken);
}

public sealed class FactureOutDocumentRecreationService : IFactureOutDocumentRecreationService
{
    private const string DeleteResponseInvalid = "FACTUREOUT_DELETE_RESPONSE_INVALID";
    private const string DeleteFailed = "FACTUREOUT_DELETE_FAILED";
    private const string CreateSyncIdMissing = "FACTUREOUT_CREATE_SYNC_ID_MISSING";

    private readonly IMoySkladFactureOutGateway _gateway;
    private readonly IFactureOutDocumentRecreationVerifier _verifier;
    private readonly IFactureOutRecreationItemRepository _recreationItems;

    public FactureOutDocumentRecreationService(
        IMoySkladFactureOutGateway gateway,
        IFactureOutDocumentRecreationVerifier verifier,
        IFactureOutRecreationItemRepository recreationItems)
    {
        _gateway = gateway;
        _verifier = verifier;
        _recreationItems = recreationItems;
    }

    public async Task<FactureOutDocumentRecreationResult> RecreateAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<FactureOutPayload> documentsPayload,
        CancellationToken cancellationToken)
    {
        ValidateInput(accountId, mainCounterpartyId, documentsPayload);
        if (documentsPayload.Count == 0)
            return new FactureOutDocumentRecreationResult([], []);

        var correlationId = Guid.NewGuid().ToString("D");
        var deletion = await DeleteDocumentsAsync(
            accountId,
            correlationId,
            documentsPayload,
            cancellationToken);

        return await CreateAndVerifyDocumentsAsync(
            accountId,
            mainCounterpartyId,
            correlationId,
            deletion.ReadyToCreatePayloads,
            deletion.FailedDocuments,
            cancellationToken);
    }

    public async Task<FactureOutDocumentDeletionResult> DeleteDocumentsAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<FactureOutPayload> documentsPayload,
        CancellationToken cancellationToken)
    {
        ValidatePayloadInput(accountId, documentsPayload);
        if (documentsPayload.Count == 0)
            return new FactureOutDocumentDeletionResult([], []);

        IReadOnlyList<MoySkladFactureOutBatchDeleteResult> deleteResults;
        try
        {
            deleteResults = await _gateway.DeleteBatchAsync(
                accountId,
                correlationId,
                documentsPayload.Select(item => item.SourceDocumentId).ToArray(),
                cancellationToken);
        }
        catch (Exception exception) when (exception is EgressException or InvalidOperationException or JsonException)
        {
            return new FactureOutDocumentDeletionResult([], documentsPayload.Select(payload =>
                ToFailed(payload, "FACTUREOUT_DELETE_UNKNOWN_RESULT", SafeError(exception))).ToArray());
        }
        var deleteResultsById = deleteResults
            .GroupBy(result => result.SourceDocumentId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var readyToCreate = new List<FactureOutPayload>(documentsPayload.Count);
        var failed = new List<FactureOutFailedDocumentResult>();
        foreach (var payload in documentsPayload)
        {
            if (!deleteResultsById.TryGetValue(payload.SourceDocumentId, out var results) || results.Length != 1)
            {
                failed.Add(ToFailed(payload, DeleteResponseInvalid,
                    "MoySklad did not return an unambiguous delete result for factureout."));
                continue;
            }

            var deleteResult = results[0];
            if (!deleteResult.Succeeded)
            {
                failed.Add(ToFailed(
                    payload,
                    deleteResult.ErrorCode ?? DeleteFailed,
                    deleteResult.Error ?? "MoySklad rejected factureout deletion."));
                continue;
            }

            readyToCreate.Add(payload);
        }

        return new FactureOutDocumentDeletionResult(readyToCreate, failed);
    }

    public async Task<FactureOutDocumentRecreationResult> CreateAndVerifyDocumentsAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        string correlationId,
        IReadOnlyList<FactureOutPayload> documentsPayload,
        IReadOnlyList<FactureOutFailedDocumentResult> deletionFailures,
        CancellationToken cancellationToken)
    {
        ValidateInput(accountId, mainCounterpartyId, documentsPayload);

        var transferredDocumentIds = new Dictionary<Guid, Guid>();
        var failed = new List<FactureOutFailedDocumentResult>(deletionFailures);
        IReadOnlyList<FactureOutPayload> pending = documentsPayload;
        for (var attempt = 0; attempt < 2 && pending.Count > 0; attempt++)
        {
            IReadOnlyList<MoySkladFactureOutBatchCreateResult> response;
            FactureOutDocumentRecreationVerificationResult verification;
            try
            {
                response = await CreateBatchAsync(accountId, correlationId, pending, cancellationToken);
                verification = _verifier.Verify(pending, response);
            }
            catch (Exception exception) when (exception is EgressException or InvalidOperationException or JsonException)
            {
                failed.AddRange(pending.Select(payload => ToFailed(
                    payload, "FACTUREOUT_CREATE_UNKNOWN_RESULT", SafeError(exception))));
                break;
            }

            foreach (var created in verification.CreatedDocumentIds)
                transferredDocumentIds.Add(created.Key, created.Value);
            pending = verification.MissingDocuments;
            if (attempt == 0)
                continue;

            foreach (var payload in pending)
            {
                var matchingErrors = response.Where(result => result.ReturnedSyncId == payload.NewSyncId &&
                    (result.ErrorCode is not null || result.Error is not null)).ToArray();
                failed.Add(ToFailed(
                    payload,
                    matchingErrors.Length == 1 ? matchingErrors[0].ErrorCode ?? CreateSyncIdMissing : CreateSyncIdMissing,
                    matchingErrors.Length == 1 && matchingErrors[0].Error is string error
                        ? error
                        : "MoySklad did not return a successful result with the expected syncId after retry."));
            }
        }

        try
        {
            await _recreationItems.SaveRecreationResultsAsync(
                accountId,
                transferredDocumentIds,
                failed.Select(item => item.SourceDocumentId).ToArray(),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failed.AddRange(documentsPayload.Where(item => transferredDocumentIds.ContainsKey(item.SourceDocumentId))
                .Select(item => new FactureOutFailedDocumentResult(item.SourceDocumentId, item.NewSyncId,
                    transferredDocumentIds[item.SourceDocumentId], "Failed", "FACTUREOUT_RESULTS_SAVE_FAILED",
                    "Factureout was created but its recreation result could not be saved.")));
        }

        var failedIds = failed.Select(item => item.SourceDocumentId).ToHashSet();
        var transferredIds = transferredDocumentIds.Keys.Except(failedIds).ToArray();
        var deletedIds = documentsPayload.Select(item => item.SourceDocumentId).ToHashSet();
        return new FactureOutDocumentRecreationResult(transferredIds, failed)
        {
            CreatedDocumentIds = transferredDocumentIds,
            TransferredDocuments = transferredIds
                .Select(id => new DocumentRecreationTransferredDocumentResult(id, transferredDocumentIds[id]))
                .ToArray(),
            SkippedDocuments = deletionFailures
                .Select(item => new FactureOutSkippedDocumentResult(
                    item.SourceDocumentId,
                    "Skipped",
                    item.ErrorCode,
                    item.Error))
                .ToArray(),
            CreatedWithErrors = failed
                .Where(item => item.NewDocumentId is not null)
                .Select(item => new FactureOutCreatedWithErrorResult(
                    item.SourceDocumentId,
                    item.NewDocumentId!.Value,
                    "CreatedWithError",
                    item.ErrorCode,
                    item.Error))
                .ToArray(),
            UnCreatedButDeleted = failed
                .Where(item => deletedIds.Contains(item.SourceDocumentId) && item.NewDocumentId is null)
                .Select(item => new DocumentRecreationUnCreatedButDeletedResult(
                    item.SourceDocumentId,
                    "Failed",
                    item.ErrorCode,
                    item.Error))
                .ToArray()
        };
    }

    private Task<IReadOnlyList<MoySkladFactureOutBatchCreateResult>> CreateBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<FactureOutPayload> documents,
        CancellationToken cancellationToken) =>
        _gateway.CreateBatchAsync(
            accountId,
            correlationId,
            documents.Select(item => new MoySkladFactureOutBatchCreateItem(
                item.SourceDocumentId,
                item.NewSyncId,
                item.PayloadJson)).ToArray(),
            cancellationToken);

    private static string SafeError(Exception exception) =>
        exception is EgressException egress ? egress.SafeMessage : exception.Message;

    private static FactureOutFailedDocumentResult ToFailed(
        FactureOutPayload payload,
        string errorCode,
        string error) =>
        new(payload.SourceDocumentId, payload.NewSyncId, null, "Failed", errorCode, error);

    private static void ValidateInput(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<FactureOutPayload> documentsPayload)
    {
        if (mainCounterpartyId == Guid.Empty)
            throw new ArgumentException("A non-empty main counterparty id is required.", nameof(mainCounterpartyId));
        ValidatePayloadInput(accountId, documentsPayload);
    }

    private static void ValidatePayloadInput(
        Guid accountId,
        IReadOnlyList<FactureOutPayload> documentsPayload)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("A non-empty account id is required.", nameof(accountId));
        if (documentsPayload.Count > MoySkladFactureOutGateway.BatchSize)
            throw new ArgumentOutOfRangeException(
                nameof(documentsPayload),
                $"A factureout recreation batch cannot exceed {MoySkladFactureOutGateway.BatchSize} documents.");
        if (documentsPayload.Any(item =>
                item.SourceDocumentId == Guid.Empty ||
                item.NewSyncId == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.PayloadJson)) ||
            documentsPayload.Select(item => item.SourceDocumentId).Distinct().Count() != documentsPayload.Count ||
            documentsPayload.Select(item => item.NewSyncId).Distinct().Count() != documentsPayload.Count)
        {
            throw new ArgumentException(
                "Factureout payloads must have unique non-empty source and new sync ids and a JSON payload.",
                nameof(documentsPayload));
        }
    }
}
