using System.Text.Json.Nodes;

namespace DocumentRelationsGenerator.References;

public sealed class ReferenceData
{
    public const int ProductCount = 5;
    public const int ServiceCount = 2;
    public const int AccountsPerCounterparty = 2;

    /// <summary>Existing organization; never created or modified.</summary>
    public required JsonObject Organization { get; init; }

    public IReadOnlyList<JsonObject> OrganizationAccounts { get; init; } = [];
    public JsonObject? Store { get; set; }
    public JsonObject? Employee { get; init; }
    public JsonObject? ExpenseItem { get; set; }
    public JsonObject? Project { get; set; }

    /// <summary>Non-archived integer VAT rates of the account's taxrate dictionary.</summary>
    public IReadOnlyList<int> VatRates { get; init; } = [];

    public JsonObject? ForeignCurrency { get; init; }
    public JsonObject? RetailStore { get; init; }

    /// <summary>Why retail-flow cannot run; null when the retail store is usable.</summary>
    public string? RetailProblem { get; init; }

    public List<JsonObject> Products { get; } = [];
    public List<JsonObject> Services { get; } = [];
    public List<CounterpartyData> Counterparties { get; } = [];
}

public sealed class CounterpartyData
{
    public CounterpartyData(int index, JsonObject entity)
    {
        Index = index;
        Entity = entity;
    }

    public int Index { get; }
    public JsonObject Entity { get; }
    public List<JsonObject> Accounts { get; } = [];
    public JsonObject? SalesContract { get; set; }
    public JsonObject? CommissionContract { get; set; }
}
