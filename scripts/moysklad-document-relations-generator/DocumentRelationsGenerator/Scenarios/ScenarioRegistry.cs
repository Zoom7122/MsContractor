using DocumentRelationsGenerator.Relations;

namespace DocumentRelationsGenerator.Scenarios;

/// <summary>
/// Business graphs that together create every relation of <see cref="RelationCatalog"/> at least once.
/// Mutually exclusive bases (one base per facture, one facture per base) live in separate graphs.
/// </summary>
public static class ScenarioRegistry
{
    public static IReadOnlyList<ScenarioDefinition> All { get; } = Build();

    public static IReadOnlyList<ScenarioDefinition> Select(IReadOnlyCollection<string> names)
    {
        if (names.Count == 0) return All;
        var unknown = names.Where(name => All.All(scenario => scenario.Name != name)).ToList();
        if (unknown.Count > 0)
            throw new GeneratorException(
                $"Unknown scenario(s): {string.Join(", ", unknown)}. Use --list-scenarios to see available names.");
        return All.Where(scenario => names.Contains(scenario.Name)).ToList();
    }

    /// <summary>Position of the scenario in the registry; used for deterministic variant selection.</summary>
    public static int Ordinal(ScenarioDefinition scenario) => All.ToList().FindIndex(item => item.Name == scenario.Name);

    /// <summary>Structural rules every scenario must satisfy; violations are programming errors.</summary>
    public static void Validate(ScenarioDefinition scenario)
    {
        var steps = scenario.Steps.ToDictionary(step => step.Key, StringComparer.Ordinal);
        _ = DependencyOrder.Sort(scenario.Steps);
        foreach (var step in scenario.Steps)
        {
            if (!DocumentFieldSupport.DocumentTypes.Contains(step.DocumentType))
                throw new InvalidOperationException($"{scenario.Name}/{step.Key}: unsupported type {step.DocumentType}.");
            foreach (var link in step.Links)
            {
                var relation = RelationCatalog.Get(link.RelationId);
                if (relation.SourceType != step.DocumentType || steps[link.TargetStep].DocumentType != relation.TargetType)
                    throw new InvalidOperationException($"{scenario.Name}/{step.Key}: link {link.RelationId} does not match step types.");
            }

            var valid = step.Kind switch
            {
                StepKind.Root or StepKind.RetailShift => step.Links.Count == 0,
                StepKind.Template => step.Links.Count > 0 &&
                                     step.Links.All(link => RelationCatalog.Get(link.RelationId).Creation == CreationPath.Template),
                StepKind.PaymentPost => step.Links.Count > 0 &&
                                        step.Links.All(link => RelationCatalog.Get(link.RelationId).Kind == RelationKind.Operation),
                _ => false
            };
            if (!valid) throw new InvalidOperationException($"{scenario.Name}/{step.Key}: links do not fit step kind {step.Kind}.");
            if (step.Kind == StepKind.RetailShift && step.DocumentType != "retailshift")
                throw new InvalidOperationException($"{scenario.Name}/{step.Key}: RetailShift step must create retailshift.");

            // "Документ-основание должен быть указан в единственном экземпляре" for both facture types.
            if (step.DocumentType is "factureout" or "facturein" &&
                step.Links.Count(link => RelationCatalog.Get(link.RelationId).SourceType == step.DocumentType) != 1)
                throw new InvalidOperationException($"{scenario.Name}/{step.Key}: a facture needs exactly one base.");
        }

        var factureBases = scenario.Steps
            .Where(step => step.DocumentType is "factureout" or "facturein")
            .SelectMany(step => step.Links.Select(link => link.TargetStep))
            .GroupBy(key => key)
            .FirstOrDefault(group => group.Count() > 1);
        if (factureBases is not null)
            throw new InvalidOperationException($"{scenario.Name}: step '{factureBases.Key}' is the base of several factures.");
    }

