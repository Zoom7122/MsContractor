using MsContractor.MoySkladEgressService.Gateways.Documents.Salesreturn;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Salesreturn;

public interface IMoySkladSalesReturnPositionsService
{
    Task LoadAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        IReadOnlyList<Guid> salesReturnIds,
        CancellationToken cancellationToken);
}

public sealed class MoySkladSalesReturnPositionsService : IMoySkladSalesReturnPositionsService
{
    private readonly IMoySkladSalesReturnPositionsGateway _gateway;
    private readonly ISalesReturnPositionRawDataRepository _repository;

    public MoySkladSalesReturnPositionsService(
        IMoySkladSalesReturnPositionsGateway gateway,
        ISalesReturnPositionRawDataRepository repository)
    {
        _gateway = gateway;
        _repository = repository;
    }

    public async Task LoadAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        IReadOnlyList<Guid> salesReturnIds,
        CancellationToken cancellationToken)
    {
        ValidateSalesReturnIds(salesReturnIds);

        foreach (var salesReturnBatch in salesReturnIds.Chunk(SalesReturnBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var salesReturnId in salesReturnBatch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var positions = new Dictionary<Guid, string>();
                int? expectedSize = null;

                for (var offset = 0; expectedSize is null || offset < expectedSize.Value; offset += PositionPageSize)
                {
                    var page = await _gateway.GetPageAsync(
                        accountId,
                        requestedByUserId,
                        correlationId,
                        salesReturnId,
                        PositionPageSize,
                        offset,
                        cancellationToken);

                    if (expectedSize is not null && expectedSize != page.Size)
                        throw new InvalidOperationException(
                            $"MoySklad returned changing positions size for salesreturn {salesReturnId:D}.");

                    expectedSize ??= page.Size;
                    foreach (var position in page.Positions)
                    {
                        if (!positions.TryAdd(position.Key, position.Value))
                            throw new InvalidOperationException(
                                $"MoySklad returned a duplicate position {position.Key:D} for salesreturn {salesReturnId:D}.");
                    }
                }

                if (positions.Count != expectedSize)
                    throw new InvalidOperationException(
                        $"MoySklad returned incomplete positions for salesreturn {salesReturnId:D}.");

                await _repository.ReplaceAsync(
                    accountId, salesReturnId, positions, cancellationToken);
            }
        }
    }

    private static void ValidateSalesReturnIds(IReadOnlyList<Guid> salesReturnIds)
    {
        if (salesReturnIds.Count == 0 ||
            salesReturnIds.Any(id => id == Guid.Empty) ||
            salesReturnIds.Distinct().Count() != salesReturnIds.Count)
            throw new ArgumentException(
                "A unique non-empty salesreturn id list is required.", nameof(salesReturnIds));
    }

    private const int SalesReturnBatchSize = 1000;
    private const int PositionPageSize = MoySkladSalesReturnPositionsGateway.PageSize;
}
