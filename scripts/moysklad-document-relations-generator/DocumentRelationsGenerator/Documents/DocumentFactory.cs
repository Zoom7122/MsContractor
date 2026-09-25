using System.Text.Json.Nodes;
using DocumentRelationsGenerator.Execution;
using DocumentRelationsGenerator.MoySklad;
using DocumentRelationsGenerator.Randomization;
using DocumentRelationsGenerator.References;
using DocumentRelationsGenerator.Relations;
using DocumentRelationsGenerator.Scenarios;

namespace DocumentRelationsGenerator.Documents;

/// <summary>
/// Builds request bodies. Every business field goes through <see cref="Set"/>, which rejects fields the
/// document type does not have, and relation fields come only from <see cref="RelationCatalog"/> links.
/// Links are always written on the side that stores them: the reference fields of the other side
/// (for example salesreturn.factureOut) are accepted by POST/PUT but not saved (verified by GET).
/// </summary>
public sealed class DocumentFactory
{
    /// <summary>Template keys that describe the template itself rather than the new document.</summary>
    private static readonly string[] TemplateNoise =
        ["meta", "id", "accountId", "created", "updated", "deleted", "printed", "published", "files", "syncId"];

    private static readonly string[] PositionNoise = ["meta", "id", "accountId"];

    private static readonly HashSet<string> Returns = ["salesreturn", "purchasereturn", "retailsalesreturn"];
    private static readonly HashSet<string> Payments = ["paymentin", "paymentout", "cashin", "cashout"];
    private static readonly HashSet<string> OutgoingPayments = ["paymentout", "cashout"];
    private static readonly HashSet<string> NoPositionDiscount = ["commissionreportin", "commissionreportout", "loss", "purchasereturn"];

    private readonly ReferenceData refs;
    private readonly RunIdentity run;

    public DocumentFactory(ReferenceData refs, RunIdentity run)
    {
        this.refs = refs;
        this.run = run;
    }

    public JsonObject BuildRoot(ScenarioPlan scenario, StepPlan step, CounterpartyData counterparty)
    {
        var type = step.Step.DocumentType;
        var payload = new JsonObject();
        Set(payload, type, "organization", MetaReference.To(refs.Organization));
        Set(payload, type, "agent", MetaReference.To(counterparty.Entity));
        if (DocumentFieldSupport.Supports(type, "store")) Set(payload, type, "store", MetaReference.To(RequireStore()));
        ApplyAgreement(payload, type, scenario, step, counterparty);
        ApplyCommon(payload, type, scenario, step);

        if (DocumentFieldSupport.Supports(type, "positions"))
        {
            var vatEnabled = step.VatEnabled && refs.VatRates.Count > 0;
            Set(payload, type, "vatEnabled", vatEnabled);
            // vatIncluded is read-only for commissionreportout.
            if (vatEnabled && DocumentFieldSupport.Supports(type, "vatIncluded")) Set(payload, type, "vatIncluded", step.VatIncluded);
            Set(payload, type, "positions", BuildPositions(type, step, vatEnabled));
        }

        if (type is "commissionreportin" or "commissionreportout")
        {
            // "contract" is required and must be a commission contract with the same counterparty.
            Set(payload, type, "contract", MetaReference.To(counterparty.CommissionContract
                ?? throw new InvalidOperationException("Commission contract was not prepared.")));
            Set(payload, type, "commissionPeriodStart", MoySkladTime.Format(step.PeriodStart));
            Set(payload, type, "commissionPeriodEnd", MoySkladTime.Format(step.PeriodEnd));
        }

        if (DocumentFieldSupport.Supports(type, "incomingNumber"))
        {
            Set(payload, type, "incomingNumber", step.IncomingNumber);
            Set(payload, type, "incomingDate", MoySkladTime.Format(step.IncomingDate));
        }

        if (Payments.Contains(type))
        {
            // A payment without operations: an advance that is not linked to any document.
            Set(payload, type, "sum", step.AmountKopecks);
            Set(payload, type, "paymentPurpose", step.PaymentPurpose);
            if (OutgoingPayments.Contains(type)) Set(payload, type, "expenseItem", MetaReference.To(RequireExpenseItem()));
        }

        if (DocumentFieldSupport.Supports(type, "deliveryPlannedMoment"))
            Set(payload, type, "deliveryPlannedMoment", MoySkladTime.Format(step.PlannedMoment));
        if (DocumentFieldSupport.Supports(type, "paymentPlannedMoment"))
            Set(payload, type, "paymentPlannedMoment", MoySkladTime.Format(step.PlannedMoment));
        return payload;
    }

