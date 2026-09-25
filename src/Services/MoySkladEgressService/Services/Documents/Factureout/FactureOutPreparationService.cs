using System.Text.Json;
using MsContractor.MoySkladEgressService.Gateways.Documents.Factureout;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Factureout;

public interface IFactureOutPreparationService
{
    Task<FactureOutPreparationResult> PrepareAsync(
        Guid accountId,
        IReadOnlyList<Guid> factureOutIds,
        CancellationToken cancellationToken);
}

public sealed class FactureOutPreparationService : IFactureOutPreparationService
{
    private static readonly (string Field, string[] Types)[] BaseRelations =
    [
        ("demands", ["demand"]),
        ("returns", ["purchasereturn"]),
        ("payments", ["paymentin", "cashin"])
    ];

    private readonly IMoySkladFactureOutGateway _gateway;
    private readonly IFactureOutRawDataRepository _repository;
    private readonly IFactureOutRecreationItemRepository _recreationItems;
    private readonly ILogger<FactureOutPreparationService> _logger;

    public FactureOutPreparationService(
        IMoySkladFactureOutGateway gateway,
        IFactureOutRawDataRepository repository,
        IFactureOutRecreationItemRepository recreationItems,
        ILogger<FactureOutPreparationService> logger)
    {
        _gateway = gateway;
        _repository = repository;
        _recreationItems = recreationItems;
        _logger = logger;
    }

    public async Task<FactureOutPreparationResult> PrepareAsync(
        Guid accountId,
        IReadOnlyList<Guid> factureOutIds,
        CancellationToken cancellationToken)
    {
        ValidateInput(accountId, factureOutIds);

        var correlationId = Guid.NewGuid().ToString("D");
        var documentIds = new HashSet<Guid>();
        var returnedDocumentIds = new HashSet<Guid>();
        var skipped = new List<FactureOutSkippedDocumentResult>();

        foreach (var batch in factureOutIds.Chunk(MoySkladFactureOutGateway.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchDocuments = await _gateway.GetAsync(
                accountId,
                correlationId,
                batch,
                cancellationToken);

            await _repository.UpsertAsync(accountId, batchDocuments, cancellationToken);

            var sourceSyncIds = batchDocuments.ToDictionary(
                document => document.Key,
                document => ReadSourceSyncId(document.Value));
            var documentsWithoutBase = batchDocuments
                .Where(document => !HasBaseRelation(document.Value))
                .Select(document => document.Key)
                .ToArray();
            var notFoundIds = batch
                .Where(documentId => !batchDocuments.ContainsKey(documentId))
                .ToArray();
            var skippedIds = notFoundIds.Concat(documentsWithoutBase).ToArray();
            await _recreationItems.UpsertPreparationAsync(
                accountId,
                sourceSyncIds,
                skippedIds,
                cancellationToken);

            foreach (var document in batchDocuments)
            {
                if (!returnedDocumentIds.Add(document.Key))
                {
                    throw new InvalidOperationException(
                        $"MoySklad returned duplicate factureout {document.Key:D} across batches.");
                }

                if (!documentsWithoutBase.Contains(document.Key))
                    documentIds.Add(document.Key);
            }

            skipped.AddRange(notFoundIds
                .Select(documentId => new FactureOutSkippedDocumentResult(
                    documentId,
                    "Skipped",
                    "FACTUREOUT_NOT_FOUND",
                    "factureout was not returned by MoySklad.")));
            skipped.AddRange(documentsWithoutBase
                .Select(documentId => new FactureOutSkippedDocumentResult(
                    documentId,
                    "Skipped",
                    "FACTUREOUT_BASE_MISSING",
                    "factureout has no demand, purchasereturn, or payment basis.")));

            _logger.LogInformation(
                "factureout raw batch saved: account_id={AccountId}, requested_count={RequestedCount}, returned_count={ReturnedCount}, prepared_count={PreparedCount}, skipped_without_base_count={SkippedWithoutBaseCount}, correlation_id={CorrelationId}",
                accountId,
                batch.Length,
                batchDocuments.Count,
                batchDocuments.Count - documentsWithoutBase.Length,
                documentsWithoutBase.Length,
                correlationId);
        }

        return new FactureOutPreparationResult(documentIds.ToArray(), skipped);
    }

    private static bool HasBaseRelation(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            return BaseRelations.Any(relation =>
                document.RootElement.TryGetProperty(relation.Field, out var value) &&
                ContainsRelationReference(value, relation.Types));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsRelationReference(JsonElement collection, IReadOnlyCollection<string> expectedTypes)
    {
        var rows = collection.ValueKind switch
        {
            JsonValueKind.Array => collection,
            JsonValueKind.Object when collection.TryGetProperty("rows", out var rowsElement) &&
                                      rowsElement.ValueKind == JsonValueKind.Array => rowsElement,
            _ => default
        };

        return rows.ValueKind == JsonValueKind.Array && rows.EnumerateArray().Any(row =>
            IsRelationReference(row, expectedTypes));
    }

    private static bool IsRelationReference(JsonElement row, IReadOnlyCollection<string> expectedTypes)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty("meta", out var meta) ||
            meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("href", out var href) ||
            href.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(href.GetString(), UriKind.Absolute, out var uri) ||
            !meta.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            !expectedTypes.Contains(type.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var pathSegments = uri.AbsolutePath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return pathSegments.Length >= 2 &&
               string.Equals(pathSegments[^2], type.GetString(), StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(pathSegments[^1]);
    }

    private static Guid? ReadSourceSyncId(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("syncId", out var syncIdElement) &&
                syncIdElement.ValueKind == JsonValueKind.String &&
                Guid.TryParse(syncIdElement.GetString(), out var syncId) &&
                syncId != Guid.Empty)
            {
                return syncId;
            }
        }
        catch (JsonException)
        {
            // The raw response has already been saved; an unavailable source sync id is optional.
        }

        return null;
    }

    private static void ValidateInput(Guid accountId, IReadOnlyList<Guid> factureOutIds)
    {
        if (accountId == Guid.Empty ||
            factureOutIds.Count == 0 ||
            factureOutIds.Any(id => id == Guid.Empty) ||
            factureOutIds.Distinct().Count() != factureOutIds.Count)
        {
            throw new ArgumentException(
                "A non-empty account and unique factureout ids are required.",
                nameof(factureOutIds));
        }
    }
}
