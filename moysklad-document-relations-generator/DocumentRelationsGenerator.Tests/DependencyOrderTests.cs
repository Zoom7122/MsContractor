using DocumentRelationsGenerator.Scenarios;

namespace DocumentRelationsGenerator.Tests;

public sealed class DependencyOrderTests
{
    private static ScenarioStep Step(string key, params string[] parents) =>
        new(key, "demand", StepKind.Template, parents.Select(parent => new StepLink("demand.customerOrder->customerorder", parent)).ToList());

    [Fact]
    public void Sort_PlacesParentsBeforeChildrenAndKeepsDeclarationOrderForTies()
    {
        var steps = new[] { Step("return", "demand"), Step("order"), Step("demand", "order"), Step("payment", "order") };
        Assert.Equal(["order", "demand", "return", "payment"], DependencyOrder.Sort(steps).Select(step => step.Key));
    }

    [Fact]
    public void Sort_RejectsCycles()
    {
        var error = Assert.Throws<InvalidOperationException>(() => DependencyOrder.Sort([Step("a", "b"), Step("b", "a")]));
        Assert.Contains("cycle", error.Message);
    }

    [Fact]
    public void Sort_RejectsUnknownAndDuplicateSteps()
    {
        Assert.Throws<InvalidOperationException>(() => DependencyOrder.Sort([Step("a", "missing")]));
        Assert.Throws<InvalidOperationException>(() => DependencyOrder.Sort([Step("a"), Step("a")]));
    }

    [Fact]
    public void Descendants_BlocksTheWholeSubtreeOnly()
    {
        var steps = new[] { Step("order"), Step("demand", "order"), Step("return", "demand"), Step("loss", "return"), Step("invoice", "order") };
        Assert.Equal(new HashSet<string> { "return", "loss" }, DependencyOrder.Descendants(steps, ["demand"]));
    }

    [Fact]
    public void RegistryScenarios_AreTopologicallySortable() =>
        Assert.All(ScenarioRegistry.All, scenario => Assert.Equal(scenario.Steps.Count, DependencyOrder.Sort(scenario.Steps).Count));
}
