namespace DocumentRelationsGenerator.Scenarios;

public enum StepKind
{
    /// <summary>Built from scratch (organization, agent, positions or sums).</summary>
    Root,

    /// <summary><c>PUT /entity/{type}/new</c> with the base documents, then POST of the adjusted template.</summary>
    Template,

    /// <summary>Payment created by POST with an explicit <c>operations</c> collection.</summary>
    PaymentPost,

    /// <summary>Retail shift opened for the configured retail store.</summary>
    RetailShift
}

public enum ScenarioRequirement
{
    None,

    /// <summary>MS_RELTEST_RETAIL_STORE_ID must point to an active retail store.</summary>
    RetailStore
}

/// <summary>A relation that this step writes, pointing at an earlier step of the same scenario.</summary>
public sealed record StepLink(string RelationId, string TargetStep);

public sealed record ScenarioStep(string Key, string DocumentType, StepKind Kind, IReadOnlyList<StepLink> Links)
{
    /// <summary>Root positions may include a service (only where the chain never reaches a return or loss).</summary>
    public bool AllowServices { get; init; }

    public IEnumerable<string> Dependencies => Links.Select(link => link.TargetStep).Distinct(StringComparer.Ordinal);
}

public sealed record ScenarioDefinition(
    string Name,
    string Description,
    ScenarioRequirement Requirement,
    IReadOnlyList<ScenarioStep> Steps)
{
    /// <summary>Documents use a foreign account currency when one exists (chosen per run by the seed).</summary>
    public bool AllowsForeignCurrency { get; init; }
}
