using System.Text.Json;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Gateways.Documents;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Models.Options;

namespace MsContractor.MoySkladEgressService.Services.Documents;

public interface IMoySkladMergeVerificationSnapshotService
{
    Task<MoySkladMergeVerificationSnapshotResponse> CaptureAsync(Guid accountId, IReadOnlyList<Guid> counterpartyIds,
        string correlationId, CancellationToken cancellationToken);
}

public sealed class MoySkladMergeVerificationSnapshotService(
    IMoySkladMergeVerificationGateway gateway,
    MoySkladDocumentDiscoveryOptions discoveryOptions) : IMoySkladMergeVerificationSnapshotService
{
    private const int PageSize = 1000;

    public async Task<MoySkladMergeVerificationSnapshotResponse> CaptureAsync(Guid accountId,
        IReadOnlyList<Guid> counterpartyIds, string correlationId, CancellationToken cancellationToken)
    {
        var requested = counterpartyIds.ToHashSet();
        var counterparties = new List<MoySkladMergeVerificationCounterparty>(counterpartyIds.Count);
        foreach (var counterpartyId in counterpartyIds)
        {
            var rawJson = await gateway.GetCounterpartyAsync(accountId, counterpartyId, correlationId, cancellationToken);
            if (ReadId(rawJson, "id") != counterpartyId)
                throw Invalid("MoySklad returned a counterparty with an unexpected id.");
            counterparties.Add(new MoySkladMergeVerificationCounterparty(counterpartyId, rawJson));
        }

        var documents = new List<MoySkladMergeVerificationDocument>();
        foreach (var documentType in discoveryOptions.DocumentTypes)
        {
            var pageIds = new HashSet<Guid>();
            int? expectedSize = null;
            var loaded = 0;
            for (var offset = 0; expectedSize is null || offset < expectedSize.Value; offset += PageSize)
            {
                var page = await gateway.GetDocumentPageAsync(accountId, documentType, counterpartyIds, PageSize, offset,
                    correlationId, cancellationToken);
                expectedSize ??= page.Size;
                if (page.Size != expectedSize || page.Limit != PageSize || page.Offset != offset || page.Rows.Count > PageSize)
                    throw Incomplete("MoySklad returned inconsistent document pagination metadata.");
                if (page.Rows.Count == 0 && loaded < expectedSize)
                    throw Incomplete("MoySklad returned an empty document page before loading completed.");

                foreach (var row in page.Rows)
                {
                    var documentId = ReadId(row, "id");
                    var counterpartyId = ReadAgentId(row);
                    if (!pageIds.Add(documentId))
                        throw Incomplete("MoySklad returned a duplicate document id during pagination.");
                    if (!requested.Contains(counterpartyId))
                        throw Invalid("MoySklad returned a document for an unexpected counterparty.");

                    var rawJson = await gateway.GetDocumentAsync(accountId, documentType, documentId, correlationId,
                        cancellationToken);
                    if (ReadId(rawJson, "id") != documentId || ReadAgentId(rawJson) != counterpartyId)
                        throw Invalid("MoySklad returned a document whose identity or agent changed during capture.");
                    var positions = await GetAllPositionsAsync(accountId, documentType, documentId, correlationId,
                        cancellationToken);
                    documents.Add(new MoySkladMergeVerificationDocument(documentType, documentId, counterpartyId,
                        rawJson, positions));
                }

                loaded += page.Rows.Count;
                if (loaded > expectedSize)
                    throw Incomplete("MoySklad returned more documents than declared by pagination metadata.");
            }

            if (loaded != expectedSize)
                throw Incomplete("MoySklad document capture did not load the declared number of documents.");
        }

        return new MoySkladMergeVerificationSnapshotResponse(counterparties, documents,
            discoveryOptions.DocumentTypes);
    }

    private async Task<IReadOnlyList<string>> GetAllPositionsAsync(Guid accountId, string documentType, Guid documentId,
        string correlationId, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        int? expectedSize = null;
        for (var offset = 0; expectedSize is null || offset < expectedSize.Value; offset += PageSize)
        {
            var page = await gateway.GetPositionsPageAsync(accountId, documentType, documentId, PageSize, offset,
                correlationId, cancellationToken);
            expectedSize ??= page.Size;
            if (page.Size != expectedSize || page.Limit != PageSize || page.Offset != offset || page.Rows.Count > PageSize)
                throw Incomplete("MoySklad returned inconsistent positions pagination metadata.");
            if (page.Rows.Count == 0 && result.Count < expectedSize)
                throw Incomplete("MoySklad returned an empty positions page before loading completed.");
            result.AddRange(page.Rows);
            if (result.Count > expectedSize)
                throw Incomplete("MoySklad returned more positions than declared by pagination metadata.");
        }
        if (result.Count != expectedSize)
            throw Incomplete("MoySklad positions capture did not load the declared number of positions.");
        return result;
    }

    private static Guid ReadId(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(property, out var id) || id.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(id.GetString(), out var value) || value == Guid.Empty)
            throw Invalid("MoySklad returned an entity without a valid id.");
        return value;
    }

    private static Guid ReadAgentId(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("agent", out var agent) || agent.ValueKind != JsonValueKind.Object ||
            !agent.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("href", out var href) || href.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(href.GetString(), UriKind.Absolute, out var uri) ||
            !Guid.TryParse(uri.AbsolutePath.TrimEnd('/').Split('/').Last(), out var result) || result == Guid.Empty)
            throw Invalid("MoySklad returned a document without a valid agent reference.");
        return result;
    }

    private static EgressException Invalid(string message) =>
        new(502, "MOYSKLAD_MERGE_VERIFICATION_INVALID_RESPONSE", message);
    private static EgressException Incomplete(string message) =>
        new(502, "MOYSKLAD_MERGE_VERIFICATION_INCOMPLETE", message);
}
