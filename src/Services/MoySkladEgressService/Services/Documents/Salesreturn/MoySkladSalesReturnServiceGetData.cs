using MsContractor.MoySkladEgressService.Gateways.Documents.Salesreturn;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Salesreturn;

public interface IMoySkladSalesReturnServiceGetData
{
    Task LoadAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        IReadOnlyList<Guid> salesReturnIds,
        CancellationToken cancellationToken);
}

public sealed class MoySkladSalesReturnServiceGetData : IMoySkladSalesReturnServiceGetData
{
    private readonly IMoySkladSalesReturnGateway _gateway;
    private readonly ISalesReturnRawDataRepository _repository;

    public MoySkladSalesReturnServiceGetData(
        IMoySkladSalesReturnGateway gateway,
        ISalesReturnRawDataRepository repository)
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
        if (salesReturnIds.Count == 0 ||
            salesReturnIds.Any(id => id == Guid.Empty) ||
            salesReturnIds.Distinct().Count() != salesReturnIds.Count)
            throw new ArgumentException(
                "A unique non-empty salesreturn id list is required.", nameof(salesReturnIds));

        foreach (var ids in salesReturnIds.Chunk(SalesReturnBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var documents = await _gateway.GetAsync(
                accountId, requestedByUserId, correlationId, ids, cancellationToken);

            await _repository.UpsertAsync(accountId, documents, cancellationToken);
        }
    }

    private const int SalesReturnBatchSize = 1000;
}
