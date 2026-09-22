using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;

namespace MsContractor.MoySkladEgressService.Repositories;

public interface IPurchaseReturnPreparationRepository
{
    Task<IReadOnlyDictionary<Guid, string>> GetRequiredDocumentsAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> purchaseReturnIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>>> GetRequiredPositionsAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> purchaseReturnIds,
        CancellationToken cancellationToken);

    Task UpsertDocumentsAsync(
        Guid accountId,
        IReadOnlyDictionary<Guid, string> documents,
        CancellationToken cancellationToken);

    Task ReplacePositionsAsync(
        Guid accountId,
        Guid purchaseReturnId,
        IReadOnlyDictionary<Guid, string> positions,
        CancellationToken cancellationToken);
}

public sealed class PurchaseReturnPreparationRepository : IPurchaseReturnPreparationRepository
{
    private readonly EgressDbContext _dbContext;

    public PurchaseReturnPreparationRepository(EgressDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetRequiredDocumentsAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> purchaseReturnIds,
        CancellationToken cancellationToken)
    {
        var ids = purchaseReturnIds.Distinct().ToArray();
        var result = await _dbContext.PurchaseReturnRawData
            .AsNoTracking()
            .Where(item => item.AccountId == accountId && ids.Contains(item.DocumentId))
            .ToDictionaryAsync(item => item.DocumentId, item => item.RawJson, cancellationToken);

        if (result.Count != ids.Length)
            throw new InvalidOperationException("Raw data is missing for one or more purchasereturn documents.");
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>>> GetRequiredPositionsAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> purchaseReturnIds,
        CancellationToken cancellationToken)
    {
        var ids = purchaseReturnIds.Distinct().ToArray();
        var rows = await _dbContext.PurchaseReturnPositionRawData
            .AsNoTracking()
            .Where(item => item.AccountId == accountId && ids.Contains(item.PurchaseReturnId))
            .ToListAsync(cancellationToken);
        var result = rows
            .GroupBy(item => item.PurchaseReturnId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<Guid, string>)group.ToDictionary(
                    item => item.PositionId,
                    item => item.RawJson));

