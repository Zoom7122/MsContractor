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
            if (step.AddsPositions && (step.Kind != StepKind.Template || !DocumentFieldSupport.Supports(step.DocumentType, "positions")))
                throw new InvalidOperationException($"{scenario.Name}/{step.Key}: only template steps with positions can add positions.");
            if (step.ReturnsToCommissioner && (step.Kind != StepKind.Root || step.DocumentType != "commissionreportin"))
                throw new InvalidOperationException($"{scenario.Name}/{step.Key}: returnToCommissionerPositions belong to a commissionreportin root.");

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
            FactureOutFull(),
            SalesReturnFull(),
            PurchaseReturnFull(),
            FactureInFull(),
            RetailDemandFull(),
            RetailSalesReturnFull(),
            CommissionReportInFull(),
            CommissionReportOutFull()
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

    private static ScenarioDefinition Full(string name, string description, List<ScenarioStep> steps,
        ScenarioRequirement requirement = ScenarioRequirement.None) =>
        new(name, description, requirement, steps) { AlternatesAgreementsPerRoot = true };

    /// <summary>
    /// Every context of a sales return on one counterparty: by an order shipment (refunds and write-off), by an
    /// invoice shipment that already has a factureout, by a standalone shipment, without a base document, and a
    /// refund paid in one outgoing payment together with a supplier payment (two operations).
    /// </summary>
    private static ScenarioDefinition SalesReturnFull() => Full("salesreturn-full",
        "all salesreturn contexts on one counterparty: by order/invoice/standalone demand, without base; loss, paymentout, " +
        "cashout, a paymentout with two operations",
        [
            Root("order", "customerorder"),
            Template("order-demand", "demand", Link("demand.customerOrder->customerorder", "order")),
            Template("order-return", "salesreturn", Link("salesreturn.demand->demand", "order-demand")),
            Template("order-return-loss", "loss", Link("loss.salesReturn->salesreturn", "order-return")),
            Template("order-return-paymentout", "paymentout", Link("paymentout.operations->salesreturn", "order-return")),
            Template("order-return-cashout", "cashout", Link("cashout.operations->salesreturn", "order-return")),
            Root("invoice", "invoiceout"),
            Template("invoice-demand", "demand", Link("demand.invoicesOut->invoiceout", "invoice")),
            Template("fo-invoice-demand", "factureout", Link("factureout.demands->demand", "invoice-demand")),
            Template("invoice-return", "salesreturn", Link("salesreturn.demand->demand", "invoice-demand")),
            Template("invoice-return-paymentout", "paymentout", Link("paymentout.operations->salesreturn", "invoice-return")),
            Root("demand", "demand"),
            Template("demand-return", "salesreturn", Link("salesreturn.demand->demand", "demand")),
            Root("return-without-base", "salesreturn"),
            Template("rwb-loss", "loss", Link("loss.salesReturn->salesreturn", "return-without-base")),
            Template("rwb-cashout", "cashout", Link("cashout.operations->salesreturn", "return-without-base")),
            Root("supply", "supply"),
            PaymentPost("netting-paymentout", "paymentout",
                Link("paymentout.operations->salesreturn", "demand-return"),
                Link("paymentout.operations->supply", "supply"))
        ]);

    /// <summary>
    /// Every context of a purchase return: by an order supply, by an invoice supply, by a supply with a received
    /// facture and an issued facture on the return (MSContractor skips such returns), without base, and a refund
    /// received in one payment together with a customer payment.
    /// </summary>
    private static ScenarioDefinition PurchaseReturnFull() => Full("purchasereturn-full",
        "all purchasereturn contexts on one counterparty: by order/invoice/standalone supply, with factures, without base; " +
        "paymentin, cashin, a paymentin with two operations",
        [
            Root("order", "purchaseorder"),
            Template("order-supply", "supply", Link("supply.purchaseOrder->purchaseorder", "order")),
            Template("order-return", "purchasereturn", Link("purchasereturn.supply->supply", "order-supply")),
            Template("order-return-paymentin", "paymentin", Link("paymentin.operations->purchasereturn", "order-return")),
            Template("order-return-cashin", "cashin", Link("cashin.operations->purchasereturn", "order-return")),
            Root("invoice", "invoicein"),
            Template("invoice-supply", "supply", Link("supply.invoicesIn->invoicein", "invoice")),
            Template("invoice-return", "purchasereturn", Link("purchasereturn.supply->supply", "invoice-supply")),
            Template("invoice-return-cashin", "cashin", Link("cashin.operations->purchasereturn", "invoice-return")),
            Root("supply", "supply"),
            Template("fi-supply", "facturein", Link("facturein.supplies->supply", "supply")),
            Template("supply-return", "purchasereturn", Link("purchasereturn.supply->supply", "supply")),
            Template("fo-supply-return", "factureout", Link("factureout.returns->purchasereturn", "supply-return")),
            Template("supply-return-paymentin", "paymentin", Link("paymentin.operations->purchasereturn", "supply-return")),
            Root("return-without-base", "purchasereturn"),
            Template("rwb-paymentin", "paymentin", Link("paymentin.operations->purchasereturn", "return-without-base")),
            Root("demand", "demand"),
            PaymentPost("netting-paymentin", "paymentin",
                Link("paymentin.operations->purchasereturn", "invoice-return"),
                Link("paymentin.operations->demand", "demand"))
        ]);

    /// <summary>
    /// Every base and context of facturein: supply (by order, by invoice, standalone with a later return) and
    /// paymentout/cashout on each document type an outgoing payment can be linked to, plus advances without document.
    /// </summary>
    private static ScenarioDefinition FactureInFull()
    {
        var steps = new List<ScenarioStep>();
        void Payments(string prefix, string target, string targetType)
        {
            foreach (var payment in new[] { "paymentout", "cashout" })
            {
                steps.Add(Template($"{prefix}-{payment}", payment, Link($"{payment}.operations->{targetType}", target)));
                steps.Add(Template($"fi-{prefix}-{payment}", "facturein", Link($"facturein.payments->{payment}", $"{prefix}-{payment}")));
            }
        }

        steps.Add(Root("order", "purchaseorder", services: true));
        steps.Add(Template("order-supply", "supply", Link("supply.purchaseOrder->purchaseorder", "order")));
        steps.Add(Template("fi-order-supply", "facturein", Link("facturein.supplies->supply", "order-supply")));
        Payments("order-advance", "order", "purchaseorder");
        steps.Add(Root("invoice", "invoicein", services: true));
        steps.Add(Template("invoice-supply", "supply", Link("supply.invoicesIn->invoicein", "invoice")));
        steps.Add(Template("fi-invoice-supply", "facturein", Link("facturein.supplies->supply", "invoice-supply")));
        Payments("invoice", "invoice", "invoicein");
        steps.Add(Root("supply", "supply"));
        steps.Add(Template("fi-supply", "facturein", Link("facturein.supplies->supply", "supply")));
        steps.Add(Template("supply-return", "purchasereturn", Link("purchasereturn.supply->supply", "supply")));
        Payments("supply", "supply", "supply");
        foreach (var payment in new[] { "paymentout", "cashout" })
        {
            steps.Add(Root($"advance-{payment}", payment));
            steps.Add(Template($"fi-advance-{payment}", "facturein", Link($"facturein.payments->{payment}", $"advance-{payment}")));
        }
        steps.Add(Root("commission-report", "commissionreportout"));
        Payments("commission", "commission-report", "commissionreportout");
        steps.Add(Root("demand", "demand"));
        steps.Add(Template("demand-return", "salesreturn", Link("salesreturn.demand->demand", "demand")));
        Payments("customer-refund", "demand-return", "salesreturn");
        return Full("facturein-full",
            "all facturein bases and contexts on one counterparty: supply (by order, by invoice, standalone), paymentout/cashout " +
            "(on purchaseorder, invoicein, supply, commissionreportout, salesreturn, without document)", steps);
    }

    /// <summary>Retail sales in a test shift: without order, by an order paid in advance; returns and shift money.</summary>
    private static ScenarioDefinition RetailDemandFull() => Full("retaildemand-full",
        "all retaildemand contexts on one counterparty: sale in the shift without order, sale by an order with an advance, " +
        "returns of both, cash and bank money of the shift",
        [
            new ScenarioStep("shift", "retailshift", StepKind.RetailShift, []),
            Template("sale", "retaildemand", Link("retaildemand.retailShift->retailshift", "shift")) with { AddsPositions = true },
            Template("sale-return", "retailsalesreturn",
                Link("retailsalesreturn.demand->retaildemand", "sale"), Link("retailsalesreturn.retailShift->retailshift", "shift")),
            Root("order", "customerorder"),
            Template("order-advance-paymentin", "paymentin", Link("paymentin.operations->customerorder", "order")),
            Template("order-sale", "retaildemand",
                Link("retaildemand.retailShift->retailshift", "shift"), Link("retaildemand.customerOrder->customerorder", "order")),
            Template("order-sale-return", "retailsalesreturn",
                Link("retailsalesreturn.demand->retaildemand", "order-sale"), Link("retailsalesreturn.retailShift->retailshift", "shift")),
            PaymentPost("shift-cashin", "cashin", Link("cashin.operations->retailshift", "shift")),
            PaymentPost("shift-paymentin", "paymentin", Link("paymentin.operations->retailshift", "shift"))
        ], ScenarioRequirement.RetailStore);

    /// <summary>Retail returns: by a sale without order, by a sale by order, and without a base sale.</summary>
    private static ScenarioDefinition RetailSalesReturnFull() => Full("retailsalesreturn-full",
        "all retailsalesreturn contexts on one counterparty: by a shift sale, by a sale by order, without base sale",
        [
            new ScenarioStep("shift", "retailshift", StepKind.RetailShift, []),
            Template("sale", "retaildemand", Link("retaildemand.retailShift->retailshift", "shift")) with { AddsPositions = true },
            Template("sale-return", "retailsalesreturn",
                Link("retailsalesreturn.demand->retaildemand", "sale"), Link("retailsalesreturn.retailShift->retailshift", "shift")),
            Root("order", "customerorder"),
            Template("order-sale", "retaildemand",
                Link("retaildemand.retailShift->retailshift", "shift"), Link("retaildemand.customerOrder->customerorder", "order")),
            Template("order-sale-return", "retailsalesreturn",
                Link("retailsalesreturn.demand->retaildemand", "order-sale"), Link("retailsalesreturn.retailShift->retailshift", "shift")),
            Template("return-without-base", "retailsalesreturn",
                Link("retailsalesreturn.retailShift->retailshift", "shift")) with { AddsPositions = true }
        ], ScenarioRequirement.RetailStore);

    /// <summary>
    /// Commission reports received under one commission contract: paid by bank and cash (with advance factures),
    /// with goods returned to the commissioner paid together with a shipment, and an unpaid draft period.
    /// </summary>
    private static ScenarioDefinition CommissionReportInFull() => Full("commissionreportin-full",
        "all commissionreportin contexts on one counterparty: paid by paymentin/cashin with factureout, " +
        "returnToCommissionerPositions paid together with a demand, unpaid report",
        [
            Root("report-paid", "commissionreportin"),
            Template("report-paid-paymentin", "paymentin", Link("paymentin.operations->commissionreportin", "report-paid")),
            Template("fo-report-paid-paymentin", "factureout", Link("factureout.payments->paymentin", "report-paid-paymentin")),
            Template("report-paid-cashin", "cashin", Link("cashin.operations->commissionreportin", "report-paid")),
            Template("fo-report-paid-cashin", "factureout", Link("factureout.payments->cashin", "report-paid-cashin")),
            Root("report-with-return", "commissionreportin") with { ReturnsToCommissioner = true },
            Root("demand", "demand"),
            PaymentPost("netting-paymentin", "paymentin",
                Link("paymentin.operations->commissionreportin", "report-with-return"),
                Link("paymentin.operations->demand", "demand")),
            Root("report-unpaid", "commissionreportin")
        ]);

    /// <summary>Commission reports issued under one commission contract: paid (with received factures), paid together
    /// with a supply, and unpaid.</summary>
    private static ScenarioDefinition CommissionReportOutFull() => Full("commissionreportout-full",
        "all commissionreportout contexts on one counterparty: paid by paymentout/cashout with facturein, paid together " +
        "with a supply, unpaid report",
        [
            Root("report-paid", "commissionreportout"),
            Template("report-paid-paymentout", "paymentout", Link("paymentout.operations->commissionreportout", "report-paid")),
            Template("fi-report-paid-paymentout", "facturein", Link("facturein.payments->paymentout", "report-paid-paymentout")),
            Template("report-paid-cashout", "cashout", Link("cashout.operations->commissionreportout", "report-paid")),
            Template("fi-report-paid-cashout", "facturein", Link("facturein.payments->cashout", "report-paid-cashout")),
            Root("report-with-supply", "commissionreportout"),
            Root("supply", "supply"),
            PaymentPost("netting-paymentout", "paymentout",
                Link("paymentout.operations->commissionreportout", "report-with-supply"),
                Link("paymentout.operations->supply", "supply")),
            Root("report-unpaid", "commissionreportout")
        ]);

    private static ScenarioStep Root(string key, string type, bool services = false) =>
        new(key, type, StepKind.Root, []) { AllowServices = services };

    private static ScenarioStep Template(string key, string type, params StepLink[] links) =>
        new(key, type, StepKind.Template, links);

    private static ScenarioStep PaymentPost(string key, string type, params StepLink[] links) =>
        new(key, type, StepKind.PaymentPost, links);

    private static StepLink Link(string relationId, string targetStep) => new(relationId, targetStep);
}
