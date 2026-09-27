using MsContractor.CatalogSyncService.Repositories;
using MsContractor.Contracts.Internal;

namespace MsContractor.CatalogSyncService.Services;

public interface ICatalogSettingsService
{
    Task SaveAsync(
        Guid accountId,
        CatalogSettingsRequest? request,
        CancellationToken cancellationToken);
}

public sealed class CatalogSettingsValidationException(
    string code,
    string message) : Exception(message)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = message;
}

public sealed class CatalogSettingsService : ICatalogSettingsService
{
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "name",
        "email",
        "phone"
    };

    private readonly ICatalogSettingsRepository _repository;

    public CatalogSettingsService(ICatalogSettingsRepository repository)
    {
        _repository = repository;
    }

    public async Task SaveAsync(
        Guid accountId,
        CatalogSettingsRequest? request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeAndValidate(request);
        await _repository.SaveAsync(
            accountId,
            normalized.IncludeArchivedWithDocuments,
            normalized.GroupLimit,
            normalized.ItemLimit,
            normalized.Exclusions,
            cancellationToken);
    }

    private static ValidatedSettings NormalizeAndValidate(CatalogSettingsRequest? request)
    {
        if (request?.DuplicateExclusions is null ||
            request.DuplicateSearchOptions is null ||
            request.SearchLimits is null)
        {
            throw new CatalogSettingsValidationException(
                "INVALID_CATALOG_SETTINGS",
                "duplicateExclusions, duplicateSearchOptions, and searchLimits are required.");
        }

        var exclusions = new List<CatalogDuplicateExclusionSetting>(request.DuplicateExclusions.Count);
        var distinctExclusions = new HashSet<(string Field, string Value)>();
        foreach (var exclusion in request.DuplicateExclusions)
        {
            var field = exclusion.Field?.Trim().ToLowerInvariant();
            var value = exclusion.Value?.Trim().ToLower();
            if (field is null || !AllowedFields.Contains(field) || string.IsNullOrWhiteSpace(value))
            {
                throw new CatalogSettingsValidationException(
                    "INVALID_DUPLICATE_EXCLUSION",
                    "Each exclusion must have a field of name, email, or phone and a non-empty value.");
            }

            if (!distinctExclusions.Add((field, value)))
                continue;

            exclusions.Add(new CatalogDuplicateExclusionSetting(field, value));
        }

        var limits = request.SearchLimits;
        if (limits.GroupLimit is < 1 or > 1000 || limits.ItemLimit is < 1 or > 1000)
        {
            throw new CatalogSettingsValidationException(
                "INVALID_SEARCH_LIMITS",
                "groupLimit and itemLimit must be between 1 and 1000.");
        }

        return new ValidatedSettings(
            exclusions,
            request.DuplicateSearchOptions.IncludeArchivedWithDocuments,
            limits.GroupLimit,
            limits.ItemLimit);
    }

    private sealed record ValidatedSettings(
        IReadOnlyList<CatalogDuplicateExclusionSetting> Exclusions,
        bool IncludeArchivedWithDocuments,
        int GroupLimit,
        int ItemLimit);
}