    /// <summary>Body of <c>PUT /entity/{type}/new</c>: the base documents wrapped in the relation fields.</summary>
    public static JsonObject BuildTemplateRequest(ScenarioStep step, IReadOnlyDictionary<string, JsonObject> created)
    {
        var body = new JsonObject();
        foreach (var link in step.Links)
        {
            var relation = RelationCatalog.Get(link.RelationId);
            if (relation.Creation == CreationPath.Template) AddLink(body, relation, created[link.TargetStep], linkedSum: null);
        }

        return body;
    }

    public JsonObject BuildFromTemplate(ScenarioPlan scenario, StepPlan step, JsonObject template,
        CounterpartyData counterparty, IReadOnlyDictionary<string, JsonObject> created)
    {
        var type = step.Step.DocumentType;
        var runtime = new TestDataRandomizer(step.RuntimeSeed);
        var payload = CleanTemplate(template);

        // Links written through the template must reach the POST even if the template omitted them.
        foreach (var link in step.Step.Links)
        {
            var relation = RelationCatalog.Get(link.RelationId);
            if (!Contains(payload, relation, created[link.TargetStep]))
                AddLink(payload, relation, created[link.TargetStep], linkedSum: null);
        }

        ApplyCommon(payload, type, scenario, step);

        // purchaseorder templates deliberately omit the supplier (documentation, "Шаблон Заказа поставщику на основе").
        if (payload["agent"] is null && DocumentFieldSupport.Supports(type, "agent"))
            Set(payload, type, "agent", MetaReference.To(counterparty.Entity));
        if (payload["store"] is null && DocumentFieldSupport.Supports(type, "store"))
            Set(payload, type, "store", MetaReference.To(RequireStore()));

        if (Returns.Contains(type)) ReducePositions(payload, runtime);
        if (Payments.Contains(type)) ApplyTemplatePayment(payload, type, step, runtime, created[step.Step.Links[0].TargetStep]);

        switch (type)
        {
            case "facturein":
                // Not taken from the base: must be passed explicitly ("Создать Счет-фактуру").
                Set(payload, type, "incomingNumber", step.IncomingNumber);
                Set(payload, type, "incomingDate", MoySkladTime.Format(step.IncomingDate));
                break;
            case "factureout" when step.Step.Links.Any(link => RelationCatalog.Get(link.RelationId).Field == "payments"):
                // paymentPurpose is only available for factures based on payments.
                Set(payload, type, "paymentPurpose", step.PaymentPurpose);
                break;
            case "retaildemand":
                if (payload["retailStore"] is null) Set(payload, type, "retailStore", MetaReference.To(RequireRetailStore()));
                SplitRetailPayment(payload, type, step);
                break;
        }

        return payload;
    }

    /// <summary>Payment with an explicit operations collection and a linkedSum per operation.</summary>
    public JsonObject BuildPaymentPost(ScenarioPlan scenario, StepPlan step, CounterpartyData counterparty,
        IReadOnlyDictionary<string, JsonObject> created)
    {
        var type = step.Step.DocumentType;
        var runtime = new TestDataRandomizer(step.RuntimeSeed);
        var payload = new JsonObject();
        Set(payload, type, "organization", MetaReference.To(refs.Organization));
        Set(payload, type, "agent", MetaReference.To(counterparty.Entity));
        ApplyAgreement(payload, type, scenario, step, counterparty);
        ApplyCommon(payload, type, scenario, step);

        long total = 0;
        foreach (var link in step.Step.Links)
        {
            var target = created[link.TargetStep];
            var targetSum = ReadSum(target);
            var linkedSum = targetSum > 0
                ? Math.Max(100, (long)Math.Round(targetSum * step.PaymentShare))
                : runtime.Between(500, 5000) * 100L;
            AddLink(payload, RelationCatalog.Get(link.RelationId), target, linkedSum);
            total += linkedSum;
        }

        Set(payload, type, "sum", total);
        Set(payload, type, "paymentPurpose", step.PaymentPurpose);
        if (OutgoingPayments.Contains(type)) Set(payload, type, "expenseItem", MetaReference.To(RequireExpenseItem()));
        return payload;
    }

    public JsonObject BuildRetailShift(StepPlan step)
    {
        var retailStore = RequireRetailStore();
        var payload = new JsonObject();
        const string type = "retailshift";
        Set(payload, type, "name", step.Name);
        Set(payload, type, "description", Describe(step, "retail-flow"));
        Set(payload, type, "externalCode", step.ExternalCode);
        Set(payload, type, "moment", MoySkladTime.Format(step.Moment));
        Set(payload, type, "organization", MetaReference.To(refs.Organization));
        Set(payload, type, "retailStore", MetaReference.To(retailStore));
        Set(payload, type, "syncId", step.SyncId.ToString("D"));
        return payload;
    }

