using System.Text.RegularExpressions;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Repo;

namespace MsContractor.CatalogSyncService.Services;

public interface ICounterpartyNormalizer
{
    Counterparty Create(
        Guid accountId,
        Guid syncRunId,
        ParsedCounterparty source,
        DateTimeOffset now);
}

public sealed partial class CounterpartyNormalizer : ICounterpartyNormalizer
{
    public Counterparty Create(
        Guid accountId,
        Guid syncRunId,
        ParsedCounterparty source,
        DateTimeOffset now)
    {
        var item = source.Value;
        return new Counterparty
        {
            Id = item.Id,
            AccountId = accountId,
            Name = item.Name,
            Phone = item.Phone,
            Email = item.Email,
            Inn = item.Inn,
            Kpp = item.Kpp,
            Description = item.Description,
            Archived = item.Archived,
            NormalizedName = NormalizeName(item.Name),
            NormalizedPhone = NormalizePhone(item.Phone),
            NormalizedEmail = NormalizeEmail(item.Email),
            NormalizedInn = Digits(item.Inn),
            NormalizedKpp = Digits(item.Kpp),
            MoySkladUpdatedAt = item.Updated?.ToUniversalTime(),
            LastSyncRunId = syncRunId,
            RawJson = source.RawJson,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static string NormalizeName(string value) =>
        MultipleWhitespace().Replace(value.Trim(), " ").ToLowerInvariant();

    public static string? NormalizeEmail(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    public static string? NormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized[0] == '8'
            ? $"+7{normalized[1..]}"
            : normalized;
    }

    public static string? Digits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = new string(value.Where(char.IsDigit).ToArray());
        return normalized.Length == 0 ? null : normalized;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleWhitespace();
}
