using MsContractor.Contracts.Internal;

namespace MsContractor.MoySkladEgressService.Services;

public interface IMoySkladDocumentDiscoveryService
{
    Task<MoySkladDocumentDiscoveryResponse> DiscoverAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        IReadOnlyList<Guid> counterpartyIds,
        CancellationToken cancellationToken);
}

public sealed class MoySkladDocumentDiscoveryService(
    IMoySkladDocumentGateway gateway,
    ILogger<MoySkladDocumentDiscoveryService> logger) : IMoySkladDocumentDiscoveryService
{
    public const int PageSize = 1000;

    public async Task<MoySkladDocumentDiscoveryResponse> DiscoverAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        IReadOnlyList<Guid> counterpartyIds,
        CancellationToken cancellationToken)
    {
        var requestedIds = counterpartyIds.ToHashSet();
        var documents = new List<MoySkladDocumentReference>();
        var counts = new List<MoySkladDocumentTypeCount>(SupportedMoySkladDocumentTypes.All.Count);

        foreach (var documentType in SupportedMoySkladDocumentTypes.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var documentIds = new HashSet<Guid>();
            var loadedCount = 0;
            var pagesCount = 0;
            int? expectedSize = null;

            for (var offset = 0; expectedSize is null || offset < expectedSize.Value; offset += PageSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await gateway.GetPageAsync(
                    accountId,
                    requestedByUserId,
                    correlationId,
                    documentType,
                    counterpartyIds,
                    PageSize,
                    offset,
                    cancellationToken);
                pagesCount++;

                expectedSize ??= page.Size;
                ValidatePage(page, expectedSize.Value, offset);

                if (page.Rows.Count == 0 && loadedCount < expectedSize.Value)
                    throw Incomplete("MoySklad returned an empty document page before discovery completed.");

                foreach (var row in page.Rows)
                {
                    if (row.DocumentId == Guid.Empty)
                        throw InvalidResponse("MoySklad returned a document without an id.");
                    if (!documentIds.Add(row.DocumentId))
                        throw Incomplete("MoySklad returned a duplicate document id during pagination.");

                    var counterpartyId = ParseCounterpartyId(row.AgentHref, row.AgentType);
                    if (!requestedIds.Contains(counterpartyId))
                        throw InvalidResponse("MoySklad returned a document for an unexpected counterparty.");

                    documents.Add(new MoySkladDocumentReference(documentType, row.DocumentId, counterpartyId));
                }

                loadedCount += page.Rows.Count;
                if (loadedCount > expectedSize.Value)
                    throw Incomplete("MoySklad returned more documents than declared by pagination metadata.");

                logger.LogInformation(
                    "MoySklad document page loaded: account_id={AccountId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, document_type={DocumentType}, counterparties_count={CounterpartiesCount}, page_offset={Offset}, page_limit={Limit}, expected_total={ExpectedTotal}, page_count={PageCount}, loaded_count={LoadedCount}, status={StatusCode}",
                    accountId,
                    requestedByUserId,
                    correlationId,
                    documentType,
                    counterpartyIds.Count,
                    offset,
                    PageSize,
                    expectedSize.Value,
                    page.Rows.Count,
                    loadedCount,
                    page.StatusCode);
            }

            if (loadedCount != expectedSize)
                throw Incomplete("MoySklad document discovery did not load the declared number of documents.");

            counts.Add(new MoySkladDocumentTypeCount(documentType, expectedSize.Value));
            logger.LogInformation(
                "MoySklad document discovery completed: account_id={AccountId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, document_type={DocumentType}, counterparties_count={CounterpartiesCount}, documents_count={DocumentsCount}, pages_count={PagesCount}",
                accountId,
                requestedByUserId,
                correlationId,
                documentType,
                counterpartyIds.Count,
                loadedCount,
                pagesCount);
        }

        return new MoySkladDocumentDiscoveryResponse(documents, counts);
    }

    private static void ValidatePage(MoySkladDocumentPage page, int expectedSize, int requestedOffset)
    {
        if (page.Size < 0 || page.Limit != PageSize || page.Offset != requestedOffset)
            throw Incomplete("MoySklad returned inconsistent document pagination metadata.");
        if (page.Size != expectedSize)
            throw Incomplete("MoySklad document count changed during pagination.");
        if (page.Rows.Count > PageSize)
            throw Incomplete("MoySklad returned a document page larger than requested.");
    }

    private static Guid ParseCounterpartyId(string href, string? type)
    {
        if (type is not null && !string.Equals(type, "counterparty", StringComparison.Ordinal))
            throw InvalidResponse("MoySklad returned a document with an invalid agent type.");
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
            throw InvalidResponse("MoySklad returned a document with an invalid agent href.");

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 3 ||
            !string.Equals(segments[^3], "entity", StringComparison.Ordinal) ||
            !string.Equals(segments[^2], "counterparty", StringComparison.Ordinal) ||
            !Guid.TryParse(segments[^1], out var counterpartyId) ||
            counterpartyId == Guid.Empty)
        {
            throw InvalidResponse("MoySklad returned a document with an invalid agent href.");
        }

        return counterpartyId;
    }

    private static EgressException InvalidResponse(string message) =>
        new(502, "MOYSKLAD_DOCUMENT_DISCOVERY_INVALID_RESPONSE", message);

    private static EgressException Incomplete(string message) =>
        new(502, "MOYSKLAD_DOCUMENT_DISCOVERY_INCOMPLETE", message);
}
