using System.Globalization;
using DocumentRelationsGenerator.Execution;
using DocumentRelationsGenerator.Randomization;
using DocumentRelationsGenerator.Scenarios;

namespace DocumentRelationsGenerator.Tests;

public sealed class SeedReproducibilityTests
{
    private static readonly DateOnly Anchor = new(2026, 9, 24);

    [Fact]
    public void SameSeed_ProducesIdenticalValues()
    {
        var first = Fingerprint(new RunIdentity("20260924-100000-aaaa", 7348291, Anchor));
        var second = Fingerprint(new RunIdentity("20260924-100000-aaaa", 7348291, Anchor));
        Assert.Equal(first, second);
    }

    [Fact]
    public void SameSeedInANewRun_ReproducesValuesButNotNamesOrSyncIds()
    {
        var scenario = ScenarioRegistry.All.First();
        var original = ScenarioPlanner.Plan(scenario, 1, new RunIdentity("20260924-100000-aaaa", 12345, Anchor));
        var rerun = ScenarioPlanner.Plan(scenario, 1, new RunIdentity("20260925-080000-bbbb", 12345, Anchor));

        Assert.Equal(Values(original), Values(rerun));
        Assert.NotEqual(original.Steps[0].SyncId, rerun.Steps[0].SyncId); // a rerun must not resolve to old objects
        Assert.NotEqual(original.Steps[0].Name, rerun.Steps[0].Name);
    }

    [Fact]
    public void DifferentSeed_ProducesDifferentValues()
    {
        Assert.NotEqual(Fingerprint(new RunIdentity("run", 1, Anchor)), Fingerprint(new RunIdentity("run", 2, Anchor)));
    }

    [Fact]
    public void ScenarioValues_DoNotDependOnWhichOtherScenariosAreSelected()
    {
        var run = new RunIdentity("run", 555, Anchor);
        var alone = ScenarioRegistry.Select(["purchase-flow"]).Single();
        var withOthers = ScenarioRegistry.Select(["sales-flow", "purchase-flow", "retail-flow"]).Single(item => item.Name == "purchase-flow");
        Assert.Equal(Values(ScenarioPlanner.Plan(alone, 2, run)), Values(ScenarioPlanner.Plan(withOthers, 2, run)));
    }

    [Fact]
    public void StableSeed_IsIndependentOfProcessHashRandomization()
    {
        // Pinned value: string.GetHashCode would differ between processes.
        Assert.Equal(StableSeed.Derive(1, "sales-flow", 1), StableSeed.Derive(1, "sales-flow", 1));
        Assert.Equal(2130335118, StableSeed.Derive(7348291, "sales-flow", 1));
    }

    [Fact]
    public void SyncId_IsDeterministicWithinARun()
    {
        var run = new RunIdentity("20260924-221530-a81f", 1, Anchor);
        Assert.Equal(run.SyncId(1, "sales-flow", "demand"), run.SyncId(1, "sales-flow", "demand"));
        Assert.NotEqual(run.SyncId(1, "sales-flow", "demand"), run.SyncId(2, "sales-flow", "demand"));
    }

    private static string Fingerprint(RunIdentity run) =>
        string.Join("\n", ScenarioRegistry.All.Select(scenario => Values(ScenarioPlanner.Plan(scenario, 1, run))));

    private static string Values(ScenarioPlan plan) =>
        $"{plan.WithContract}|{plan.WithAgentAccount}|{plan.WithForeignCurrency}|" + string.Join(";", plan.Steps.Select(step =>
            string.Join(",",
                step.Step.Key, step.Moment.ToString("s", CultureInfo.InvariantCulture), step.Applicable, step.Shared, step.SetProject,
                step.SetOwner, step.VatEnabled, step.VatIncluded, step.PaymentShare, step.Description, step.IncomingNumber,
                string.Join("/", step.Positions.Select(position =>
                    $"{position.AssortmentIndex}:{position.IsService}:{position.Quantity}:{position.PriceKopecks}:{position.Discount}:{position.VatSlot}")))));
}