    private static List<ScenarioDefinition> Build()
    {
        var scenarios = new List<ScenarioDefinition>
        {
            new("sales-flow",
                "customerorder with invoiceout and demand; factureout, paymentin and cashin on the chain",
                ScenarioRequirement.None,
                [
                    Root("customerorder", "customerorder", services: true),
                    Template("invoiceout", "invoiceout", Link("invoiceout.customerOrder->customerorder", "customerorder")),
                    Template("demand", "demand", Link("demand.customerOrder->customerorder", "customerorder")),
                    Template("factureout-from-demand", "factureout", Link("factureout.demands->demand", "demand")),
                    Template("paymentin", "paymentin", Link("paymentin.operations->customerorder", "customerorder")),
                    Template("cashin", "cashin", Link("cashin.operations->demand", "demand"))
                ]),
            new("sales-invoice-first",
                "invoiceout without order, demand on the invoice, payments and factureout on paymentin",
                ScenarioRequirement.None,
                [
                    Root("invoiceout", "invoiceout", services: true),
                    Template("demand", "demand", Link("demand.invoicesOut->invoiceout", "invoiceout")),
                    Template("paymentin", "paymentin", Link("paymentin.operations->invoiceout", "invoiceout")),
                    Template("cashin", "cashin", Link("cashin.operations->invoiceout", "invoiceout")),
                    Template("factureout-from-paymentin", "factureout", Link("factureout.payments->paymentin", "paymentin"))
                ]),
            new("sales-return-flow",
                "demand with invoiceout and paymentin; salesreturn with loss, paymentout and cashout",
                ScenarioRequirement.None,
                [
                    Root("demand", "demand"),
                    Template("invoiceout", "invoiceout", Link("invoiceout.demands->demand", "demand")),
                    Template("paymentin", "paymentin", Link("paymentin.operations->demand", "demand")),
                    Template("salesreturn", "salesreturn", Link("salesreturn.demand->demand", "demand")),
                    Template("loss", "loss", Link("loss.salesReturn->salesreturn", "salesreturn")),
                    Template("paymentout", "paymentout", Link("paymentout.operations->salesreturn", "salesreturn")),
                    Template("cashout", "cashout", Link("cashout.operations->salesreturn", "salesreturn"))
                ]),
            new("factureout-from-cashin",
                "cashin on a demand and a factureout based on that cashin",
                ScenarioRequirement.None,
                [
                    Root("demand", "demand", services: true),
                    Template("cashin", "cashin", Link("cashin.operations->demand", "demand")),
                    Template("factureout-from-cashin", "factureout", Link("factureout.payments->cashin", "cashin"))
                ]),
            new("customerorder-procurement",
                "customerorder with a purchaseorder for it; cashin on the order and cashout on the purchase order",
                ScenarioRequirement.None,
                [
                    Root("customerorder", "customerorder", services: true),
                    Template("purchaseorder", "purchaseorder",
                        Link("purchaseorder.customerOrders->customerorder", "customerorder")),
                    Template("cashin", "cashin", Link("cashin.operations->customerorder", "customerorder")),
                    Template("cashout", "cashout", Link("cashout.operations->purchaseorder", "purchaseorder"))
                ]) { AllowsForeignCurrency = true },
            new("purchase-flow",
                "purchaseorder with supply and invoicein; facturein on supply and on the cashout; payments",
                ScenarioRequirement.None,
                [
                    Root("purchaseorder", "purchaseorder", services: true),
                    Template("supply", "supply", Link("supply.purchaseOrder->purchaseorder", "purchaseorder")),
                    Template("invoicein", "invoicein", Link("invoicein.purchaseOrder->purchaseorder", "purchaseorder")),
                    Template("facturein-from-supply", "facturein", Link("facturein.supplies->supply", "supply")),
                    Template("paymentout", "paymentout", Link("paymentout.operations->purchaseorder", "purchaseorder")),
                    Template("cashout", "cashout", Link("cashout.operations->supply", "supply")),
                    Template("facturein-from-cashout", "facturein", Link("facturein.payments->cashout", "cashout"))
                ]),
            new("purchase-invoice-first",
                "invoicein without order, supply on the invoice, payments and facturein on paymentout",
                ScenarioRequirement.None,
                [
                    Root("invoicein", "invoicein", services: true),
                    Template("supply", "supply", Link("supply.invoicesIn->invoicein", "invoicein")),
                    Template("paymentout", "paymentout", Link("paymentout.operations->invoicein", "invoicein")),
                    Template("cashout", "cashout", Link("cashout.operations->invoicein", "invoicein")),
                    Template("facturein-from-paymentout", "facturein", Link("facturein.payments->paymentout", "paymentout"))
                ]),
            new("purchase-return-flow",
                "supply with invoicein and facturein; purchasereturn with factureout and payments",
                ScenarioRequirement.None,
                [
                    Root("supply", "supply"),
                    Template("invoicein", "invoicein", Link("invoicein.supplies->supply", "supply")),
                    Template("facturein-from-supply", "facturein", Link("facturein.supplies->supply", "supply")),
                    Template("purchasereturn", "purchasereturn", Link("purchasereturn.supply->supply", "supply")),
                    Template("factureout-from-purchasereturn", "factureout",
                        Link("factureout.returns->purchasereturn", "purchasereturn")),
                    Template("paymentin", "paymentin", Link("paymentin.operations->purchasereturn", "purchasereturn")),
                    Template("cashin", "cashin", Link("cashin.operations->purchasereturn", "purchasereturn")),
                    Template("paymentout", "paymentout", Link("paymentout.operations->supply", "supply"))
                ]),
            new("commission-flow",
                "commission reports (commission contract) with incoming and outgoing payments",
                ScenarioRequirement.None,
                [
                    Root("commissionreportin", "commissionreportin"),
                    Root("commissionreportout", "commissionreportout"),
                    Template("paymentin", "paymentin", Link("paymentin.operations->commissionreportin", "commissionreportin")),
                    Template("cashin", "cashin", Link("cashin.operations->commissionreportin", "commissionreportin")),
                    Template("paymentout", "paymentout", Link("paymentout.operations->commissionreportout", "commissionreportout")),
                    Template("cashout", "cashout", Link("cashout.operations->commissionreportout", "commissionreportout"))
                ]),
            new("payment-multi-operations",
                "one paymentin split between a customerorder and its invoiceout (two operations, linkedSum each)",
                ScenarioRequirement.None,
                [
                    Root("customerorder", "customerorder", services: true),
                    Template("invoiceout", "invoiceout", Link("invoiceout.customerOrder->customerorder", "customerorder")),
                    PaymentPost("paymentin", "paymentin",
                        Link("paymentin.operations->customerorder", "customerorder"),
                        Link("paymentin.operations->invoiceout", "invoiceout"))
                ]),
            new("retail-flow",
                "retail shift, retaildemand on a customerorder, retailsalesreturn, payments linked to the shift",
                ScenarioRequirement.RetailStore,
                [
                    new ScenarioStep("retailshift", "retailshift", StepKind.RetailShift, []),
                    Root("customerorder", "customerorder"),
                    Template("retaildemand", "retaildemand",
                        Link("retaildemand.retailShift->retailshift", "retailshift"),
                        Link("retaildemand.customerOrder->customerorder", "customerorder")),
                    Template("retailsalesreturn", "retailsalesreturn",
                        Link("retailsalesreturn.demand->retaildemand", "retaildemand"),
                        Link("retailsalesreturn.retailShift->retailshift", "retailshift")),
                    PaymentPost("cashin", "cashin", Link("cashin.operations->retailshift", "retailshift")),
                    PaymentPost("paymentin", "paymentin", Link("paymentin.operations->retailshift", "retailshift"))
                ]),
            FactureOutFull()
        };

        foreach (var scenario in scenarios) Validate(scenario);
        return scenarios;
    }

