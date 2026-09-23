using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MsContractor.MoySkladEgressService.Gateways.Documents.Purchasereturn;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Purchasereturn;

public interface IPurchaseReturnFactureRelationsService
{
    Task<PurchaseReturnFactureRelationsResult> CheckAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> purchaseReturnIds,
        CancellationToken cancellationToken);

    Task<PurchaseReturnRelationsReattachResult> ReattachAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> detachedPurchaseReturnIds,
        IReadOnlyDictionary<Guid, Guid> oldToNewPurchaseReturnIds,
        CancellationToken cancellationToken);
}

public sealed class PurchaseReturnFactureRelationsService : IPurchaseReturnFactureRelationsService
{
    private const string Skipped = "Skipped";
    private const string NoRelations = "NoRelations";
    private const string Detached = "Detached";
    private const string RelationsPresent = "PURCHASERETURN_FACTURE_RELATIONS_PRESENT";
    private const string SnapshotLoadFailed = "PURCHASERETURN_FACTURE_SNAPSHOT_LOAD_FAILED";
    private const string MoneyRelationsLoadFailed = "PURCHASERETURN_PAYMENT_RELATIONS_LOAD_FAILED";
    private const string MoneyRelationNotFound = "PURCHASERETURN_PAYMENT_RELATION_NOT_FOUND";
    private const string MoneyRelationsDetachFailed = "PURCHASERETURN_PAYMENT_RELATIONS_DETACH_FAILED";
    private const string MoneyRelationsDetachVerifyFailed = "PURCHASERETURN_PAYMENT_RELATIONS_DETACH_VERIFY_FAILED";
    private const string MoneyRelationsRollbackFailed = "PURCHASERETURN_PAYMENT_RELATIONS_ROLLBACK_FAILED";
    private const string MoneyRelationsReattachFailed = "PURCHASERETURN_PAYMENT_RELATIONS_REATTACH_FAILED";

    private readonly IPurchaseReturnFactureRelationsRepository _repository;
    private readonly IPurchaseReturnMoneyRelationsRepository? _moneyRepository;
    private readonly IMoySkladPurchaseReturnMoneyRelationsGateway? _gateway;
    private readonly ILogger<PurchaseReturnFactureRelationsService> _logger;

    public PurchaseReturnFactureRelationsService(
        IPurchaseReturnFactureRelationsRepository repository,
        ILogger<PurchaseReturnFactureRelationsService> logger,
        IMoySkladPurchaseReturnMoneyRelationsGateway? gateway = null,
        IPurchaseReturnMoneyRelationsRepository? moneyRepository = null)
    {
        _repository = repository;
        _logger = logger;
        _gateway = gateway;
        _moneyRepository = moneyRepository;
    }

