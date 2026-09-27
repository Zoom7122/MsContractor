namespace MsContractor.Contracts.Internal;

public sealed record CatalogDuplicateExclusionSetting(
    string? Field,
    string? Value);

public sealed record CatalogDuplicateSearchOptions(
    bool IncludeArchivedWithDocuments);

public sealed record CatalogDuplicateSearchLimits(
    int GroupLimit,
    int ItemLimit);

public sealed record CatalogSettingsRequest(
    IReadOnlyList<CatalogDuplicateExclusionSetting>? DuplicateExclusions,
    CatalogDuplicateSearchOptions? DuplicateSearchOptions,
    CatalogDuplicateSearchLimits? SearchLimits);
