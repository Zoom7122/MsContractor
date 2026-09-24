using System.Text.Json.Nodes;
using DocumentRelationsGenerator.Documents;
using DocumentRelationsGenerator.Manifest;
using DocumentRelationsGenerator.MoySklad;
using DocumentRelationsGenerator.References;
using DocumentRelationsGenerator.Relations;
using DocumentRelationsGenerator.Scenarios;

namespace DocumentRelationsGenerator.Execution;

/// <summary>
/// Creates a scenario in topological order. Every child receives the real meta of its created parents.
/// A failed step blocks its descendants; independent branches of the same scenario continue.
/// </summary>
public sealed class ScenarioRunner
{
    private readonly IMoySkladApi api;
    private readonly DocumentFactory factory;
    private readonly ReferenceData refs;
    private readonly ManifestStore manifest;
    private readonly RelationVerifier verifier;
    private readonly TextWriter output;

    public ScenarioRunner(IMoySkladApi api, DocumentFactory factory, ReferenceData refs, ManifestStore manifest,
        TextWriter output)
    {
        this.api = api;
        this.factory = factory;
        this.refs = refs;
        this.manifest = manifest;
        this.output = output;
        verifier = new RelationVerifier(api, output);
    }

    public static ManifestScenario NewResult(ScenarioPlan plan) => new()
    {
        Name = plan.Scenario.Name,
        CounterpartyIndex = plan.CounterpartyIndex,
        WithContract = plan.WithContract,
        WithAgentAccount = plan.WithAgentAccount,
        WithForeignCurrency = plan.WithForeignCurrency,
        Relations = plan.Steps.SelectMany(step => step.Step.Links.Select(link =>
        {
            var relation = RelationCatalog.Get(link.RelationId);
            return new ManifestRelation
            {
                RelationId = relation.Id,
                SourceStep = step.Step.Key,
                SourceType = relation.SourceType,
                Field = relation.Field,
                TargetStep = link.TargetStep,
                TargetType = relation.TargetType,
                ReverseField = relation.ReverseField
            };
        })).ToList()
    };

    public async Task<ManifestScenario> RunAsync(ScenarioPlan plan, CancellationToken cancellationToken)
    {
        var counterparty = refs.Counterparties[plan.CounterpartyIndex - 1];
        var result = NewResult(plan);
        result.CounterpartyId = MetaReference.Id(MetaReference.Href(counterparty.Entity));
        manifest.Manifest.Scenarios.Add(result);
        manifest.Save();

        output.WriteLine();
        output.WriteLine($"=== {plan.Label}: contract={Flag(plan.WithContract)}, agentAccount={Flag(plan.WithAgentAccount)}" +
                         (plan.WithForeignCurrency ? ", foreign currency" : ""));

        var created = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var broken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            var type = step.Step.DocumentType;
            var brokenParent = step.Step.Dependencies.FirstOrDefault(broken.Contains);
            if (brokenParent is not null)
            {
                broken.Add(step.Step.Key);
                result.Warnings.Add($"{step.Step.Key} skipped: required parent '{brokenParent}' was not created");
                output.WriteLine($"  [SKIP] {step.Step.Key}: required parent '{brokenParent}' was not created");
                continue;
            }

            var operation = "build payload";
            JsonObject? payload = null;
            try
            {
                switch (step.Step.Kind)
                {
                    case StepKind.Root:
                        payload = factory.BuildRoot(plan, step, counterparty);
                        break;
                    case StepKind.Template:
                        operation = "PUT template";
                        var template = await api.GetTemplateAsync(type, DocumentFactory.BuildTemplateRequest(step.Step, created),
                            cancellationToken);
                        operation = "build payload";
                        payload = factory.BuildFromTemplate(plan, step, template, counterparty, created);
                        break;
                    case StepKind.PaymentPost:
                        payload = factory.BuildPaymentPost(plan, step, counterparty, created);
                        break;
                    case StepKind.RetailShift:
                        payload = factory.BuildRetailShift(step);
                        break;
                }

                operation = "POST create";
                var entity = await api.CreateAsync(type, payload!, cancellationToken);
                Record(result, step, entity);
                created[step.Step.Key] = entity;
                output.WriteLine($"  [CREATED] {step.Step.Key,-32} {type,-20} {entity["name"]} id={MetaReference.Id(MetaReference.Href(entity))}");
            }
            catch (MoySkladApiException ex) when (!ex.IsAuthenticationFailure)
            {
                broken.Add(step.Step.Key);
                result.Errors.Add(RelationVerifier.ToError(result.Name, step.Step.Key, type, operation, ex,
                    operation == "POST create" ? payload : null));
                output.WriteLine($"  [ERROR] scenario={result.Name} type={type} operation={operation} " +
                                 $"status={ex.StatusCode?.ToString() ?? "-"} code={ex.ErrorCode?.ToString() ?? "-"} " +
                                 $"message={ex.ErrorMessage ?? "-"}");
                if (ex.OutcomeUnknown)
                    output.WriteLine($"          the object may exist without being in the manifest; externalCode={step.ExternalCode}");
            }
            catch (InvalidOperationException ex)
            {
                broken.Add(step.Step.Key);
                result.Errors.Add(new ManifestError
                {
                    Scenario = result.Name, Step = step.Step.Key, DocumentType = type, Operation = operation, Message = ex.Message
                });
                output.WriteLine($"  [ERROR] scenario={result.Name} type={type} operation={operation} message={ex.Message}");
            }

            manifest.Save();
        }

        output.WriteLine("  verification (GET):");
        await verifier.VerifyAsync(result, created, MetaReference.Href(counterparty.Entity)!, cancellationToken);
        result.Status = result.Errors.Count == 0 && broken.Count == 0 && result.Relations.All(relation => relation.Verified)
            ? "passed"
            : "failed";
        manifest.Save();
        output.WriteLine($"  => {result.Status.ToUpperInvariant()}");
        return result;
    }

    private void Record(ManifestScenario result, StepPlan step, JsonObject entity)
    {
        var href = MetaReference.Href(entity) ?? throw new MoySkladApiException("POST", step.Step.DocumentType, null, null,
            "created object has no meta.href");
        var id = MetaReference.Id(href)!;
        result.Documents.Add(new ManifestDocument
        {
            Key = step.Step.Key,
            Type = step.Step.DocumentType,
            Id = id,
            Name = entity["name"]?.GetValue<string>(),
            Href = href,
            Moment = entity["moment"]?.GetValue<string>(),
            ExternalCode = entity["externalCode"]?.GetValue<string>() ?? step.ExternalCode,
            DependsOn = step.Step.Dependencies.ToList(),
            Sequence = manifest.NextSequence()
        });
        foreach (var relation in result.Relations)
        {
            if (relation.SourceStep == step.Step.Key) relation.SourceId = id;
            if (relation.TargetStep == step.Step.Key) relation.TargetId = id;
        }
    }

    private static string Flag(bool value) => value ? "with" : "without";
}