        if (result.Count != ids.Length || result.Values.Any(positions => positions.Count == 0))
            throw new InvalidOperationException("Raw positions are missing for one or more purchasereturn documents.");
        return result;
    }

    public async Task UpsertDocumentsAsync(
        Guid accountId,
        IReadOnlyDictionary<Guid, string> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
            return;

        var ids = documents.Keys.ToArray();
        var existing = await _dbContext.PurchaseReturnRawData
            .Where(item => item.AccountId == accountId && ids.Contains(item.DocumentId))
            .ToDictionaryAsync(item => item.DocumentId, cancellationToken);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        foreach (var document in documents)
        {
            if (existing.TryGetValue(document.Key, out var row))
            {
                row.RawJson = document.Value;
                continue;
            }

            _dbContext.PurchaseReturnRawData.Add(new PurchaseReturnRawData
            {
                AccountId = accountId,
                DocumentId = document.Key,
                RawJson = document.Value
            });
        }

        await ReplaceRelatedDocumentsAsync(accountId, documents, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ReplaceRelatedDocumentsAsync(
        Guid accountId,
        IReadOnlyDictionary<Guid, string> documents,
        CancellationToken cancellationToken)
    {
        var purchaseReturnIds = documents.Keys.ToArray();

        var factureOut = new List<PurchaseReturnFactureOutRawData>();
        var factureIn = new List<PurchaseReturnFactureInRawData>();
        var paymentIn = new List<PurchaseReturnPaymentInRawData>();
        var cashIn = new List<PurchaseReturnCashInRawData>();

        foreach (var document in documents)
        {
            using var json = JsonDocument.Parse(document.Value);
            var root = json.RootElement;

            factureOut.AddRange(ReadRelations(
                root,
                "factureOut",
                "factureout",
                accountId,
                document.Key).Select(relation => new PurchaseReturnFactureOutRawData
                {
                    AccountId = accountId,
                    PurchaseReturnId = document.Key,
                    DocumentId = relation.DocumentId,
                    RawJson = relation.RawJson
                }));
            factureIn.AddRange(ReadRelations(
                root,
                "factureIn",
                "facturein",
                accountId,
                document.Key).Select(relation => new PurchaseReturnFactureInRawData
                {
                    AccountId = accountId,
                    PurchaseReturnId = document.Key,
                    DocumentId = relation.DocumentId,
                    RawJson = relation.RawJson
                }));

            foreach (var payment in ReadRelations(
                         root,
                         "payments",
                         expectedType: null,
                         accountId,
                         document.Key))
            {
                var paymentType = payment.Type;
                if (string.Equals(paymentType, "paymentin", StringComparison.OrdinalIgnoreCase))
                {
                    paymentIn.Add(new PurchaseReturnPaymentInRawData
                    {
                        AccountId = accountId,
                        PurchaseReturnId = document.Key,
                        DocumentId = payment.DocumentId,
                        RawJson = payment.RawJson
                    });
                }
                else if (string.Equals(paymentType, "cashin", StringComparison.OrdinalIgnoreCase))
                {
                    cashIn.Add(new PurchaseReturnCashInRawData
                    {
                        AccountId = accountId,
                        PurchaseReturnId = document.Key,
                        DocumentId = payment.DocumentId,
                        RawJson = payment.RawJson
                    });
                }
            }
        }

        EnsureUnique(factureOut.Select(item => (item.AccountId, item.PurchaseReturnId, item.DocumentId)), "factureout");
        EnsureUnique(factureIn.Select(item => (item.AccountId, item.PurchaseReturnId, item.DocumentId)), "facturein");
        EnsureUnique(paymentIn.Select(item => (item.AccountId, item.PurchaseReturnId, item.DocumentId)), "paymentin");
        EnsureUnique(cashIn.Select(item => (item.AccountId, item.PurchaseReturnId, item.DocumentId)), "cashin");

        var existingFactureOut = await _dbContext.PurchaseReturnFactureOutRawData
            .Where(item => item.AccountId == accountId && purchaseReturnIds.Contains(item.PurchaseReturnId))
            .ToListAsync(cancellationToken);
        var existingFactureIn = await _dbContext.PurchaseReturnFactureInRawData
            .Where(item => item.AccountId == accountId && purchaseReturnIds.Contains(item.PurchaseReturnId))
            .ToListAsync(cancellationToken);
        var existingPaymentIn = await _dbContext.PurchaseReturnPaymentInRawData
            .Where(item => item.AccountId == accountId && purchaseReturnIds.Contains(item.PurchaseReturnId))
            .ToListAsync(cancellationToken);
        var existingCashIn = await _dbContext.PurchaseReturnCashInRawData
            .Where(item => item.AccountId == accountId && purchaseReturnIds.Contains(item.PurchaseReturnId))
            .ToListAsync(cancellationToken);

        _dbContext.PurchaseReturnFactureOutRawData.RemoveRange(existingFactureOut);
        _dbContext.PurchaseReturnFactureInRawData.RemoveRange(existingFactureIn);
        _dbContext.PurchaseReturnPaymentInRawData.RemoveRange(existingPaymentIn);
        _dbContext.PurchaseReturnCashInRawData.RemoveRange(existingCashIn);

        _dbContext.PurchaseReturnFactureOutRawData.AddRange(factureOut);
        _dbContext.PurchaseReturnFactureInRawData.AddRange(factureIn);
        _dbContext.PurchaseReturnPaymentInRawData.AddRange(paymentIn);
        _dbContext.PurchaseReturnCashInRawData.AddRange(cashIn);
    }

    private static IEnumerable<RelatedDocument> ReadRelations(
        JsonElement root,
        string propertyName,
        string? expectedType,
        Guid accountId,
        Guid purchaseReturnId)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            yield break;

        IEnumerable<JsonElement> items = property.ValueKind switch
        {
            JsonValueKind.Object => [property],
            JsonValueKind.Array => property.EnumerateArray(),
            _ => throw InvalidRelationData(accountId, purchaseReturnId, propertyName,
                "The relation must be an object, an array or null.")
        };

        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw InvalidRelationData(accountId, purchaseReturnId, propertyName,
                    "The relation item must be a JSON object.");

            var type = ReadMetaType(item);
            if (expectedType is not null && !string.Equals(type, expectedType, StringComparison.OrdinalIgnoreCase))
                throw InvalidRelationData(accountId, purchaseReturnId, propertyName,
                    $"The relation has type '{type ?? "<null>"}' instead of '{expectedType}'.");

            var documentId = ReadDocumentId(item, accountId, purchaseReturnId, propertyName);
            yield return new RelatedDocument(documentId, type, item.GetRawText());
        }
    }

    private static string? ReadMetaType(JsonElement relation) =>
        relation.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
        meta.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    private static Guid ReadDocumentId(
        JsonElement relation,
        Guid accountId,
        Guid purchaseReturnId,
        string propertyName)
    {
        if (!relation.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("href", out var href) || href.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(href.GetString(), UriKind.Absolute, out var uri))
        {
            throw InvalidRelationData(accountId, purchaseReturnId, propertyName,
                "The relation has no valid meta.href.");
        }

        var rawId = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
        if (!Guid.TryParse(rawId, out var documentId) || documentId == Guid.Empty)
            throw InvalidRelationData(accountId, purchaseReturnId, propertyName,
                "The relation meta.href does not contain a valid document id.");

        return documentId;
    }

    private static InvalidOperationException InvalidRelationData(
        Guid accountId,
        Guid purchaseReturnId,
        string propertyName,
        string message) => new(
        $"Invalid purchasereturn relation: account_id={accountId:D}, purchasereturn_id={purchaseReturnId:D}, property={propertyName}. {message}");

    private static void EnsureUnique(
        IEnumerable<(Guid AccountId, Guid PurchaseReturnId, Guid DocumentId)> keys,
        string relationType)
    {
        var keyList = keys.ToArray();
        if (keyList.Distinct().Count() != keyList.Length)
            throw new InvalidOperationException($"Duplicate purchasereturn {relationType} relation was returned.");
    }

    private sealed record RelatedDocument(Guid DocumentId, string? Type, string RawJson);

    public async Task ReplacePositionsAsync(
        Guid accountId,
        Guid purchaseReturnId,
        IReadOnlyDictionary<Guid, string> positions,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.PurchaseReturnPositionRawData
            .Where(item => item.AccountId == accountId && item.PurchaseReturnId == purchaseReturnId)
            .ToListAsync(cancellationToken);
        _dbContext.PurchaseReturnPositionRawData.RemoveRange(existing);
        _dbContext.PurchaseReturnPositionRawData.AddRange(
            positions.Select(position => new PurchaseReturnPositionRawData
            {
                AccountId = accountId,
                PurchaseReturnId = purchaseReturnId,
                PositionId = position.Key,
                RawJson = position.Value
            }));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
