using MsContractor.DuplicatesMergeService.Repositories;
using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.DuplicatesMergeService.Models;
using MsContractor.Contracts.Merge;

namespace MsContractor.DuplicatesMergeService.Services;

public interface IMergeSelectionPreviewService
{
    Task<MergeSelectionPreviewResponse> GetAsync(
        Guid accountId,
        MergeSelectionPreviewRequest request,
        CancellationToken cancellationToken);
}

public sealed class MergeSelectionPreviewService : IMergeSelectionPreviewService
{
    private readonly ICounterpartyRepository _repository;

    public MergeSelectionPreviewService(
        ICounterpartyRepository repository)
    {
        _repository = repository;
    }

    public async Task<MergeSelectionPreviewResponse> GetAsync(
        Guid accountId,
        MergeSelectionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);

        var ids = request.CounterpartyIds.ToArray();
        var counterparties = await _repository.GetSelectionAsync(accountId, ids, cancellationToken);

        if (counterparties.Count != ids.Length)
        {
            throw new MergeSelectionPreviewException(
                MergeSelectionPreviewError.NotFound,
                "COUNTERPARTY_NOT_FOUND",
                "One or more counterparties were not found.");
        }

        if (await _repository.HasMergeLocksAsync(accountId, ids, cancellationToken))
        {
            throw new MergeSelectionPreviewException(
                MergeSelectionPreviewError.Busy,
                "COUNTERPARTY_BUSY",
                "One or more counterparties are already being merged.");
        }

        var byId = counterparties.ToDictionary(item => item.Id, item => new MergeSelectionCounterpartyDto(
            item.Id, item.Name, item.Description, item.Email, item.Phone, item.Archived, item.UpdatedAt,
            MergeCounterpartyAttributesParser.Parse(item.RawJson)));
        return new MergeSelectionPreviewResponse(ids.Select(id => byId[id]).ToArray());
    }

    private static void Validate(MergeSelectionPreviewRequest request)
    {
        if (request.CounterpartyIds is null ||
            request.CounterpartyIds.Count < 2 ||
            request.CounterpartyIds.Any(id => id == Guid.Empty) ||
            request.CounterpartyIds.Distinct().Count() != request.CounterpartyIds.Count)
        {
            throw new MergeSelectionPreviewException(
                MergeSelectionPreviewError.Invalid,
                "INVALID_MERGE_PREVIEW_REQUEST",
                "At least two unique counterparty ids are required.");
        }
    }
}
