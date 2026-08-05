using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Repo;
using MsContractor.Contracts.Duplicates;

namespace MsContractor.DuplicatesMergeService.Services;

public interface IDuplicatePreviewService
{
    Task<IReadOnlyList<DuplicateGroupDto>> FindAsync(
        Guid accountId,
        IReadOnlyCollection<DuplicateMatchField> fields,
        CancellationToken cancellationToken);
}

public sealed class DuplicatePreviewService(CatalogSyncDbContext dbContext) : IDuplicatePreviewService
{
    public async Task<IReadOnlyList<DuplicateGroupDto>> FindAsync(
        Guid accountId,
        IReadOnlyCollection<DuplicateMatchField> fields,
        CancellationToken cancellationToken)
    {
        var candidates = await dbContext.Counterparties
            .AsNoTracking()
            .Where(item => item.AccountId == accountId && !item.Archived)
            .Select(item => new Candidate(item.Id, item.NormalizedName, item.NormalizedEmail, item.NormalizedPhone))
            .ToListAsync(cancellationToken);

        var groups = new Dictionary<string, DuplicateGroupCandidate>();
        AddGroups(DuplicateMatchField.Name, fields.Contains(DuplicateMatchField.Name), candidates, item => item.NormalizedName, groups);
        AddGroups(DuplicateMatchField.Email, fields.Contains(DuplicateMatchField.Email), candidates, item => item.NormalizedEmail, groups);
        AddGroups(DuplicateMatchField.Phone, fields.Contains(DuplicateMatchField.Phone), candidates, item => item.NormalizedPhone, groups);
        if (groups.Count == 0)
            return [];

        var ids = groups.Values.SelectMany(group => group.Ids).Distinct().ToArray();
        var displayItems = await dbContext.Counterparties
            .AsNoTracking()
            .Where(item => item.AccountId == accountId && ids.Contains(item.Id))
            .Select(item => new DisplayItem(item.Id, item.Name, item.Email, item.Phone, item.Description, item.RawJson, item.CreatedAt, item.UpdatedAt))
            .ToListAsync(cancellationToken);
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
        IEnumerable<Candidate> candidates,
        Func<Candidate, string?> valueSelector,
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

    private static DuplicateCounterpartyDto ToDto(DisplayItem item) => new(
        item.Id, item.Name, item.Email, item.Phone, item.Description, ParseRawJson(item.RawJson), item.CreatedAt, item.UpdatedAt);

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

    private sealed record Candidate(Guid Id, string? NormalizedName, string? NormalizedEmail, string? NormalizedPhone);
    private sealed record DuplicateGroupCandidate(DuplicateMatchField Field, string MatchValue, Guid[] Ids);
    private sealed record DisplayItem(Guid Id, string Name, string? Email, string? Phone, string? Description, string RawJson, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
}
