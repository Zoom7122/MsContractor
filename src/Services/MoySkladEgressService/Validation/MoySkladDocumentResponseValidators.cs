using MsContractor.MoySkladEgressService.Models;
using System.Text.Json;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.ResponseHandling;

namespace MsContractor.MoySkladEgressService.Validation;

public interface IMoySkladSingleDocumentResponseValidator
{
    string? Validate(JsonElement response, MoySkladDocumentChangeItem expected, Guid targetCounterpartyId);
}

public interface IMoySkladBulkDocumentResponseValidator
{
    (IReadOnlyList<MoySkladDocumentChangeItem> Succeeded, IReadOnlyList<MoySkladDocumentValidationFailure> Failed)
        Validate(JsonElement response, IReadOnlyList<MoySkladDocumentChangeItem> expected, Guid targetCounterpartyId);
}

public sealed class MoySkladSingleDocumentResponseValidator : IMoySkladSingleDocumentResponseValidator
{
    public string? Validate(JsonElement response, MoySkladDocumentChangeItem expected, Guid targetCounterpartyId)
    {
        if (!MoySkladDocumentJson.TryReadId(response, out var actualId))
            return $"document_id={expected.DocumentId:D}: response does not contain a valid document id.";
        if (actualId != expected.DocumentId)
            return $"document_id={expected.DocumentId:D}: response document id is {actualId:D}.";
        if (!MoySkladDocumentJson.TryReadAgentId(response, out var actualAgentId))
            return $"document_id={expected.DocumentId:D}: response does not contain a valid agent id; expected_agent_id={targetCounterpartyId:D}.";
        return actualAgentId == targetCounterpartyId
            ? null
            : $"document_id={expected.DocumentId:D}: expected_agent_id={targetCounterpartyId:D}, actual_agent_id={actualAgentId:D}.";
    }
}

public sealed class MoySkladBulkDocumentResponseValidator : IMoySkladBulkDocumentResponseValidator
{
    public (IReadOnlyList<MoySkladDocumentChangeItem> Succeeded, IReadOnlyList<MoySkladDocumentValidationFailure> Failed)
        Validate(JsonElement response, IReadOnlyList<MoySkladDocumentChangeItem> expected, Guid targetCounterpartyId)
    {
        if (response.ValueKind != JsonValueKind.Array)
        {
            return ([], expected.Select(item => new MoySkladDocumentValidationFailure(
                item, "Bulk response is not an array.")).ToArray());
        }

        var responseById = new Dictionary<Guid, JsonElement>();
        var duplicateIds = new HashSet<Guid>();
        foreach (var item in response.EnumerateArray())
        {
            if (!MoySkladDocumentJson.TryReadId(item, out var id)) continue;
            if (!responseById.TryAdd(id, item)) duplicateIds.Add(id);
        }

        var succeeded = new List<MoySkladDocumentChangeItem>();
        var failed = new List<MoySkladDocumentValidationFailure>();
        var itemErrors = response.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("errors", out _))
            .Select(item => MoySkladResponseHandler.ParseError(item.GetRawText()))
            .Where(item => item.Structured)
            .ToArray();
        var missingDocuments = expected.Where(item => !responseById.ContainsKey(item.DocumentId)).ToArray();
        foreach (var document in expected)
        {
            if (!responseById.TryGetValue(document.DocumentId, out var item))
            {
                var unambiguousError = missingDocuments.Length == 1 && itemErrors.Length == 1
                    ? itemErrors[0]
                    : default;
                failed.Add(new MoySkladDocumentValidationFailure(
                    document,
                    unambiguousError.Structured
                        ? $"document_id={document.DocumentId:D}: {unambiguousError.Message}"
                        : $"document_id={document.DocumentId:D}: document is missing from the bulk response.",
                    unambiguousError.Code is null
                        ? "MOYSKLAD_RESPONSE_VALIDATION_FAILED"
                        : $"MOYSKLAD_{unambiguousError.Code}"));
                continue;
            }
            if (duplicateIds.Contains(document.DocumentId))
            {
                failed.Add(new MoySkladDocumentValidationFailure(
                    document,
                    $"document_id={document.DocumentId:D}: document occurs more than once in the bulk response."));
                continue;
            }
            if (!MoySkladDocumentJson.TryReadAgentId(item, out var actualAgentId))
            {
                failed.Add(new MoySkladDocumentValidationFailure(
                    document,
                    $"document_id={document.DocumentId:D}: response does not contain a valid agent id; expected_agent_id={targetCounterpartyId:D}."));
                continue;
            }
            if (actualAgentId != targetCounterpartyId)
            {
                failed.Add(new MoySkladDocumentValidationFailure(
                    document,
                    $"document_id={document.DocumentId:D}: expected_agent_id={targetCounterpartyId:D}, actual_agent_id={actualAgentId:D}."));
                continue;
            }
            succeeded.Add(document);
        }
        return (succeeded, failed);
    }
}

internal static class MoySkladDocumentJson
{
    public static bool TryReadId(JsonElement item, out Guid id) =>
        TryReadGuid(item, "id", out id) && id != Guid.Empty;

    public static bool TryReadAgentId(JsonElement item, out Guid id) =>
        TryReadEntityId(item, "agent", out id);

    public static bool TryReadEntityId(JsonElement item, string property, out Guid id)
    {
        id = Guid.Empty;
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(property, out var entity) ||
            entity.ValueKind != JsonValueKind.Object)
            return false;
        if (TryReadGuid(entity, "id", out id) && id != Guid.Empty) return true;
        if (!entity.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("href", out var href) || href.ValueKind != JsonValueKind.String)
            return false;
        return TryParseHrefId(href.GetString(), out id);
    }

    private static bool TryReadGuid(JsonElement item, string property, out Guid id)
    {
        id = Guid.Empty;
        return item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
               Guid.TryParse(value.GetString(), out id);
    }

    private static bool TryParseHrefId(string? href, out Guid id)
    {
        id = Guid.Empty;
        return Uri.TryCreate(href, UriKind.Absolute, out var uri) &&
               Guid.TryParse(uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), out id) &&
               id != Guid.Empty;
    }
}
