using System.Text.Json;
using MsContractor.MoySkladEgressService.Gateways.Documents.Purchasereturn;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Purchasereturn;

public interface IPurchaseReturnPreparationService
{
    Task<PurchaseReturnPreparationResult> PrepareAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> purchaseReturnIds,
        CancellationToken cancellationToken);
}

public sealed class PurchaseReturnPreparationService : IPurchaseReturnPreparationService
{
    private readonly IMoySkladPurchaseReturnGateway _purchaseReturns;
    private readonly IMoySkladPurchaseReturnPositionsGateway _positions;
    private readonly IMoySkladSupplyGateway _supplies;
    private readonly IPurchaseReturnPreparationRepository _repository;

    public PurchaseReturnPreparationService(
        IMoySkladPurchaseReturnGateway purchaseReturns,
        IMoySkladPurchaseReturnPositionsGateway positions,
        IMoySkladSupplyGateway supplies,
        IPurchaseReturnPreparationRepository repository)
    {
        _purchaseReturns = purchaseReturns;
        _positions = positions;
        _supplies = supplies;
        _repository = repository;
    }

    public async Task<PurchaseReturnPreparationResult> PrepareAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> purchaseReturnIds,
        CancellationToken cancellationToken)
    {
        ValidateInput(accountId, mainCounterpartyId, purchaseReturnIds);
        var correlationId = Guid.NewGuid().ToString("D");
        var skipped = new List<PurchaseReturnSkippedDocument>();
        var documents = await _purchaseReturns.GetAsync(
            accountId, Guid.Empty, correlationId, purchaseReturnIds, cancellationToken);

        foreach (var purchaseReturnId in purchaseReturnIds)
        {
            if (!documents.ContainsKey(purchaseReturnId))
                skipped.Add(new PurchaseReturnSkippedDocument(
                    purchaseReturnId, "purchasereturn was not returned by MoySklad."));
        }

        await _repository.UpsertDocumentsAsync(accountId, documents, cancellationToken);

        var positionsByDocument = new Dictionary<Guid, IReadOnlyDictionary<Guid, string>>();
        foreach (var purchaseReturnId in purchaseReturnIds)
        {
            if (!documents.ContainsKey(purchaseReturnId))
                continue;

            try
            {
                var positions = await LoadPositionsAsync(
                    accountId, correlationId, purchaseReturnId, cancellationToken);
                if (positions.Count == 0)
                {
                    skipped.Add(new PurchaseReturnSkippedDocument(
                        purchaseReturnId, "purchasereturn has no positions."));
                    continue;
                }

                positionsByDocument[purchaseReturnId] = positions;
            }
            catch (EgressException exception) when (exception.StatusCode == 404 || exception.HttpStatus == 404)
            {
                skipped.Add(new PurchaseReturnSkippedDocument(
                    purchaseReturnId, "purchasereturn positions were not found in MoySklad."));
            }
        }

        foreach (var item in positionsByDocument)
        {
            await _repository.ReplacePositionsAsync(
                accountId, item.Key, item.Value, cancellationToken);
        }

        var supplyByDocument = new Dictionary<Guid, Guid>();
        foreach (var item in documents)
        {
            if (!positionsByDocument.ContainsKey(item.Key))
                continue;

            if (!TryReadSupplyId(item.Value, out var supplyId))
            {
                skipped.Add(new PurchaseReturnSkippedDocument(
                    item.Key, "purchasereturn has no valid supply reference."));
                continue;
            }

            supplyByDocument[item.Key] = supplyId;
        }

        var supplyIds = supplyByDocument.Values.Distinct().ToArray();
        var supplies = await _supplies.GetAsync(
            accountId, Guid.Empty, correlationId, supplyIds, cancellationToken);
        var ready = new List<Guid>();

        foreach (var purchaseReturnId in purchaseReturnIds)
        {
            if (!supplyByDocument.TryGetValue(purchaseReturnId, out var supplyId))
                continue;

            if (!supplies.TryGetValue(supplyId, out var supply))
            {
                skipped.Add(new PurchaseReturnSkippedDocument(
                    purchaseReturnId, "related supply was not returned by MoySklad."));
                continue;
            }

            if (supply.AgentId is null)
            {
                skipped.Add(new PurchaseReturnSkippedDocument(
                    purchaseReturnId, "related supply has no valid agent."));
                continue;
            }

            if (supply.AgentId != mainCounterpartyId)
            {
                skipped.Add(new PurchaseReturnSkippedDocument(
                    purchaseReturnId, "related supply agent does not match main counterparty."));
                continue;
            }

            ready.Add(purchaseReturnId);
        }

        return new PurchaseReturnPreparationResult(
            ready,
            skipped
                .GroupBy(item => item.PurchaseReturnId)
                .Select(group => group.First())
                .ToArray());
    }

    private async Task<IReadOnlyDictionary<Guid, string>> LoadPositionsAsync(
        Guid accountId,
        string correlationId,
        Guid purchaseReturnId,
        CancellationToken cancellationToken)
    {
        var positions = new Dictionary<Guid, string>();
        int? expectedSize = null;

        for (var offset = 0; expectedSize is null || offset < expectedSize.Value; offset += PositionPageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _positions.GetPageAsync(
                accountId,
                Guid.Empty,
                correlationId,
                purchaseReturnId,
                PositionPageSize,
                offset,
                cancellationToken);

            expectedSize ??= page.Size;
            if (expectedSize != page.Size)
                throw new InvalidOperationException(
                    $"MoySklad returned changing positions size for purchasereturn {purchaseReturnId:D}.");

            foreach (var position in page.Positions)
            {
                if (!positions.TryAdd(position.Key, position.Value))
                    throw new InvalidOperationException(
                        $"MoySklad returned duplicate position {position.Key:D} for purchasereturn {purchaseReturnId:D}.");
            }
        }

        if (expectedSize is null || positions.Count != expectedSize.Value)
            throw new InvalidOperationException(
                $"MoySklad returned incomplete positions for purchasereturn {purchaseReturnId:D}.");

        return positions;
    }

    private static bool TryReadSupplyId(string rawJson, out Guid supplyId)
    {
        supplyId = Guid.Empty;
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (!document.RootElement.TryGetProperty("supply", out var supply) ||
                supply.ValueKind != JsonValueKind.Object ||
                !supply.TryGetProperty("meta", out var meta) ||
                meta.ValueKind != JsonValueKind.Object ||
                !meta.TryGetProperty("href", out var href) ||
                href.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(href.GetString(), UriKind.Absolute, out var uri))
                return false;

            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return segments.Length >= 3 &&
                   string.Equals(segments[^3], "entity", StringComparison.Ordinal) &&
                   string.Equals(segments[^2], "supply", StringComparison.Ordinal) &&
                   Guid.TryParse(segments[^1], out supplyId) &&
                   supplyId != Guid.Empty;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ValidateInput(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> purchaseReturnIds)
    {
        if (accountId == Guid.Empty || mainCounterpartyId == Guid.Empty ||
            purchaseReturnIds.Count == 0 || purchaseReturnIds.Any(id => id == Guid.Empty) ||
            purchaseReturnIds.Distinct().Count() != purchaseReturnIds.Count)
            throw new ArgumentException("A non-empty account, main counterparty and unique purchasereturn ids are required.");
    }

    private const int PositionPageSize = MoySkladPurchaseReturnPositionsGateway.PageSize;
}