    /// <summary>
    /// Every business context of factureout on one counterparty. A facture has exactly one base (demand, paymentin,
    /// cashin or purchasereturn) and a base has one facture, so each context gets its own factureout. Payments are
    /// placed on every document type they can be linked to, and on nothing (an advance without a document).
    /// </summary>
    private static ScenarioDefinition FactureOutFull()
    {
        var steps = new List<ScenarioStep>();

        void Payments(string prefix, string target, string targetType)
        {
            foreach (var payment in new[] { "paymentin", "cashin" })
            {
                steps.Add(Template($"{prefix}-{payment}", payment, Link($"{payment}.operations->{targetType}", target)));
                steps.Add(Template($"fo-{prefix}-{payment}", "factureout", Link($"factureout.payments->{payment}", $"{prefix}-{payment}")));
            }
        }

        // Sale by order: invoice, shipment with its facture, advances (bank and cash) with advance factures.
        steps.Add(Root("order", "customerorder", services: true));
        steps.Add(Template("order-invoice", "invoiceout", Link("invoiceout.customerOrder->customerorder", "order")));
        steps.Add(Template("order-demand", "demand", Link("demand.customerOrder->customerorder", "order")));
        steps.Add(Template("fo-order-demand", "factureout", Link("factureout.demands->demand", "order-demand")));
        Payments("order-advance", "order", "customerorder");

        // Sale by invoice without an order.
        steps.Add(Root("invoice", "invoiceout", services: true));
        steps.Add(Template("invoice-demand", "demand", Link("demand.invoicesOut->invoiceout", "invoice")));
        steps.Add(Template("fo-invoice-demand", "factureout", Link("factureout.demands->demand", "invoice-demand")));
        Payments("invoice", "invoice", "invoiceout");

        // Shipment without order or invoice, paid after shipping, partly returned after the facture.
        steps.Add(Root("demand", "demand"));
        steps.Add(Template("fo-demand", "factureout", Link("factureout.demands->demand", "demand")));
        steps.Add(Template("demand-return", "salesreturn", Link("salesreturn.demand->demand", "demand")));
        Payments("demand", "demand", "demand");

        // Advance received without any document.
        foreach (var payment in new[] { "paymentin", "cashin" })
        {
            steps.Add(Root($"advance-{payment}", payment));
            steps.Add(Template($"fo-advance-{payment}", "factureout", Link($"factureout.payments->{payment}", $"advance-{payment}")));
        }

        // Return to supplier by supply, with the supplier's refund (bank and cash).
        steps.Add(Root("supply", "supply"));
        steps.Add(Template("supply-return", "purchasereturn", Link("purchasereturn.supply->supply", "supply")));
        steps.Add(Template("fo-supply-return", "factureout", Link("factureout.returns->purchasereturn", "supply-return")));
        Payments("supply-return-refund", "supply-return", "purchasereturn");

        // Return to supplier without a supply ("возврат без основания").
        steps.Add(Root("return-without-base", "purchasereturn"));
        steps.Add(Template("fo-return-without-base", "factureout", Link("factureout.returns->purchasereturn", "return-without-base")));

        // Commissioner's report paid by the commissioner.
        steps.Add(Root("commission-report", "commissionreportin"));
        Payments("commission", "commission-report", "commissionreportin");

        return new ScenarioDefinition("factureout-full",
            "all factureout bases and business contexts on one counterparty: demand (by order, by invoice, standalone), " +
            "paymentin/cashin (on order, invoice, demand, purchasereturn, commission report, without document), purchasereturn " +
            "(by supply, without base)",
            ScenarioRequirement.None, steps) { AlternatesAgreementsPerRoot = true };
    }

    private static ScenarioStep Root(string key, string type, bool services = false) =>
        new(key, type, StepKind.Root, []) { AllowServices = services };

    private static ScenarioStep Template(string key, string type, params StepLink[] links) =>
        new(key, type, StepKind.Template, links);

    private static ScenarioStep PaymentPost(string key, string type, params StepLink[] links) =>
        new(key, type, StepKind.PaymentPost, links);

    private static StepLink Link(string relationId, string targetStep) => new(relationId, targetStep);
}