    /// <summary>Drops template-only keys, turns <c>positions.rows</c> into a plain array, removes nulls.</summary>
    public static JsonObject CleanTemplate(JsonObject template)
    {
        var payload = (JsonObject)template.DeepClone();
        foreach (var key in TemplateNoise) payload.Remove(key);
        foreach (var key in payload.Where(pair => pair.Value is null).Select(pair => pair.Key).ToList()) payload.Remove(key);

        if (payload["positions"] is JsonObject { } metaArray)
            payload["positions"] = metaArray["rows"] is JsonArray rows ? rows.DeepClone() : new JsonArray();
        if (payload["positions"] is JsonArray positions)
            foreach (var position in positions.OfType<JsonObject>())
            foreach (var key in PositionNoise)
                position.Remove(key);
        return payload;
    }

    /// <summary>Throws for a field the document type does not have: unknown fields are never sent.</summary>
    public static void Set(JsonObject payload, string documentType, string field, JsonNode? value)
    {
        if (!DocumentFieldSupport.Supports(documentType, field))
            throw new InvalidOperationException($"{documentType} has no writable field '{field}'.");
        payload[field] = value;
    }

    private JsonArray BuildPositions(string type, StepPlan step, bool vatEnabled)
    {
        var positions = new JsonArray();
        foreach (var plan in step.Positions)
        {
            var assortment = plan.IsService
                ? refs.Services[plan.AssortmentIndex % refs.Services.Count]
                : refs.Products[plan.AssortmentIndex % refs.Products.Count];
            var position = new JsonObject
            {
                ["assortment"] = MetaReference.To(assortment),
                ["quantity"] = plan.Quantity,
                ["price"] = plan.PriceKopecks,
                // vat = 0 with vatEnabled = false means "без НДС" (positions table of the documentation).
                ["vat"] = vatEnabled ? refs.VatRates[plan.VatSlot % refs.VatRates.Count] : 0,
                ["vatEnabled"] = vatEnabled
            };
            if (!NoPositionDiscount.Contains(type)) position["discount"] = plan.Discount;
            positions.Add(position);
        }

        return positions;
    }

    private void ApplyCommon(JsonObject payload, string type, ScenarioPlan scenario, StepPlan step)
    {
        Set(payload, type, "name", step.Name);
        Set(payload, type, "description", Describe(step, scenario.Scenario.Name));
        Set(payload, type, "externalCode", step.ExternalCode);
        Set(payload, type, "moment", MoySkladTime.Format(step.Moment));
        Set(payload, type, "applicable", step.Applicable);
        if (DocumentFieldSupport.Supports(type, "shared")) Set(payload, type, "shared", step.Shared);
        if (DocumentFieldSupport.Supports(type, "syncId")) Set(payload, type, "syncId", step.SyncId.ToString("D"));
        if (step.SetProject && refs.Project is not null && DocumentFieldSupport.Supports(type, "project"))
            Set(payload, type, "project", MetaReference.To(refs.Project));
        if (step.SetOwner && refs.Employee is not null)
        {
            Set(payload, type, "owner", MetaReference.To(refs.Employee));
            if (refs.Employee["group"] is JsonObject group) Set(payload, type, "group", group.DeepClone());
        }
    }

    /// <summary>contract / agentAccount / organizationAccount / rate for documents built from scratch.</summary>
    private void ApplyAgreement(JsonObject payload, string type, ScenarioPlan scenario, StepPlan step,
        CounterpartyData counterparty)
    {
        if (step.WithContract && counterparty.SalesContract is not null && DocumentFieldSupport.Supports(type, "contract"))
            Set(payload, type, "contract", MetaReference.To(counterparty.SalesContract));
        // A document of a counterparty that has accounts cannot be saved without agentAccount (live API: omitted ->
        // MoySklad sets the default account; null -> 412/3000). The variants are therefore "default account set by
        // MoySklad" and "an explicitly chosen non-default account".
        if (step.WithAgentAccount && DocumentFieldSupport.Supports(type, "agentAccount") &&
            counterparty.Accounts.FirstOrDefault(account => account["isDefault"] is not JsonValue flag || !flag.GetValue<bool>()) is { } explicitAccount)
            Set(payload, type, "agentAccount", MetaReference.To(explicitAccount));

        if (scenario.WithForeignCurrency && refs.ForeignCurrency is not null)
            Set(payload, type, "rate", new JsonObject { ["currency"] = MetaReference.To(refs.ForeignCurrency) });
        // A foreign document currency must match the organization account currency (error 22004), so the
        // organization account is only set for documents in the default currency.
        else if (step.SetOrganizationAccount && refs.OrganizationAccounts.Count > 0 &&
                 DocumentFieldSupport.Supports(type, "organizationAccount"))
            Set(payload, type, "organizationAccount",
                MetaReference.To(refs.OrganizationAccounts[step.Index % refs.OrganizationAccounts.Count]));
    }

