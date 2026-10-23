using System.Text.Json;
using MsContractor.MoySkladEgressService.Gateways.Documents.Demand;
using MsContractor.MoySkladEgressService.Gateways.Documents.Salesreturn;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Salesreturn;

public interface IMoySkladSalesReturnServiceGetData
{
    Task<IReadOnlyDictionary<Guid, SalesReturnDemandValidationFailure>> LoadAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid mainCounterpartyId,
        string correlationId,
        IReadOnlyList<Guid> salesReturnIds,
        CancellationToken cancellationToken);
}

public sealed record SalesReturnDemandValidationFailure(string ErrorCode, string Error);

public sealed class MoySkladSalesReturnServiceGetData : IMoySkladSalesReturnServiceGetData
{
    private readonly IMoySkladSalesReturnGateway _gateway;
    private readonly IMoySkladDemandGateway _demandGateway;
    private readonly ISalesReturnRawDataRepository _repository;

    public MoySkladSalesReturnServiceGetData(
        IMoySkladSalesReturnGateway gateway,
        IMoySkladDemandGateway demandGateway,
        ISalesReturnRawDataRepository repository)
    {
        _gateway = gateway;
        _demandGateway = demandGateway;
        _repository = repository;
    }

    public async Task<IReadOnlyDictionary<Guid, SalesReturnDemandValidationFailure>> LoadAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid mainCounterpartyId,
        string correlationId,
        IReadOnlyList<Guid> salesReturnIds,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty || mainCounterpartyId == Guid.Empty || salesReturnIds.Count == 0 ||
            salesReturnIds.Any(id => id == Guid.Empty) ||
            salesReturnIds.Distinct().Count() != salesReturnIds.Count)
            throw new ArgumentException(
                "A non-empty account, main counterparty and unique salesreturn id list are required.",
                nameof(salesReturnIds));

        var failures = new Dictionary<Guid, SalesReturnDemandValidationFailure>();
        foreach (var ids in salesReturnIds.Chunk(SalesReturnBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var documents = await _gateway.GetAsync(
                accountId, requestedByUserId, correlationId, ids, cancellationToken);

            await _repository.UpsertAsync(accountId, documents, cancellationToken);

            var batchFailures = await ValidateDemandOwnershipAsync(
                accountId, requestedByUserId, mainCounterpartyId, correlationId, documents, cancellationToken);
            foreach (var failure in batchFailures)
                failures.Add(failure.Key, failure.Value);
        }

        return failures;
    }

    private async Task<IReadOnlyDictionary<Guid, SalesReturnDemandValidationFailure>> ValidateDemandOwnershipAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid mainCounterpartyId,
        string correlationId,
        IReadOnlyDictionary<Guid, string> salesReturns,
        CancellationToken cancellationToken)
    {
        var failures = new Dictionary<Guid, SalesReturnDemandValidationFailure>();
        var demandIdsBySalesReturn = new Dictionary<Guid, Guid>();

        foreach (var (salesReturnId, rawJson) in salesReturns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasFactureOut(rawJson))
                continue;

            var demandReference = ReadDemandReference(rawJson);
            if (demandReference.Error is not null)
            {
                failures[salesReturnId] = demandReference.Error;
                continue;
            }

            if (demandReference.DemandId is Guid demandId)
                demandIdsBySalesReturn.Add(salesReturnId, demandId);
        }

        var demandIds = demandIdsBySalesReturn.Values.Distinct().ToArray();
        if (demandIds.Length == 0)
            return failures;

        var demands = await _demandGateway.GetAsync(
            accountId, requestedByUserId, correlationId, demandIds, cancellationToken);

        foreach (var (salesReturnId, demandId) in demandIdsBySalesReturn)
        {
            if (!demands.TryGetValue(demandId, out var demand))
            {
                failures[salesReturnId] = new SalesReturnDemandValidationFailure(
                    "SALESRETURN_DEMAND_NOT_FOUND",
                    "The salesreturn demand was not returned by MoySklad.");
                continue;
            }

            if (demand.AgentId is not Guid agentId)
            {
                failures[salesReturnId] = new SalesReturnDemandValidationFailure(
                    "SALESRETURN_DEMAND_AGENT_MISSING",
                    "The salesreturn demand has no valid counterparty agent.");
                continue;
            }

            if (agentId != mainCounterpartyId)
            {
                failures[salesReturnId] = new SalesReturnDemandValidationFailure(
                    "SALESRETURN_DEMAND_COUNTERPARTY_MISMATCH",
                    "The salesreturn demand does not belong to the main counterparty.");
            }
        }

        return failures;
    }

    private static bool HasFactureOut(string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
               document.RootElement.TryGetProperty("factureOut", out _);
    }

    private static DemandReferenceReadResult ReadDemandReference(string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("demand", out var demand) ||
            demand.ValueKind == JsonValueKind.Null)
            return new DemandReferenceReadResult(null, null);

        if (demand.ValueKind != JsonValueKind.Object ||
            !demand.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            !string.Equals(type.GetString(), "demand", StringComparison.OrdinalIgnoreCase) ||
            !meta.TryGetProperty("href", out var href) || href.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(href.GetString(), UriKind.Absolute, out var uri))
            return new DemandReferenceReadResult(null, new SalesReturnDemandValidationFailure(
                "SALESRETURN_DEMAND_REFERENCE_INVALID",
                "The salesreturn contains an invalid demand reference."));

        var segments = uri.AbsolutePath.Split(
            '/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 3 ||
            !string.Equals(segments[^3], "entity", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[^2], "demand", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(segments[^1], out var demandId) || demandId == Guid.Empty)
            return new DemandReferenceReadResult(null, new SalesReturnDemandValidationFailure(
                "SALESRETURN_DEMAND_REFERENCE_INVALID",
                "The salesreturn contains an invalid demand reference."));

        return new DemandReferenceReadResult(demandId, null);
    }

    private sealed record DemandReferenceReadResult(
        Guid? DemandId,
        SalesReturnDemandValidationFailure? Error);

    private const int SalesReturnBatchSize = 1000;
}
