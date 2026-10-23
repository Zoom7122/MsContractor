using DocumentRelationsGenerator.Execution;
using DocumentRelationsGenerator.Randomization;
using DocumentRelationsGenerator.Scenarios;

namespace DocumentRelationsGenerator.Tests;

public sealed class TestDataRandomizerTests
{
    [Fact]
    public void NextPriceKopecks_ReturnsRealisticPricesInKopecks()
    {
        var random = new TestDataRandomizer(42);
        var prices = Enumerable.Range(0, 2000).Select(_ => random.NextPriceKopecks()).ToList();

        Assert.All(prices, price => Assert.InRange(price, 50_00, 15_000_00));
        Assert.All(prices, price => Assert.Contains(price % 100, new long[] { 0, 50, 90 }));
        Assert.Contains(prices, price => price % 100 == 90); // 249.90-style prices appear
        Assert.Contains(prices, price => price >= 3000_00 && price % 50_00 == 0); // 8450-style prices appear
        Assert.True(prices.Distinct().Count() > 500, "prices must vary");
    }

    [Fact]
    public void NextQuantity_UsesFractionsOnlyWhenAllowed()
    {
        var random = new TestDataRandomizer(7);
        var integers = Enumerable.Range(0, 500).Select(_ => random.NextQuantity(allowFractional: false)).ToList();
        var mixed = Enumerable.Range(0, 500).Select(_ => random.NextQuantity(allowFractional: true)).ToList();

        Assert.All(integers, quantity => Assert.Contains(quantity, TestDataRandomizer.IntegerQuantities));
        Assert.All(mixed, quantity => Assert.True(TestDataRandomizer.IntegerQuantities.Contains(quantity) ||
                                                  TestDataRandomizer.FractionalQuantities.Contains(quantity)));
        Assert.Contains(mixed, quantity => quantity != decimal.Floor(quantity));
    }

    [Fact]
    public void NextDiscount_UsesOnlyAllowedValues()
    {
        var random = new TestDataRandomizer(3);
        var discounts = Enumerable.Range(0, 300).Select(_ => random.NextDiscount()).ToHashSet();
        Assert.Subset(TestDataRandomizer.Discounts.ToHashSet(), discounts);
        Assert.True(discounts.Count >= 4);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(10)]
    public void ReduceQuantity_NeverExceedsTheBaseAndNeverReturnsZero(double available)
    {
        var random = new TestDataRandomizer(11);
        for (var i = 0; i < 200; i++)
            Assert.InRange(random.ReduceQuantity((decimal)available), 0.0001m, (decimal)available);
    }

    [Fact]
    public void Plan_KeepsBusinessOrderOfMoments()
    {
        var run = new RunIdentity("20260924-221530-a81f", 7348291, new DateOnly(2026, 9, 24));
        foreach (var scenario in ScenarioRegistry.All)
        {
            var plan = ScenarioPlanner.Plan(scenario, 1, run);
            foreach (var step in plan.Steps)
            {
                Assert.Equal(0, step.Moment.Second);
                Assert.True(step.Moment < run.AnchorDate.ToDateTime(TimeOnly.MinValue), $"{scenario.Name}/{step.Step.Key} is in the future");
                foreach (var dependency in step.Step.Dependencies)
                    Assert.True(plan.Step(dependency).Moment < step.Moment,
                        $"{scenario.Name}: {dependency} must precede {step.Step.Key}");
                Assert.True(step.PeriodStart < step.PeriodEnd && step.PeriodEnd <= step.Moment);
                Assert.True(step.IncomingDate <= step.Moment);
            }
        }
    }

    [Fact]
    public void Plan_RootPositionsHaveOneToFiveDistinctProductsWithValidValues()
    {
        var run = new RunIdentity("run", 99, new DateOnly(2026, 9, 24));
        var roots = ScenarioRegistry.All.SelectMany(scenario => ScenarioPlanner.Plan(scenario, 1, run).Steps)
            .Where(step => step.Positions.Count > 0).ToList();

        Assert.NotEmpty(roots);
        foreach (var step in roots)
        {
            Assert.InRange(step.Positions.Count, 1, 5);
            var products = step.Positions.Where(position => !position.IsService).Select(position => position.AssortmentIndex).ToList();
            Assert.Equal(products.Count, products.Distinct().Count());
            Assert.All(step.Positions, position => Assert.True(position.Quantity > 0 && position.PriceKopecks > 0));
            if (!step.Step.AllowServices) Assert.DoesNotContain(step.Positions, position => position.IsService);
            if (step.Step.DocumentType.StartsWith("commissionreport", StringComparison.Ordinal))
                Assert.All(step.Positions, position => Assert.Equal(0, position.Discount));
        }
    }

    [Fact]
    public void Plan_CoversBothContractAndAgentAccountVariantsAcrossTheRegistry()
    {
        var run = new RunIdentity("run", 5, new DateOnly(2026, 9, 24));
        var plans = ScenarioRegistry.All.Select(scenario => ScenarioPlanner.Plan(scenario, 1, run)).ToList();

        Assert.Contains(plans, plan => plan.WithContract);
        Assert.Contains(plans, plan => !plan.WithContract);
        Assert.Contains(plans, plan => plan.WithAgentAccount);
        Assert.Contains(plans, plan => !plan.WithAgentAccount);
        Assert.Equal(4, plans.Select(plan => (plan.WithContract, plan.WithAgentAccount)).Distinct().Count());
    }
}
