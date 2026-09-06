using MsContractor.CatalogSyncService.Models;

namespace MsContractor.CatalogSyncService.Services;

public static class CounterpartyStorageValidator
{
    public static bool TryValidate(
        Counterparty counterparty,
        out string? field,
        out int length,
        out int maxLength)
    {
        foreach (var (name, value, maximum) in Fields(counterparty))
        {
            if (value is not null && value.Length > maximum)
            {
                field = name;
                length = value.Length;
                maxLength = maximum;
                return false;
            }
        }

        field = null;
        length = 0;
        maxLength = 0;
        return true;
    }

    private static IEnumerable<(string Name, string? Value, int MaxLength)> Fields(Counterparty item)
    {
        yield return (nameof(item.Name), item.Name, 1024);
        yield return (nameof(item.Phone), item.Phone, 255);
        yield return (nameof(item.Email), item.Email, 320);
        yield return (nameof(item.Inn), item.Inn, 32);
        yield return (nameof(item.Kpp), item.Kpp, 32);
        yield return (nameof(item.NormalizedName), item.NormalizedName, 1024);
        yield return (nameof(item.NormalizedPhone), item.NormalizedPhone, 64);
        yield return (nameof(item.NormalizedEmail), item.NormalizedEmail, 320);
        yield return (nameof(item.NormalizedInn), item.NormalizedInn, 32);
        yield return (nameof(item.NormalizedKpp), item.NormalizedKpp, 32);
    }
}