    public async Task<PurchaseReturnFactureRelationsResult> CheckAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> purchaseReturnIds,
        CancellationToken cancellationToken)
    {
        var ids = purchaseReturnIds.Distinct().ToArray();
        ValidateIds(ids, nameof(purchaseReturnIds));
        if (ids.Length == 0)
            return new PurchaseReturnFactureRelationsResult([]);

        IReadOnlyDictionary<Guid, PurchaseReturnFactureRelationsSnapshot> snapshots;
        try
        {
            snapshots = await _repository.GetSnapshotsAsync(accountId, ids, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "purchasereturn relation snapshots could not be loaded: account_id={AccountId}, document_count={DocumentCount}",
                accountId,
                ids.Length);
            return new PurchaseReturnFactureRelationsResult(
                ids.Select(id => new PurchaseReturnFactureRelationResult(
                    id,
                    Skipped,
                    SnapshotLoadFailed,
                    exception.Message)).ToArray());
        }

        return await ProcessAsync(accountId, ids, snapshots, cancellationToken);
    }

    public Task<PurchaseReturnFactureRelationsResult> CheckAsync(
        Guid accountId,
        IReadOnlyCollection<PurchaseReturnFactureRelationsSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var snapshotArray = snapshots.ToArray();
        ValidateIds(snapshotArray.Select(item => item.PurchaseReturnId).ToArray(), nameof(snapshots));
        var duplicateId = snapshotArray
            .GroupBy(item => item.PurchaseReturnId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            throw new ArgumentException(
                $"The purchasereturn relation snapshots contain duplicate purchasereturn {duplicateId.Key:D}.",
                nameof(snapshots));
        }

        return ProcessAsync(
            accountId,
            snapshotArray.Select(item => item.PurchaseReturnId).ToArray(),
            snapshotArray.ToDictionary(item => item.PurchaseReturnId),
            cancellationToken);
    }

    public async Task<PurchaseReturnRelationsReattachResult> ReattachAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> detachedPurchaseReturnIds,
        IReadOnlyDictionary<Guid, Guid> oldToNewPurchaseReturnIds,
        CancellationToken cancellationToken)
    {
        var detachedIds = detachedPurchaseReturnIds.Distinct().ToArray();
        var detachedIdSet = detachedIds.ToHashSet();
        ValidateIds(detachedIds, nameof(detachedPurchaseReturnIds));
        if (oldToNewPurchaseReturnIds.Count == 0)
            return new PurchaseReturnRelationsReattachResult([]);

        if (oldToNewPurchaseReturnIds.Keys.Any(id => id == Guid.Empty) ||
            oldToNewPurchaseReturnIds.Values.Any(id => id == Guid.Empty) ||
            oldToNewPurchaseReturnIds.Keys.Any(id => !detachedIds.Contains(id)))
        {
            throw new ArgumentException(
                "The purchasereturn reattach mapping must contain non-empty ids from detachedPurchaseReturnIds.",
                nameof(oldToNewPurchaseReturnIds));
        }

        var states = oldToNewPurchaseReturnIds.ToDictionary(
            pair => pair.Key,
            pair => new ReattachState(pair.Key, pair.Value));

        IReadOnlyDictionary<Guid, PurchaseReturnFactureRelationsSnapshot> snapshots;
        try
        {
            snapshots = await _repository.GetSnapshotsAsync(
                accountId,
                detachedIds,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "purchasereturn relation snapshots could not be loaded for reattach: account_id={AccountId}, document_count={DocumentCount}",
                accountId,
                oldToNewPurchaseReturnIds.Count);
            return new PurchaseReturnRelationsReattachResult(
                states.Values.Select(state => ReattachFailure(
                    state,
                    $"Could not load purchasereturn relation snapshots: {exception.Message}")).ToArray());
        }

        var groups = new Dictionary<MoneyDocumentKey, ReattachGroup>();
        foreach (var state in states.Values)
        {
            if (!snapshots.TryGetValue(state.PurchaseReturnId, out var snapshot))
            {
                MarkReattachFailure(state, "Could not find the purchasereturn relation snapshot.");
                continue;
            }

            var relations = snapshot.PaymentIns
                .Select(item => new ReattachRelation(MoneyRelationKind.PaymentIn, item))
                .Concat(snapshot.CashIns.Select(item => new ReattachRelation(MoneyRelationKind.CashIn, item)))
                .ToArray();
            if (relations.Length == 0)
            {
                state.Status = "NoRelations";
                continue;
            }

            state.HasRelations = true;
            foreach (var relation in relations)
            {
                if (string.IsNullOrWhiteSpace(relation.Snapshot.OperationsBeforeJson))
                {
                    MarkReattachFailure(
                        state,
                        $"The {EntityType(relation.Kind)} {relation.Snapshot.DocumentId:D} has no saved operations snapshot.");
                    continue;
                }

                JsonElement[] operations;
                try
                {
                    operations = ReadOperationsSnapshot(relation.Snapshot.OperationsBeforeJson);
                }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException)
                {
                    MarkReattachFailure(
                        state,
                        $"The saved {EntityType(relation.Kind)} {relation.Snapshot.DocumentId:D} operations snapshot is invalid: {exception.Message}");
                    continue;
                }

                if (!operations.Any(operation =>
                        IsPurchaseReturnReference(operation, state.PurchaseReturnId)))
                {
                    MarkReattachFailure(
                        state,
                        $"The saved {EntityType(relation.Kind)} {relation.Snapshot.DocumentId:D} operations do not contain purchasereturn {state.PurchaseReturnId:D}.");
                    continue;
                }

                var key = new MoneyDocumentKey(relation.Kind, relation.Snapshot.DocumentId);
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new ReattachGroup(
                        relation.Kind,
                        relation.Snapshot.DocumentId,
                        operations);
                    groups[key] = group;
                }

                group.Relations.Add(new ReattachRelationWork(state, relation.Snapshot));
            }
        }

        if (_gateway is null)
        {
            foreach (var group in groups.Values)
            {
                foreach (var state in ActiveStates(group))
                    MarkReattachFailure(state, "The purchasereturn money relations gateway is not configured.");
            }
        }

        foreach (var kind in new[] { MoneyRelationKind.PaymentIn, MoneyRelationKind.CashIn })
        {
            var kindGroups = groups.Values
                .Where(group => group.Kind == kind)
                .ToArray();
            foreach (var chunk in kindGroups.Chunk(MoySkladPurchaseReturnMoneyRelationsGateway.BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requests = new List<MoySkladPurchaseReturnMoneyRelationBatchItem>();
                var requestGroups = new List<ReattachGroup>();
                foreach (var group in chunk)
                {
                    var activeSources = group.Relations
                        .Select(item => item.State)
                        .Where(state => !state.HasFailure)
                        .DistinctBy(state => state.PurchaseReturnId)
                        .ToArray();
                    if (activeSources.Length == 0)
                        continue;

                    requests.Add(new MoySkladPurchaseReturnMoneyRelationBatchItem(
                        group.DocumentId,
                        BuildReattachMoneyPayload(
                            EntityType(group.Kind),
                            group.DocumentId,
                            group.OriginalOperations,
                            detachedIdSet,
                            states,
                            oldToNewPurchaseReturnIds)));
                    requestGroups.Add(group);
                }

                if (requests.Count == 0)
                    continue;

                if (_gateway is null)
                    continue;

                IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchResult> response;
                try
                {
                    response = kind == MoneyRelationKind.PaymentIn
                        ? await _gateway.PaymentInBatchAsync(
                            accountId,
                            Guid.NewGuid().ToString("D"),
                            requests,
                            cancellationToken)
                        : await _gateway.CashInBatchAsync(
                            accountId,
                            Guid.NewGuid().ToString("D"),
                            requests,
                            cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    foreach (var group in requestGroups)
                    {
                        foreach (var state in ActiveStates(group))
                        {
                            MarkReattachFailure(
                                state,
                                $"Could not send the {EntityType(group.Kind)} {group.DocumentId:D} batch: {exception.Message}");
                        }
                    }

                    continue;
                }

                var byId = response
                    .GroupBy(item => item.DocumentId)
                    .ToDictionary(group => group.Key, group => group.Last());
                foreach (var group in requestGroups)
                {
                    var activeStates = ActiveStates(group);
                    if (!byId.TryGetValue(group.DocumentId, out var item))
                    {
                        foreach (var state in activeStates)
                        {
                            MarkReattachFailure(
                                state,
                                $"The {EntityType(group.Kind)} {group.DocumentId:D} is missing from the reattach batch response.");
                        }

                        continue;
                    }

                    if (!item.Succeeded)
                    {
                        foreach (var state in activeStates)
                        {
                            MarkReattachFailure(
                                state,
                                $"MoySklad rejected the {EntityType(group.Kind)} {group.DocumentId:D} reattach: {item.Error ?? item.ErrorCode ?? "unknown error"}.");
                        }

                        continue;
                    }

                    JsonElement[] afterOperations;
                    try
                    {
                        afterOperations = ReadOperations(item.RawJson, requireArray: false);
                    }
                    catch (Exception exception) when (exception is JsonException or InvalidOperationException)
                    {
                        foreach (var state in activeStates)
                        {
                            MarkReattachFailure(
                                state,
                                $"The {EntityType(group.Kind)} {group.DocumentId:D} reattach response is invalid: {exception.Message}");
                        }

                        continue;
                    }

                    var oldDetachedLinkRemains = afterOperations.Any(operation =>
                        FindPurchaseReturnReference(operation, detachedIdSet) is not null);
                    foreach (var state in activeStates)
                    {
                        var newLinkExists = afterOperations.Any(operation =>
                            IsPurchaseReturnReference(operation, state.NewPurchaseReturnId));
                        if (oldDetachedLinkRemains || !newLinkExists)
                        {
                            var reason = oldDetachedLinkRemains
                                ? "an old purchasereturn link is still present"
                                : "the new purchasereturn link is missing";
                            MarkReattachFailure(
                                state,
                                $"The {EntityType(group.Kind)} {group.DocumentId:D} reattach verification failed: {reason}.");
                        }
                    }
                }
            }
        }

        foreach (var state in states.Values.Where(state => !state.HasFailure && state.Status is null))
            state.Status = state.HasRelations ? "Reattached" : "NoRelations";

        return new PurchaseReturnRelationsReattachResult(
            oldToNewPurchaseReturnIds.Keys
                .Select(id => states[id].ToResult())
                .ToArray());
    }

    private async Task<PurchaseReturnFactureRelationsResult> ProcessAsync(
        Guid accountId,
        IReadOnlyList<Guid> ids,
        IReadOnlyDictionary<Guid, PurchaseReturnFactureRelationsSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var states = ids.Select(id =>
        {
            if (!snapshots.TryGetValue(id, out var snapshot))
            {
                return new SourceState(
                    id,
                    new PurchaseReturnFactureRelationsSnapshot(id, [], []),
                    "PURCHASERETURN_FACTURE_SNAPSHOT_MISSING",
                    "The facture relation snapshot is missing.");
            }

            if (snapshot.FactureIn.Count != 0 || snapshot.FactureOut.Count != 0)
            {
                var relationDescription =
                    $"factureIn={snapshot.FactureIn.Count}, factureOut={snapshot.FactureOut.Count}";
                _logger.LogWarning(
                    "purchasereturn skipped because facture relations are present: account_id={AccountId}, purchasereturn_id={PurchaseReturnId}, relations={Relations}",
                    accountId,
                    id,
                    relationDescription);
                return new SourceState(
                    id,
                    snapshot,
                    RelationsPresent,
                    $"The purchasereturn has facture relations ({relationDescription}).");
            }

            return new SourceState(id, snapshot);
        }).ToArray();

        var moneyStates = states
            .Where(state => !state.HasFailure && state.HasMoneyRelations)
            .ToArray();
        foreach (var state in states.Where(state => !state.HasFailure && !state.HasMoneyRelations))
            state.Status = NoRelations;

        if (moneyStates.Length == 0)
            return ToResult(states);

        if (_gateway is null || _moneyRepository is null)
        {
            foreach (var state in moneyStates)
            {
                MarkFailure(
                    state,
                    MoneyRelationsLoadFailed,
                    "The purchasereturn money relations gateway or repository is not configured.");
            }
            return ToResult(states);
        }

        await LoadMoneyRelationsAsync(accountId, moneyStates, cancellationToken);

        var snapshotRelations = moneyStates
            .SelectMany(state => state.Relations)
            .ToArray();
        if (snapshotRelations.Length != 0)
        {
            try
            {
                await _moneyRepository.SaveMoneyRelationSnapshotsAsync(
                    accountId,
                    snapshotRelations.Select(relation => new PurchaseReturnMoneyRelationSnapshotUpdate(
                            relation.State.PurchaseReturnId,
                            relation.DocumentId,
                            relation.Kind == MoneyRelationKind.CashIn,
                            SerializeOperations(relation.Document.Operations),
                            relation.LinkedSum))
                        .ToArray(),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "purchasereturn money relation snapshots could not be saved: account_id={AccountId}, relation_count={RelationCount}",
                    accountId,
                    snapshotRelations.Length);
                foreach (var state in moneyStates.Where(state => !state.HasFailure))
                    MarkFailure(state, MoneyRelationsLoadFailed, exception.Message);
                return ToResult(states);
            }

            var detachableStates = moneyStates.Where(state => !state.HasFailure).ToArray();
            if (detachableStates.Length != 0)
                await DetachAsync(accountId, detachableStates, cancellationToken);
        }

        var failedStates = moneyStates
            .Where(state => state.HasFailure && state.Detached.Count != 0)
            .ToArray();
        if (failedStates.Length != 0)
            await RollbackAsync(accountId, failedStates, cancellationToken);

        foreach (var state in moneyStates.Where(state => !state.HasFailure))
            state.Status = Detached;

        return ToResult(states);
    }

    private async Task LoadMoneyRelationsAsync(
        Guid accountId,
        IReadOnlyCollection<SourceState> states,
        CancellationToken cancellationToken)
    {
        var documents = states
            .SelectMany(state => state.MoneyRelations.Select(relation =>
                new MoneyDocumentKey(relation.Kind, relation.DocumentId)))
            .Distinct()
            .ToArray();
        var loaded = new Dictionary<MoneyDocumentKey, LoadedMoneyDocument>();
        var failed = new Dictionary<MoneyDocumentKey, string>();
        var correlationId = Guid.NewGuid().ToString("D");

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var rawJson = document.Kind == MoneyRelationKind.PaymentIn
                    ? await _gateway!.GetPaymentInAsync(accountId, document.DocumentId, correlationId, cancellationToken)
                    : await _gateway!.GetCashInAsync(accountId, document.DocumentId, correlationId, cancellationToken);
                loaded[document] = new LoadedMoneyDocument(
                    document,
                    ReadOperations(rawJson, requireArray: true));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed[document] = exception.Message;
                _logger.LogWarning(
                    exception,
                    "purchasereturn money relation document could not be loaded: account_id={AccountId}, document_id={DocumentId}, relation_type={RelationType}",
                    accountId,
                    document.DocumentId,
                    document.Kind);
            }
        }

        foreach (var state in states)
        {
            foreach (var relation in state.MoneyRelations)
            {
                var key = new MoneyDocumentKey(relation.Kind, relation.DocumentId);
                if (failed.TryGetValue(key, out var loadError))
                {
                    MarkFailure(
                        state,
                        MoneyRelationsLoadFailed,
                        $"Could not load {relation.Kind.ToString().ToLowerInvariant()} {relation.DocumentId:D}: {loadError}");
                    continue;
                }

                var document = loaded[key];
                var matching = document.Operations
                    .Where(operation => IsPurchaseReturnReference(operation, state.PurchaseReturnId))
                    .ToArray();
                if (matching.Length == 0)
                {
                    MarkFailure(
                        state,
                        MoneyRelationNotFound,
                        $"The {relation.Kind.ToString().ToLowerInvariant()} {relation.DocumentId:D} does not contain an operation for purchasereturn {state.PurchaseReturnId:D}.");
                    continue;
                }

                if (!TryReadDecimal(matching[0], "linkedSum", out var linkedSum))
                {
                    MarkFailure(
                        state,
                        MoneyRelationsLoadFailed,
                        $"The {relation.Kind.ToString().ToLowerInvariant()} {relation.DocumentId:D} has no valid linkedSum on its purchasereturn operation.");
                    continue;
                }

                state.Relations.Add(new RelationWork(
                    state,
                    relation.Kind,
                    relation.DocumentId,
                    document,
                    linkedSum));
            }
        }
    }

    private async Task DetachAsync(
        Guid accountId,
        IReadOnlyCollection<SourceState> states,
        CancellationToken cancellationToken)
    {
        foreach (var kind in new[] { MoneyRelationKind.PaymentIn, MoneyRelationKind.CashIn })
        {
            var groups = states
                .SelectMany(state => state.Relations)
                .Where(relation => relation.Kind == kind)
                .GroupBy(relation => relation.DocumentId)
                .Select(group => new DetachGroup(
                    kind,
                    group.Key,
                    group.ToArray(),
                    group.First().Document))
                .ToArray();

            foreach (var chunk in groups.Chunk(MoySkladPurchaseReturnMoneyRelationsGateway.BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requests = chunk
                    .Select(group => new MoySkladPurchaseReturnMoneyRelationBatchItem(
                        group.DocumentId,
                        BuildMoneyPayload(
                            EntityType(group.Kind),
                            group.DocumentId,
                            group.Document.Operations.Where(operation =>
                                !group.Relations.Any(relation =>
                                    IsPurchaseReturnReference(operation, relation.State.PurchaseReturnId))).ToArray())))
                    .ToArray();
                IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchResult> results;
                try
                {
                    results = kind == MoneyRelationKind.PaymentIn
                        ? await _gateway!.PaymentInBatchAsync(accountId, Guid.NewGuid().ToString("D"), requests, cancellationToken)
                        : await _gateway!.CashInBatchAsync(accountId, Guid.NewGuid().ToString("D"), requests, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    foreach (var group in chunk)
                    {
                        foreach (var relation in group.Relations)
                            MarkFailure(relation.State, MoneyRelationsDetachFailed, exception.Message);
                    }
                    continue;
                }

                var byId = results
                    .GroupBy(item => item.DocumentId)
                    .ToDictionary(group => group.Key, group => group.Last());
                foreach (var group in chunk)
                {
                    if (!byId.TryGetValue(group.DocumentId, out var result))
                    {
                        foreach (var relation in group.Relations)
                            MarkFailure(relation.State, MoneyRelationsDetachFailed, "The detach batch response omitted a related document.");
                        continue;
                    }

                    if (!result.Succeeded)
                    {
                        var error = result.Error is null
                            ? "MoySklad rejected relation detachment."
                            : $"MoySklad rejected relation detachment ({result.ErrorCode}): {result.Error}";
                        foreach (var relation in group.Relations)
                            MarkFailure(relation.State, MoneyRelationsDetachFailed, error);
                        continue;
                    }

                    JsonElement[] afterOperations;
                    try
                    {
                        afterOperations = ReadOperations(result.RawJson, requireArray: false);
                    }
                    catch (Exception exception)
                    {
                        foreach (var relation in group.Relations)
                            MarkFailure(relation.State, MoneyRelationsDetachVerifyFailed, exception.Message);
                        continue;
                    }

                    foreach (var relation in group.Relations)
                    {
                        if (afterOperations.Any(operation =>
                                IsPurchaseReturnReference(operation, relation.State.PurchaseReturnId)))
                        {
                            MarkFailure(
                                relation.State,
                                MoneyRelationsDetachVerifyFailed,
                                $"The old purchasereturn link is still present in {EntityType(kind)} {group.DocumentId:D} after detach.");
                            continue;
                        }

                        relation.State.Detached.Add(new DetachedRelation(relation, afterOperations));
                    }
                }
            }
        }
    }

    private async Task RollbackAsync(
        Guid accountId,
        IReadOnlyCollection<SourceState> states,
        CancellationToken cancellationToken)
    {
        foreach (var kind in new[] { MoneyRelationKind.PaymentIn, MoneyRelationKind.CashIn })
        {
            var groups = states
                .SelectMany(state => state.Detached)
                .Where(item => item.Relation.Kind == kind)
                .GroupBy(item => item.Relation.DocumentId)
                .Select(group => new RollbackGroup(
                    kind,
                    group.Key,
                    group.ToArray(),
                    group.First().Relation.Document,
                    group.First().AfterOperations))
                .ToArray();

            foreach (var chunk in groups.Chunk(MoySkladPurchaseReturnMoneyRelationsGateway.BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requests = chunk
                    .Select(group => new MoySkladPurchaseReturnMoneyRelationBatchItem(
                        group.DocumentId,
                        BuildMoneyPayload(
                            EntityType(group.Kind),
                            group.DocumentId,
                            BuildRollbackOperations(
                                group.Document.Operations,
                                group.AfterOperations,
                                group.Relations.Select(item => item.Relation.State.PurchaseReturnId).ToHashSet()))))
                    .ToArray();
                IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchResult> results;
                try
                {
                    results = kind == MoneyRelationKind.PaymentIn
                        ? await _gateway!.PaymentInBatchAsync(accountId, Guid.NewGuid().ToString("D"), requests, cancellationToken)
                        : await _gateway!.CashInBatchAsync(accountId, Guid.NewGuid().ToString("D"), requests, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    foreach (var group in chunk)
                    {
                        foreach (var item in group.Relations)
                            MarkRollbackFailure(item.Relation.State, exception.Message);
                    }
                    continue;
                }

                var byId = results
                    .GroupBy(item => item.DocumentId)
                    .ToDictionary(group => group.Key, group => group.Last());
                foreach (var group in chunk)
                {
                    var valid = byId.TryGetValue(group.DocumentId, out var result) && result.Succeeded;
                    JsonElement[] afterRollback = [];
                    if (valid)
                    {
                        try
                        {
                            afterRollback = ReadOperations(result!.RawJson, requireArray: false);
                        }
                        catch
                        {
                            valid = false;
                        }
                    }

                    foreach (var item in group.Relations)
                    {
                        if (!valid || !afterRollback.Any(operation =>
                                IsPurchaseReturnReference(operation, item.Relation.State.PurchaseReturnId)))
                        {
                            MarkRollbackFailure(
                                item.Relation.State,
                                result?.Error ?? "MoySklad did not restore a detached purchasereturn relation.");
                        }
                    }
                }
            }
        }
    }

    private static IReadOnlyList<ReattachState> ActiveStates(ReattachGroup group) =>
        group.Relations
            .Select(item => item.State)
            .Where(state => !state.HasFailure)
            .DistinctBy(state => state.PurchaseReturnId)
            .ToArray();

    private static PurchaseReturnRelationsReattachItem ReattachFailure(
        ReattachState state,
        string error)
    {
        MarkReattachFailure(state, error);
        return state.ToResult();
    }

    private static void MarkReattachFailure(ReattachState state, string error)
    {
        if (state.HasFailure)
            return;

        state.ErrorCode = MoneyRelationsReattachFailed;
        state.Error =
            $"Could not reattach purchasereturn {state.PurchaseReturnId:D} to {state.NewPurchaseReturnId:D}: {error}";
        state.Status = "Failed";
    }

    private static string BuildReattachMoneyPayload(
        string entityType,
        Guid documentId,
        IReadOnlyList<JsonElement> originalOperations,
        IReadOnlySet<Guid> detachedIds,
        IReadOnlyDictionary<Guid, ReattachState> states,
        IReadOnlyDictionary<Guid, Guid> oldToNewPurchaseReturnIds)
    {
        var operations = new List<JsonElement>(originalOperations.Count);
        foreach (var operation in originalOperations)
        {
            var sourceId = FindPurchaseReturnReference(operation, detachedIds);
            if (sourceId is null)
            {
                operations.Add(operation.Clone());
                continue;
            }

            if (!states.TryGetValue(sourceId.Value, out var state) ||
                state.HasFailure ||
                !oldToNewPurchaseReturnIds.TryGetValue(sourceId.Value, out var newId))
            {
                continue;
            }

            operations.Add(ReplacePurchaseReturnReference(operation, newId));
        }

        return BuildMoneyPayload(entityType, documentId, operations);
    }

    private static JsonElement ReplacePurchaseReturnReference(JsonElement operation, Guid newPurchaseReturnId)
    {
        var node = JsonNode.Parse(operation.GetRawText())?.AsObject()
            ?? throw new JsonException("A saved money operation is not a JSON object.");
        var meta = node["meta"]?.AsObject()
            ?? throw new JsonException("A saved money operation has no meta object.");

        meta["href"] = $"https://api.moysklad.ru/api/remap/1.2/entity/purchasereturn/{newPurchaseReturnId:D}";
        meta["type"] = "purchasereturn";
        if (meta.ContainsKey("uuidHref"))
        {
            meta["uuidHref"] =
                $"https://online.moysklad.ru/app/#purchasereturn/edit?id={newPurchaseReturnId:D}";
        }

        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static Guid? FindPurchaseReturnReference(
        JsonElement operation,
        IReadOnlySet<Guid> purchaseReturnIds)
    {
        foreach (var purchaseReturnId in purchaseReturnIds)
        {
            if (IsPurchaseReturnReference(operation, purchaseReturnId))
                return purchaseReturnId;
        }

        return null;
    }

    private static PurchaseReturnFactureRelationsResult ToResult(IEnumerable<SourceState> states) =>
        new(states.Select(state => new PurchaseReturnFactureRelationResult(
            state.PurchaseReturnId,
            state.Status ?? (state.HasFailure ? Skipped : Detached),
            state.ErrorCode,
            state.Error)).ToArray());

    private static void MarkFailure(SourceState state, string errorCode, string error)
    {
        if (state.HasFailure)
            return;
        state.ErrorCode = errorCode;
        state.Error = error;
        state.Status = Skipped;
    }

    private static void MarkRollbackFailure(SourceState state, string error)
    {
        state.ErrorCode = MoneyRelationsRollbackFailed;
        state.Error = error;
        state.Status = Skipped;
    }

    private static string EntityType(MoneyRelationKind kind) =>
        kind == MoneyRelationKind.PaymentIn ? "paymentin" : "cashin";

    private static string BuildMoneyPayload(
        string entityType,
        Guid documentId,
        IReadOnlyCollection<JsonElement> operations)
    {
        var operationsNode = new JsonArray();
        foreach (var operation in operations)
            operationsNode.Add(JsonNode.Parse(operation.GetRawText()));

        return new JsonObject
        {
            ["meta"] = BuildMeta(entityType, documentId),
            ["operations"] = operationsNode
        }.ToJsonString();
    }

    private static JsonElement[] BuildRollbackOperations(
        IReadOnlyList<JsonElement> originalOperations,
        IReadOnlyList<JsonElement> currentOperations,
        IReadOnlySet<Guid> sourceIds)
    {
        var usedCurrent = new bool[currentOperations.Count];
        var restored = new List<JsonElement>(originalOperations.Count + sourceIds.Count);
        foreach (var original in originalOperations)
        {
            if (sourceIds.Any(sourceId => IsPurchaseReturnReference(original, sourceId)))
            {
                restored.Add(original.Clone());
                continue;
            }

            var currentIndex = -1;
            for (var index = 0; index < currentOperations.Count; index++)
            {
                if (!usedCurrent[index] && string.Equals(
                        currentOperations[index].GetRawText(),
                        original.GetRawText(),
                        StringComparison.Ordinal))
                {
                    currentIndex = index;
                    break;
                }
            }

            if (currentIndex >= 0)
            {
                usedCurrent[currentIndex] = true;
                restored.Add(currentOperations[currentIndex].Clone());
            }
        }

        for (var index = 0; index < currentOperations.Count; index++)
        {
            if (!usedCurrent[index])
                restored.Add(currentOperations[index].Clone());
        }

        return restored.ToArray();
    }

    private static JsonObject BuildMeta(string type, Guid id) => new()
    {
        ["href"] = $"https://api.moysklad.ru/api/remap/1.2/entity/{type}/{id:D}",
        ["type"] = type,
        ["mediaType"] = "application/json"
    };

    private static JsonElement[] ReadOperations(string rawJson, bool requireArray)
    {
        using var document = JsonDocument.Parse(rawJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The money relation response is not a JSON object.");
        if (!document.RootElement.TryGetProperty("operations", out var operations) ||
            operations.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }
        if (operations.ValueKind != JsonValueKind.Array)
            throw new JsonException("The money relation response operations property is not an array.");
        return operations.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static JsonElement[] ReadOperationsSnapshot(string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("The saved money operations snapshot is not an array.");
        return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static bool IsPurchaseReturnReference(JsonElement operation, Guid purchaseReturnId)
    {
        if (operation.ValueKind != JsonValueKind.Object ||
            !operation.TryGetProperty("meta", out var meta) ||
            meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("type", out var type) ||
            !string.Equals(type.GetString(), "purchasereturn", StringComparison.OrdinalIgnoreCase) ||
            !meta.TryGetProperty("href", out var href) ||
            href.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return href.GetString()?.Contains(
                   purchaseReturnId.ToString("D"),
                   StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool TryReadDecimal(JsonElement element, string propertyName, out decimal value)
    {
        value = 0m;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetDecimal(out value);
        return property.ValueKind == JsonValueKind.String && decimal.TryParse(
            property.GetString(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static string SerializeOperations(IReadOnlyList<JsonElement> operations) =>
        JsonSerializer.Serialize(operations);

    private static void ValidateIds(IReadOnlyCollection<Guid> ids, string parameterName)
    {
        if (ids.Any(id => id == Guid.Empty))
            throw new ArgumentException("Purchase return ids must be non-empty.", parameterName);
    }

    private enum MoneyRelationKind
    {
        PaymentIn,
        CashIn
    }

    private sealed class SourceState(
        Guid purchaseReturnId,
        PurchaseReturnFactureRelationsSnapshot snapshot,
        string? errorCode = null,
        string? error = null)
    {
        public Guid PurchaseReturnId { get; } = purchaseReturnId;

        public PurchaseReturnFactureRelationsSnapshot Snapshot { get; } = snapshot;

        public List<RelationWork> Relations { get; } = [];

        public List<DetachedRelation> Detached { get; } = [];

        public string? Status { get; set; }

        public string? ErrorCode { get; set; } = errorCode;

        public string? Error { get; set; } = error;

        public bool HasFailure => ErrorCode is not null;

        public bool HasMoneyRelations => Snapshot.PaymentIns.Count != 0 || Snapshot.CashIns.Count != 0;

        public IEnumerable<MoneyRelationInput> MoneyRelations =>
            Snapshot.PaymentIns
                .Select(item => new MoneyRelationInput(MoneyRelationKind.PaymentIn, item.DocumentId))
                .Concat(Snapshot.CashIns.Select(item => new MoneyRelationInput(MoneyRelationKind.CashIn, item.DocumentId)));
    }

    private sealed record MoneyRelationInput(MoneyRelationKind Kind, Guid DocumentId);

    private sealed record MoneyDocumentKey(MoneyRelationKind Kind, Guid DocumentId);

    private sealed class LoadedMoneyDocument(
        MoneyDocumentKey key,
        JsonElement[] operations)
    {
        public MoneyDocumentKey Key { get; } = key;

        public JsonElement[] Operations { get; } = operations;
    }

    private sealed class RelationWork(
        SourceState state,
        MoneyRelationKind kind,
        Guid documentId,
        LoadedMoneyDocument document,
        decimal linkedSum)
    {
        public SourceState State { get; } = state;

        public MoneyRelationKind Kind { get; } = kind;

        public Guid DocumentId { get; } = documentId;

        public LoadedMoneyDocument Document { get; } = document;

        public decimal LinkedSum { get; } = linkedSum;
    }

    private sealed record DetachedRelation(RelationWork Relation, JsonElement[] AfterOperations);

    private sealed record DetachGroup(
        MoneyRelationKind Kind,
        Guid DocumentId,
        IReadOnlyList<RelationWork> Relations,
        LoadedMoneyDocument Document);

    private sealed record RollbackGroup(
        MoneyRelationKind Kind,
        Guid DocumentId,
        IReadOnlyList<DetachedRelation> Relations,
        LoadedMoneyDocument Document,
        JsonElement[] AfterOperations);

    private sealed class ReattachState(Guid purchaseReturnId, Guid newPurchaseReturnId)
    {
        public Guid PurchaseReturnId { get; } = purchaseReturnId;

        public Guid NewPurchaseReturnId { get; } = newPurchaseReturnId;

        public bool HasRelations { get; set; }

        public string? Status { get; set; }

        public string? ErrorCode { get; set; }

        public string? Error { get; set; }

        public bool HasFailure => ErrorCode is not null;

        public PurchaseReturnRelationsReattachItem ToResult() =>
            new(
                PurchaseReturnId,
                NewPurchaseReturnId,
                Status ?? "Failed",
                ErrorCode,
                Error);
    }

    private sealed class ReattachGroup(
        MoneyRelationKind kind,
        Guid documentId,
        JsonElement[] originalOperations)
    {
        public MoneyRelationKind Kind { get; } = kind;

        public Guid DocumentId { get; } = documentId;

        public JsonElement[] OriginalOperations { get; } = originalOperations;

        public List<ReattachRelationWork> Relations { get; } = [];
    }

    private sealed record ReattachRelation(
        MoneyRelationKind Kind,
        PurchaseReturnMoneyRelationSnapshot Snapshot);

    private sealed record ReattachRelationWork(
        ReattachState State,
        PurchaseReturnMoneyRelationSnapshot Snapshot);
}
