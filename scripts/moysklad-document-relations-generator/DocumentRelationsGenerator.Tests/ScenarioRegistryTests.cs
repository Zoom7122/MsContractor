using System.Text.RegularExpressions;
using DocumentRelationsGenerator.Execution;
using DocumentRelationsGenerator.Relations;
using DocumentRelationsGenerator.Scenarios;

namespace DocumentRelationsGenerator.Tests;

public sealed class ScenarioRegistryTests
{
    [Fact]
    public void Scenarios_TogetherCreateEveryConfirmedRelation()
    {
        var created = ScenarioRegistry.All.SelectMany(scenario => scenario.Steps).SelectMany(step => step.Links)
            .Select(link => link.RelationId).ToHashSet();
        var missing = RelationCatalog.All.Select(relation => relation.Id).Where(id => !created.Contains(id)).ToList();
        Assert.Empty(missing);
        Assert.Subset(RelationCatalog.All.Select(relation => relation.Id).ToHashSet(), created);
    }

    [Fact]
    public void Names_AreUniqueKebabCase()
    {
        var names = ScenarioRegistry.All.Select(scenario => scenario.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, name => Assert.Matches(new Regex("^[a-z]+(-[a-z]+)*$"), name));
    }

    [Fact]
    public void Catalog_HasUniqueIdsAndDocumentedTypes()
    {
        Assert.Equal(45, RelationCatalog.All.Count);
        Assert.Equal(RelationCatalog.All.Count, RelationCatalog.All.Select(relation => relation.Id).Distinct().Count());
        Assert.All(RelationCatalog.All, relation => Assert.Contains(relation.SourceType, DocumentFieldSupport.DocumentTypes));
    }

    [Fact]
    public void Every_scenario_passes_structural_validation() =>
        Assert.All(ScenarioRegistry.All, ScenarioRegistry.Validate);

    [Fact]
    public void FactureWithTwoBases_IsRejected()
    {
        var scenario = new ScenarioDefinition("broken", "", ScenarioRequirement.None,
        [
            new ScenarioStep("demand", "demand", StepKind.Root, []),
            new ScenarioStep("cashin", "cashin", StepKind.Template, [new StepLink("cashin.operations->demand", "demand")]),
            new ScenarioStep("factureout", "factureout", StepKind.Template,
            [
                new StepLink("factureout.demands->demand", "demand"),
                new StepLink("factureout.payments->cashin", "cashin")
            ])
        ]);
        Assert.Throws<InvalidOperationException>(() => ScenarioRegistry.Validate(scenario));
    }

    [Fact]
    public void TwoFacturesOnOneBase_AreRejected()
    {
        var scenario = new ScenarioDefinition("broken", "", ScenarioRequirement.None,
        [
            new ScenarioStep("demand", "demand", StepKind.Root, []),
            new ScenarioStep("f1", "factureout", StepKind.Template, [new StepLink("factureout.demands->demand", "demand")]),
            new ScenarioStep("f2", "factureout", StepKind.Template, [new StepLink("factureout.demands->demand", "demand")])
        ]);
        Assert.Throws<InvalidOperationException>(() => ScenarioRegistry.Validate(scenario));
    }

    [Fact]
    public void LinkWithWrongTypes_IsRejected()
    {
        var scenario = new ScenarioDefinition("broken", "", ScenarioRequirement.None,
        [
            new ScenarioStep("supply", "supply", StepKind.Root, []),
            new ScenarioStep("salesreturn", "salesreturn", StepKind.Template, [new StepLink("salesreturn.demand->demand", "supply")])
        ]);
        Assert.Throws<InvalidOperationException>(() => ScenarioRegistry.Validate(scenario));
    }

    [Fact]
    public void Select_FiltersByNameAndRejectsUnknownNames()
    {
        Assert.Equal(ScenarioRegistry.All.Count, ScenarioRegistry.Select([]).Count);
        Assert.Equal(["sales-flow", "purchase-flow"],
            ScenarioRegistry.Select(["purchase-flow", "sales-flow"]).Select(scenario => scenario.Name));
        var error = Assert.Throws<GeneratorException>(() => ScenarioRegistry.Select(["sales-flow", "no-such-flow"]));
        Assert.Contains("no-such-flow", error.Message);
    }

    [Fact]
    public void FactureOutFull_CoversEveryFactureOutBaseAndContextOnOneCounterparty()
    {
        var scenario = ScenarioRegistry.Select(["factureout-full"]).Single();
        var factures = scenario.Steps.Where(step => step.DocumentType == "factureout").ToList();
        var links = scenario.Steps.SelectMany(step => step.Links).Select(link => link.RelationId).ToHashSet();

        Assert.Equal(17, factures.Count);
        Assert.All(factures, facture => Assert.Single(facture.Links));
        Assert.Equal(factures.Count, factures.Select(facture => facture.Links[0].TargetStep).Distinct().Count());
        Assert.Subset(links, RelationCatalog.All.Where(relation => relation.SourceType == "factureout").Select(relation => relation.Id).ToHashSet());
        foreach (var payment in new[] { "paymentin", "cashin" })
        {
            foreach (var target in new[] { "customerorder", "invoiceout", "demand", "purchasereturn", "commissionreportin" })
                Assert.Contains($"{payment}.operations->{target}", links);
            Assert.Contains(scenario.Steps, step => step.Kind == StepKind.Root && step.DocumentType == payment); // advance without document
        }
        Assert.Contains(scenario.Steps, step => step.Kind == StepKind.Root && step.DocumentType == "purchasereturn"); // return without base

        var roots = ScenarioPlanner.Plan(scenario, 1, new RunIdentity("run", 7, new DateOnly(2026, 9, 25))).Steps
            .Where(step => step.Step.Kind == StepKind.Root).ToList();
        Assert.Equal(4, roots.Select(step => (step.WithContract, step.WithAgentAccount)).Distinct().Count());
    }

    [Fact]
    public void OtherScenarios_KeepOneAgreementVariantPerScenario()
    {
        var run = new RunIdentity("run", 7, new DateOnly(2026, 9, 25));
        foreach (var plan in ScenarioRegistry.All.Where(scenario => !scenario.AlternatesAgreementsPerRoot)
                     .Select(scenario => ScenarioPlanner.Plan(scenario, 1, run)))
            Assert.All(plan.Steps, step => Assert.Equal((plan.WithContract, plan.WithAgentAccount), (step.WithContract, step.WithAgentAccount)));
    }

    [Fact]
    public void OnlyRetailFlow_RequiresARetailStore() =>
        Assert.Equal(["retail-flow"], ScenarioRegistry.All.Where(scenario => scenario.Requirement != ScenarioRequirement.None)
            .Select(scenario => scenario.Name));
}
