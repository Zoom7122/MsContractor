using MsContractor.DuplicatesMergeService.Repositories;
using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.DuplicatesMergeService.Models;
using MsContractor.CatalogSyncService.Models;
using System.Text.Json;
using MsContractor.Contracts.Merge;

namespace MsContractor.DuplicatesMergeService.Services;

public interface IMergeJobCreator
{
    Task<MergeJobAccepted> CreateAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid correlationId,
        CreateMergeJobRequest request,
        CancellationToken cancellationToken);
}

public sealed class MergeJobCreator : IMergeJobCreator
{
    private readonly IMergeRepository _repository;
    private readonly ICounterpartyRepository _counterpartyRepository;
    private readonly TimeProvider _timeProvider;

    public MergeJobCreator(
        IMergeRepository repository,
        ICounterpartyRepository counterpartyRepository,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _counterpartyRepository = counterpartyRepository;
        _timeProvider = timeProvider;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

/// <summary>
/// Создание Создает задачу MERGE в бд чтобы worker потом ее взял
/// </summary>
/// <param name="accountId"></param>
/// <param name="requestedByUserId"></param>
/// <param name="correlationId"></param>
/// <param name="request"></param>
/// <param name="cancellationToken"></param>
/// <returns></returns>
/// <exception cref="MergeRequestException"></exception>
    public async Task<MergeJobAccepted> CreateAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid correlationId,
        CreateMergeJobRequest request,
        CancellationToken cancellationToken)
    {
        ValidateShape(request);
        var mainCounterparty = NormalizeAttributeJson(request.MainCounterparty);
        var ids = request.DuplicateCounterpartyIds.Append(request.MainCounterpartyId).ToArray();
        var counterparties = await _counterpartyRepository.GetSelectionAsync(accountId, ids, cancellationToken);
        if (counterparties.Count != ids.Length)
        {
            throw new MergeRequestException(
                MergeRequestError.NotFound,
                "COUNTERPARTY_NOT_FOUND",
                "One or more counterparties were not found.");
        }

        ValidateAttributes(mainCounterparty.Attributes, counterparties);

        if (counterparties.Single(item => item.Id == request.MainCounterpartyId).Archived)
        {
            throw new MergeRequestException(
                MergeRequestError.MainArchived,
                "MAIN_COUNTERPARTY_ARCHIVED",
                "The main counterparty is archived.");
        }

        var now = _timeProvider.GetUtcNow();
        var jobId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var command = new MergeRequested(
            1,
            messageId,
            correlationId,
            jobId,
            accountId,
            request.MainCounterpartyId,
            request.DuplicateCounterpartyIds,
            mainCounterparty,
            requestedByUserId,
            now);
        var job = new MergeJob
        {
            Id = jobId,
            MessageId = messageId,
            CorrelationId = correlationId,
            AccountId = accountId,
            MainCounterpartyId = request.MainCounterpartyId,
            RequestedByUserId = requestedByUserId,
            Status = MergeJobStatuses.Pending,
            PayloadVersion = 1,
            Payload = JsonSerializer.Serialize(mainCounterparty, JsonOptions),
            CreatedAt = now,
            UpdatedAt = now
        };
        job.Operations.Add(NewOperation(job, 0, MergeOperationTypes.DiscoverDocuments, request.MainCounterpartyId, now));
        job.Operations.Add(NewOperation(job, 1, MergeOperationTypes.UpdateMainCounterparty, request.MainCounterpartyId, now));
        job.Operations.Add(NewOperation(job, 2, MergeOperationTypes.ChangeDocumentCounterparties, request.MainCounterpartyId, now));
        job.Operations.Add(NewOperation(job, 3,
            MergeOperationTypes.RecreateSalesReturns, request.MainCounterpartyId, now));
        job.Operations.Add(NewOperation(job, 4,
            MergeOperationTypes.RecreatePurchaseReturns, request.MainCounterpartyId, now));
        job.Operations.Add(NewOperation(job, 5,
            MergeOperationTypes.RecreateFactureIns, request.MainCounterpartyId, now));
        job.Operations.Add(NewOperation(job, 6,
            MergeOperationTypes.RecreateFactureOuts, request.MainCounterpartyId, now));
        for (var index = 0; index < request.DuplicateCounterpartyIds.Count; index++)
        {
            job.Operations.Add(NewOperation(
                job,
                index + 7,
                MergeOperationTypes.ArchiveDuplicate,
                request.DuplicateCounterpartyIds[index],
                now));
        }

        var outbox = new SyncOutboxMessage
        {
            Id = messageId,
            Topic = MergeTopics.Commands,
            MessageKey = accountId.ToString("D"),
            EventType = nameof(MergeRequested),
            Payload = JsonSerializer.Serialize(command, JsonOptions),
            CreatedAt = now
        };
        await _repository.CreateAsync(accountId, job, outbox, ids, now, cancellationToken);
        return new MergeJobAccepted(jobId, MergeJobStatuses.Pending);
    }

