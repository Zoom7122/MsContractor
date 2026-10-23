using DocumentRelationsGenerator.Scenarios;

namespace DocumentRelationsGenerator.Execution;

/// <param name="AssortmentIndex">Index into the run's test products (or services).</param>
/// <param name="VatSlot">Index into the account's VAT rates (resolved at run time).</param>
public sealed record PositionPlan(int AssortmentIndex, bool IsService, decimal Quantity, long PriceKopecks, int Discount,
    int VatSlot);

public sealed record StepPlan(
    ScenarioStep Step,
    int Index,
    string Name,
    string ExternalCode,
    Guid SyncId,
    DateTime Moment,
    bool Applicable,
    bool Shared,
    bool SetProject,
    bool SetOwner,
    bool SetOrganizationAccount,
    bool VatEnabled,
    bool VatIncluded,
    IReadOnlyList<PositionPlan> Positions,
    decimal PaymentShare,
    string Description,
    string PaymentPurpose,
    string IncomingNumber,
    DateTime IncomingDate,
    DateTime PlannedMoment,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    int RuntimeSeed,
    bool WithContract,
    bool WithAgentAccount,
    long AmountKopecks);

public sealed record ScenarioPlan(
    ScenarioDefinition Scenario,
    int CounterpartyIndex,
    bool WithContract,
    bool WithAgentAccount,
    bool WithForeignCurrency,
    IReadOnlyList<StepPlan> Steps)
{
    public string Label => $"{Scenario.Name} [counterparty {CounterpartyIndex}]";

    public StepPlan Step(string key) => Steps.Single(step => step.Step.Key == key);
}
