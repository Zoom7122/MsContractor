using MsContractor.MoySkladEgressService.Gateways.Documents.Factureout;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Factureout;

public sealed record FactureOutDocumentRecreationResult(
    IReadOnlyList<Guid> TransferredDocumentIds,
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
        var deleteResults = await _gateway.DeleteBatchAsync(
            accountId,
            correlationId,
            documentsPayload.Select(item => item.SourceDocumentId).ToArray(),
            cancellationToken);

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

        var transferredDocumentIds = new Dictionary<Guid, Guid>();
        if (readyToCreate.Count > 0)
        {
            var createResponse = await CreateBatchAsync(accountId, correlationId, readyToCreate, cancellationToken);
            var verification = _verifier.Verify(readyToCreate, createResponse);
            foreach (var created in verification.CreatedDocumentIds)
                transferredDocumentIds.Add(created.Key, created.Value);

            if (verification.MissingDocuments.Count > 0)
            {
                var retryResponse = await CreateBatchAsync(
                    accountId,
                    correlationId,
                    verification.MissingDocuments,
                    cancellationToken);
                var retryVerification = _verifier.Verify(verification.MissingDocuments, retryResponse);
                foreach (var created in retryVerification.CreatedDocumentIds)
                    transferredDocumentIds.Add(created.Key, created.Value);

                foreach (var payload in retryVerification.MissingDocuments)
                {
                    var matchingErrors = retryResponse
                        .Where(result => result.ReturnedSyncId == payload.NewSyncId &&
                                         (result.ErrorCode is not null || result.Error is not null))
                        .ToArray();
                    var errorCode = matchingErrors.Length == 1
                        ? matchingErrors[0].ErrorCode ?? CreateSyncIdMissing
                        : CreateSyncIdMissing;
                    var error = matchingErrors.Length == 1
                        ? matchingErrors[0].Error ??
                          "MoySklad did not return a successful result with the expected syncId after retry."
                        : "MoySklad did not return a successful result with the expected syncId after retry.";
                    failed.Add(ToFailed(payload, errorCode, error));
                }
            }
        }

        await _recreationItems.SaveRecreationResultsAsync(
            accountId,
            transferredDocumentIds,
            failed.Select(item => item.SourceDocumentId).ToArray(),
            cancellationToken);

        return new FactureOutDocumentRecreationResult(
            transferredDocumentIds.Keys.ToArray(),
            failed);
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
        if (accountId == Guid.Empty)
            throw new ArgumentException("A non-empty account id is required.", nameof(accountId));
        if (mainCounterpartyId == Guid.Empty)
            throw new ArgumentException("A non-empty main counterparty id is required.", nameof(mainCounterpartyId));
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