    private static MergeOperation NewOperation(
        MergeJob job,
        int sequence,
        string type,
        Guid counterpartyId,
        DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            MergeJob = job,
            MergeJobId = job.Id,
            AccountId = job.AccountId,
            Sequence = sequence,
            OperationType = type,
            CounterpartyId = counterpartyId,
            Status = MergeOperationStatuses.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static void ValidateShape(CreateMergeJobRequest request)
    {
        var attributes = request.MainCounterparty?.Attributes;
        if (request.MainCounterpartyId == Guid.Empty ||
            request.MainCounterparty is null ||
            string.IsNullOrWhiteSpace(request.MainCounterparty.Name) ||
            request.MainCounterparty.Name.Length > 1024 ||
            request.MainCounterparty.Email?.Length > 320 ||
            request.MainCounterparty.Phone?.Length > 255 ||
            request.DuplicateCounterpartyIds is null ||
            request.DuplicateCounterpartyIds.Count == 0 ||
            request.DuplicateCounterpartyIds.Any(id => id == Guid.Empty) ||
            request.DuplicateCounterpartyIds.Distinct().Count() != request.DuplicateCounterpartyIds.Count ||
            request.DuplicateCounterpartyIds.Contains(request.MainCounterpartyId) ||
            attributes is not null &&
            (attributes.Any(attribute => attribute is null || attribute.Id == Guid.Empty ||
                                          attribute.SourceCounterpartyId == Guid.Empty ||
                                          string.IsNullOrWhiteSpace(attribute.Type) || attribute.Type.Length > 255) ||
             attributes.Select(attribute => attribute.Id).Distinct().Count() != attributes.Count))
        {
            throw InvalidMergeRequest();
        }
    }

    private static void ValidateAttributes(
        IReadOnlyList<MergeMainCounterpartyAttributeDto>? requestedAttributes,
        IReadOnlyList<CounterpartySelectionItem> counterparties)
    {
        if (requestedAttributes is null or { Count: 0 })
            return;

        var counterpartiesById = counterparties.ToDictionary(item => item.Id);
        var parsedAttributes = counterparties.ToDictionary(
            item => item.Id,
            item => MergeCounterpartyAttributesParser.Parse(item.RawJson));
        var knownAttributes = parsedAttributes.Values
            .SelectMany(attributes => attributes)
            .GroupBy(attribute => attribute.Id)
            .ToDictionary(group => group.Key, group => group.ToArray());

        foreach (var requested in requestedAttributes)
        {
            if (!counterpartiesById.ContainsKey(requested.SourceCounterpartyId) ||
                !knownAttributes.TryGetValue(requested.Id, out var definitions) ||
                definitions.Any(attribute => !string.IsNullOrWhiteSpace(attribute.Type) && attribute.Type != requested.Type))
            {
                throw InvalidMergeRequest();
            }

            if (requested.Clear)
            {
                if (!MergeCounterpartyAttributesParser.ValuesEqual(requested.Value, null) ||
                    !MergeCounterpartyAttributesParser.ValuesEqual(requested.File, null))
                {
                    throw InvalidMergeRequest();
                }

                continue;
            }

            var sourceAttribute = parsedAttributes[requested.SourceCounterpartyId]
                .FirstOrDefault(attribute => attribute.Id == requested.Id);
            if (sourceAttribute is null)
            {
                if (!MergeCounterpartyAttributesParser.ValuesEqual(requested.Value, null) ||
                    !MergeCounterpartyAttributesParser.ValuesEqual(requested.File, null))
                {
                    throw InvalidMergeRequest();
                }

                continue;
            }

            if (sourceAttribute.Type != requested.Type ||
                !MergeCounterpartyAttributesParser.ValuesEqual(sourceAttribute.Value, requested.Value) ||
                !MergeCounterpartyAttributesParser.ValuesEqual(sourceAttribute.File, requested.File))
            {
                throw InvalidMergeRequest();
            }
        }
    }

    private static MergeMainCounterpartyDto NormalizeAttributeJson(MergeMainCounterpartyDto mainCounterparty)
    {
        if (mainCounterparty.Attributes is null or { Count: 0 })
            return mainCounterparty;

        var attributes = mainCounterparty.Attributes.Select(attribute => attribute with
        {
            Value = ParseJson(attribute.ValueJson, attribute.Value),
            File = ParseJson(attribute.FileJson, attribute.File),
            ValueJson = null,
            FileJson = null
        }).ToArray();
        return mainCounterparty with { Attributes = attributes };
    }

    private static System.Text.Json.JsonElement? ParseJson(
        string? rawJson,
        System.Text.Json.JsonElement? fallback)
    {
        if (rawJson is null)
            return fallback;

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw InvalidMergeRequest();
        }
    }

    private static MergeRequestException InvalidMergeRequest() => new(
        MergeRequestError.Invalid,
        "INVALID_MERGE_REQUEST",
        "Merge request contains invalid fields.");
}
