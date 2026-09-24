using DocumentRelationsGenerator.Randomization;
using DocumentRelationsGenerator.References;
using DocumentRelationsGenerator.Relations;
using DocumentRelationsGenerator.Scenarios;

namespace DocumentRelationsGenerator.Execution;

/// <summary>
/// Turns a scenario into concrete values. Everything here depends only on seed, anchor date, scenario name
/// and counterparty index (not on API responses), so --dry-run prints the values a real run will use.
/// </summary>
public static class ScenarioPlanner
{
    private static readonly HashSet<string> NoDiscountTypes = ["commissionreportin", "commissionreportout"];

    public static ScenarioPlan Plan(ScenarioDefinition scenario, int counterpartyIndex, RunIdentity run)
    {
        var random = TestDataRandomizer.For(run.Seed, scenario.Name, counterpartyIndex);
        var ordinal = ScenarioRegistry.Ordinal(scenario);

        // Alternating variants guarantee "with/without contract" and "with/without agentAccount" across the
        // registry (and across counterparties) instead of leaving it to chance.
        var withContract = (ordinal + counterpartyIndex + (run.Seed & 1)) % 2 == 0;
        var withAgentAccount = (ordinal / 2 + counterpartyIndex + ((run.Seed >> 1) & 1)) % 2 == 0;
        var withForeignCurrency = scenario.AllowsForeignCurrency && random.Chance(0.5);

        var ordered = DependencyOrder.Sort(scenario.Steps);
        var hasDependents = scenario.Steps.SelectMany(step => step.Dependencies).ToHashSet(StringComparer.Ordinal);
        var paymentsPerBase = scenario.Steps
            .Where(step => step.Links.Any(link => RelationCatalog.Get(link.RelationId).Kind == RelationKind.Operation))
            .SelectMany(step => step.Links.Select(link => link.TargetStep))
            .GroupBy(key => key)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var start = random.NextScenarioStart(run.AnchorDate);
        var moments = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var steps = new List<StepPlan>();
        for (var index = 0; index < ordered.Count; index++)
        {
            var step = ordered[index];
            var dependencyMoments = step.Dependencies.Select(key => moments[key]).ToList();
            var moment = dependencyMoments.Count > 0
                ? random.NextMomentAfter(dependencyMoments.Max())
                : index == 0 ? start : start.AddMinutes(random.Between(1, 90));
            moment = moment.AddSeconds(-moment.Second).AddMilliseconds(-moment.Millisecond);
            moments[step.Key] = moment;

            var positions = new List<PositionPlan>();
            if (step.Kind == StepKind.Root && DocumentFieldSupport.Supports(step.DocumentType, "positions"))
            {
                var productIndexes = Enumerable.Range(0, ReferenceData.ProductCount).OrderBy(_ => random.Between(0, 1000)).ToList();
                var count = random.Between(1, 5);
                for (var i = 0; i < count; i++)
                {
                    var isService = step.AllowServices && i > 0 && random.Chance(0.25);
                    positions.Add(new PositionPlan(
                        isService ? random.Between(0, ReferenceData.ServiceCount - 1) : productIndexes[i],
                        isService,
                        random.NextQuantity(allowFractional: !isService),
                        random.NextPriceKopecks(),
                        NoDiscountTypes.Contains(step.DocumentType) ? 0 : random.NextDiscount(),
                        random.Between(0, 99)));
                }
            }

            // A base paid by two payments gets partial payments, so the second template still has an unpaid sum.
            var sharedBase = step.Links.Any(link => paymentsPerBase.GetValueOrDefault(link.TargetStep) > 1);
            var share = sharedBase ? random.Between(30, 60) / 100m : random.NextPaymentShare();

            steps.Add(new StepPlan(
                step,
                index,
                run.DocumentName(counterpartyIndex, ordinal, index),
                run.DocumentExternalCode(counterpartyIndex, scenario.Name, step.Key),
                run.SyncId(counterpartyIndex, scenario.Name, step.Key),
                moment,
                // Documents that others are based on stay applicable; leaves are sometimes drafts.
                Applicable: hasDependents.Contains(step.Key) || random.Chance(0.8),
                Shared: random.Chance(0.5),
                SetProject: random.Chance(0.5),
                SetOwner: random.Chance(0.5),
                SetOrganizationAccount: random.Chance(0.5),
                VatEnabled: random.Chance(0.6),
                VatIncluded: random.Chance(0.5),
                positions,
                share,
                random.NextDescription(),
                random.NextPaymentPurpose(),
                $"IN-{random.NextDigits(6)}",
                moment.AddDays(-random.Between(0, 3)),
                moment.AddDays(random.Between(1, 14)),
                moment.Date.AddDays(-random.Between(20, 40)),
                moment.Date.AddDays(-random.Between(1, 10)),
                StableSeed.Derive(run.Seed, scenario.Name, counterpartyIndex, step.Key, "runtime")));
        }

        return new ScenarioPlan(scenario, counterpartyIndex, withContract, withAgentAccount, withForeignCurrency, steps);
    }
}
