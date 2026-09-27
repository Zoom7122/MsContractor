using MsContractor.DuplicatesMergeService.Models;
using MsContractor.DuplicatesMergeService.Repositories;
using System.Text.Json;
using MsContractor.Contracts.Duplicates;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Services;

namespace MsContractor.DuplicatesMergeService.Services;

public interface IDuplicatePreviewService
{
    Task<IReadOnlyList<DuplicateGroupDto>> FindAsync(
        Guid accountId,
        IReadOnlyCollection<DuplicateMatchField> fields,
        CancellationToken cancellationToken);
}

public sealed class DuplicatePreviewService : IDuplicatePreviewService
{
    private readonly ICounterpartyRepository _repository;

    public DuplicatePreviewService(
        ICounterpartyRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<DuplicateGroupDto>> FindAsync(
        Guid accountId,
        IReadOnlyCollection<DuplicateMatchField> fields,
        CancellationToken cancellationToken)
    {

        var candidates = await _repository.GetCandidatesAsync(accountId, cancellationToken);
        var exclusions = await _repository.GetDuplicateSearchExclusionsAsync(accountId, cancellationToken);

        if (exclusions.Count > 0)
        {
            var excludedValues = new HashSet<(string Field, string Value)>();
            foreach (var exclusion in exclusions)
            {
                if (ToNormalizedExclusion(exclusion) is { } normalized)
                    excludedValues.Add(normalized);
            }

            candidates = candidates
                .Where(candidate => !IsExcluded(candidate, excludedValues))
                .ToArray();
        }

        var groups = new Dictionary<string, DuplicateGroupCandidate>();
        AddGroups(DuplicateMatchField.Name, fields.Contains(DuplicateMatchField.Name), candidates, item => item.NormalizedName, groups);
        AddGroups(DuplicateMatchField.Email, fields.Contains(DuplicateMatchField.Email), candidates, item => item.NormalizedEmail, groups);
        AddGroups(DuplicateMatchField.Phone, fields.Contains(DuplicateMatchField.Phone), candidates, item => item.NormalizedPhone, groups);
        if (groups.Count == 0)
            return [];

        var ids = groups.Values.SelectMany(group => group.Ids).Distinct().ToArray();
        var displayItems = await _repository.GetDisplayItemsAsync(accountId, ids, cancellationToken);
        var byId = displayItems.ToDictionary(item => item.Id, ToDto);

        return groups.Values
            .Select(group => new DuplicateGroupDto(
                DuplicateMatchFields.ToQueryValue(group.Field),
                group.MatchValue,
                group.Ids.Select(id => byId[id])
                .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).ToArray()))
            .OrderByDescending(group => group.Counterparties.Count)
            .ThenBy(group => group.Counterparties.Min(item => item.Id))
            .ToArray();
    }

    private static void AddGroups(
        DuplicateMatchField field,
        bool enabled,
        IEnumerable<DuplicateCandidate> candidates,
        Func<DuplicateCandidate, string?> valueSelector,
        IDictionary<string, DuplicateGroupCandidate> groups)
    {
        if (!enabled)
            return;

        foreach (var group in candidates
                     .Where(item => !string.IsNullOrWhiteSpace(valueSelector(item)))
                     .GroupBy(valueSelector, StringComparer.Ordinal)
                     .Select(group => new DuplicateGroupCandidate(
                         field,
                         group.Key!,
                         group.Select(item => item.Id).OrderBy(id => id).ToArray()))
                     .Where(group => group.Ids.Length >= 2))
        {
            groups.TryAdd(GroupKey(group.Ids), group);
        }
    }

    private static string GroupKey(IEnumerable<Guid> ids) => string.Join(':', ids.Select(id => id.ToString("N")));

    private static (string Field, string Value)? ToNormalizedExclusion(CatalogSettingExclusion exclusion)
    {
        var normalizedValue = exclusion.Field switch
        {
            "name" => CounterpartyNormalizer.NormalizeName(exclusion.Value),
            "email" => CounterpartyNormalizer.NormalizeEmail(exclusion.Value),
            "phone" => CounterpartyNormalizer.NormalizePhone(exclusion.Value),
            _ => null
        };

        return string.IsNullOrWhiteSpace(normalizedValue)
            ? null
            : (exclusion.Field, normalizedValue);
    }

    private static bool IsExcluded(
        DuplicateCandidate candidate,
        HashSet<(string Field, string Value)> excludedValues) =>
        Matches("name", candidate.NormalizedName, excludedValues) ||
        Matches("email", candidate.NormalizedEmail, excludedValues) ||
        Matches("phone", candidate.NormalizedPhone, excludedValues);

    private static bool Matches(
        string field,
        string? value,
        HashSet<(string Field, string Value)> excludedValues) =>
        !string.IsNullOrWhiteSpace(value) && excludedValues.Contains((field, value));

    private static DuplicateCounterpartyDto ToDto(CounterpartyDisplayItem item) => new(
        item.Id, item.Name, item.Email, item.Phone, item.Description, item.Archived, ParseRawJson(item.RawJson), item.CreatedAt, item.UpdatedAt);

    private static JsonElement ParseRawJson(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }
    }

    private sealed record DuplicateGroupCandidate(DuplicateMatchField Field, string MatchValue, Guid[] Ids);
}