    private void ApplyTemplatePayment(JsonObject payload, string type, StepPlan step, TestDataRandomizer runtime,
        JsonObject baseDocument)
    {
        var templateSum = ReadSum(payload);
        var sum = templateSum > 0
            ? Math.Max(100, (long)Math.Round(templateSum * step.PaymentShare))
            : runtime.Between(500, 5000) * 100L;
        Set(payload, type, "sum", sum);
        // The template's vatSum is the VAT of the whole base document while its sum is only the unpaid rest,
        // so it can exceed the payment (seen live: error 3007 "vatSum не может быть больше sum"). The payment
        // VAT therefore uses the base document's own VAT share.
        var baseSum = ReadDecimal(baseDocument["sum"]);
        var baseVat = ReadDecimal(baseDocument["vatSum"]);
        if (payload["vatSum"] is not null || baseVat > 0)
            Set(payload, type, "vatSum", baseSum > 0 ? (long)Math.Round(sum * Math.Min(baseVat, baseSum) / baseSum) : 0);
        if (payload["operations"] is JsonArray { Count: 1 } operations && operations[0] is JsonObject operation)
            operation["linkedSum"] = sum;
        Set(payload, type, "paymentPurpose", step.PaymentPurpose);
        if (OutgoingPayments.Contains(type) && payload["expenseItem"] is null)
            Set(payload, type, "expenseItem", MetaReference.To(RequireExpenseItem()));
        if (type == "paymentin" && runtime.Chance(0.5))
        {
            Set(payload, type, "incomingNumber", step.IncomingNumber);
            Set(payload, type, "incomingDate", MoySkladTime.Format(step.IncomingDate));
        }
    }

    /// <summary>Return positions must come from the base; quantity may only go down, price stays.</summary>
    private static void ReducePositions(JsonObject payload, TestDataRandomizer runtime)
    {
        if (payload["positions"] is not JsonArray positions || positions.Count == 0) return;
        var keep = positions.OfType<JsonObject>().Where(position => ReadDecimal(position["quantity"]) > 0).ToList();
        if (keep.Count > 1 && runtime.Chance(0.4)) keep.RemoveAt(runtime.Between(0, keep.Count - 1));
        foreach (var position in keep) position["quantity"] = runtime.ReduceQuantity(ReadDecimal(position["quantity"]));
        payload["positions"] = new JsonArray(keep.Select(position => (JsonNode)position.DeepClone()).ToArray());
    }

    private static void SplitRetailPayment(JsonObject payload, string type, StepPlan step)
    {
        var sum = ReadSum(payload);
        if (sum <= 0) return;
        var cash = (long)Math.Round(sum * step.PaymentShare);
        Set(payload, type, "cashSum", cash);
        Set(payload, type, "noCashSum", sum - cash);
    }

    private static void AddLink(JsonObject payload, RelationDefinition relation, JsonObject target, long? linkedSum)
    {
        var reference = MetaReference.To(target);
        switch (relation.Kind)
        {
            case RelationKind.Reference:
                payload[relation.Field] = reference;
                break;
            case RelationKind.Collection:
                (payload[relation.Field] as JsonArray ?? (JsonArray)(payload[relation.Field] = new JsonArray())).Add(reference);
                break;
            case RelationKind.Operation:
                if (linkedSum is not null) reference["linkedSum"] = linkedSum.Value;
                (payload["operations"] as JsonArray ?? (JsonArray)(payload["operations"] = new JsonArray())).Add(reference);
                break;
        }
    }

    private static bool Contains(JsonObject payload, RelationDefinition relation, JsonObject target)
    {
        var href = MetaReference.Href(target);
        var node = payload[relation.Kind == RelationKind.Operation ? "operations" : relation.Field];
        return node switch
        {
            JsonArray array => array.Any(item => MetaReference.SameEntity(MetaReference.Href(item), href)),
            JsonObject obj => MetaReference.SameEntity(MetaReference.Href(obj), href),
            _ => false
        };
    }

    private string Describe(StepPlan step, string scenario) =>
        $"{step.Description}. {RunIdentity.Prefix} run {run.RunId}, {scenario}/{step.Step.Key}";

    private static long ReadSum(JsonObject document) => (long)Math.Round(ReadDecimal(document["sum"]));

    private static decimal ReadDecimal(JsonNode? node) => JsonNumbers.Read(node);

    private JsonObject RequireStore() => refs.Store ?? throw new InvalidOperationException("Store was not prepared.");

    private JsonObject RequireExpenseItem() =>
        refs.ExpenseItem ?? throw new InvalidOperationException("Expense item was not prepared.");

    private JsonObject RequireRetailStore() =>
        refs.RetailStore ?? throw new InvalidOperationException("Retail store is not configured.");
}
