using System.Text.Json.Nodes;
using DocumentRelationsGenerator.Documents;
using DocumentRelationsGenerator.Execution;
using DocumentRelationsGenerator.MoySklad;
using DocumentRelationsGenerator.References;
using DocumentRelationsGenerator.Relations;
using DocumentRelationsGenerator.Scenarios;

namespace DocumentRelationsGenerator.Tests;

public sealed class PayloadBuilderTests
{
    private const string Api = "https://api.moysklad.ru/api/remap/1.2/entity/";
    private static readonly RunIdentity Run = new("20260924-221530-a81f", 7348291, new DateOnly(2026, 9, 24));

    private static JsonObject Entity(string type, string id, JsonObject? extra = null)
    {
        var entity = MetaReference.Create($"{Api}{type}/{id}", type);
        entity["name"] = $"{type}-{id}";
        foreach (var (key, value) in extra ?? []) entity[key] = value?.DeepClone();
        return entity;
    }

    private static (ReferenceData Refs, CounterpartyData Counterparty) References(bool vat = true)
    {
        var refs = new ReferenceData
        {
            Organization = Entity("organization", "org"),
            OrganizationAccounts = [Entity("account", "oa1")],
            Store = Entity("store", "store"),
            Employee = Entity("employee", "emp", new JsonObject { ["group"] = MetaReference.Create($"{Api}group/g", "group") }),
            ExpenseItem = Entity("expenseitem", "exp"),
            Project = Entity("project", "prj"),
            VatRates = vat ? [0, 10, 20] : [],
            RetailStore = Entity("retailstore", "rs")
        };
        for (var i = 0; i < ReferenceData.ProductCount; i++) refs.Products.Add(Entity("product", $"p{i}"));
        for (var i = 0; i < ReferenceData.ServiceCount; i++) refs.Services.Add(Entity("service", $"s{i}"));
        var counterparty = new CounterpartyData(1, Entity("counterparty", "cp"))
        {
            SalesContract = Entity("contract", "sales"),
            CommissionContract = Entity("contract", "commission")
        };
        counterparty.Accounts.Add(Entity("account", "a1", new JsonObject { ["isDefault"] = true }));
        counterparty.Accounts.Add(Entity("account", "a2", new JsonObject { ["isDefault"] = false }));
        refs.Counterparties.Add(counterparty);
        return (refs, counterparty);
    }

    private static ScenarioPlan Plan(string scenario, int counterparty = 1) =>
        ScenarioPlanner.Plan(ScenarioRegistry.Select([scenario]).Single(), counterparty, Run);

    [Fact]
    public void RootPayloads_UseOnlyFieldsTheirDocumentTypeSupports()
    {
        var (refs, counterparty) = References();
        var factory = new DocumentFactory(refs, Run);
        // Many seeds, so every random branch (VAT on/off, with/without contract, ...) is exercised for every type.
        var roots = Enumerable.Range(1, 40).Select(seed => Run with { Seed = seed })
            .SelectMany(run => ScenarioRegistry.All.SelectMany(scenario => new[] { 1, 2 }.Select(index => ScenarioPlanner.Plan(scenario, index, run))))
            .SelectMany(plan => plan.Steps.Where(step => step.Step.Kind == StepKind.Root).Select(step => (plan, step)));

        foreach (var (plan, step) in roots)
        {
            var payload = factory.BuildRoot(plan, step, counterparty);
            var type = step.Step.DocumentType;
            Assert.All(payload.Select(pair => pair.Key), key => Assert.True(DocumentFieldSupport.Supports(type, key), $"{type}.{key}"));
            Assert.Equal(MetaReference.Href(counterparty.Entity), MetaReference.Href(payload["agent"]));
            if (DocumentFieldSupport.Supports(type, "positions")) Assert.InRange(((JsonArray)payload["positions"]!).Count, 1, 5);
            else Assert.True(JsonNumbers.Read(payload["sum"]) > 0, $"{type} without positions needs a sum");
            Assert.Contains(Run.RunId, payload["externalCode"]!.GetValue<string>());
            if (type.StartsWith("commissionreport", StringComparison.Ordinal))
                Assert.Equal(MetaReference.Href(counterparty.CommissionContract), MetaReference.Href(payload["contract"]));
            else
                Assert.Equal(step.WithContract, payload["contract"] is not null);
            if (DocumentFieldSupport.Supports(type, "agentAccount"))
            {
                // Without an explicit account MoySklad sets the default one itself; the explicit variant is never the default.
                Assert.Equal(step.WithAgentAccount, payload["agentAccount"] is not null);
                if (step.WithAgentAccount) Assert.EndsWith("/a2", MetaReference.Href(payload["agentAccount"]));
            }
        }
    }

    [Fact]
    public void RootPositions_UseKopecksAndAccountVatRates()
    {
        var (refs, counterparty) = References();
        var plan = Plan("sales-return-flow");
        var step = plan.Step("demand");
        var payload = new DocumentFactory(refs, Run).BuildRoot(plan, step, counterparty);
        var positions = ((JsonArray)payload["positions"]!).OfType<JsonObject>().ToList();

        for (var i = 0; i < positions.Count; i++)
        {
            Assert.Equal(step.Positions[i].PriceKopecks, JsonNumbers.Read(positions[i]["price"]));
            var vat = (int)JsonNumbers.Read(positions[i]["vat"]);
            if (payload["vatEnabled"]!.GetValue<bool>()) Assert.Contains(vat, refs.VatRates);
            else Assert.Equal(0, vat);
        }
    }

    [Fact]
    public void WithoutAccountVatRates_DocumentsAreWithoutVat()
    {
        var (refs, counterparty) = References(vat: false);
        var plan = Plan("sales-flow");
        var payload = new DocumentFactory(refs, Run).BuildRoot(plan, plan.Step("customerorder"), counterparty);
        Assert.False(payload["vatEnabled"]!.GetValue<bool>());
        Assert.All(((JsonArray)payload["positions"]!).OfType<JsonObject>(), position => Assert.False(position["vatEnabled"]!.GetValue<bool>()));
    }

    [Fact]
    public void CleanTemplate_RemovesTemplateNoiseAndFlattensPositions()
    {
        var template = new JsonObject
        {
            ["meta"] = new JsonObject { ["type"] = "salesreturn" },
            ["accountId"] = "x", ["id"] = "y", ["syncId"] = "z", ["contract"] = null, ["name"] = "tpl",
            ["positions"] = new JsonObject
            {
                ["meta"] = new JsonObject(),
                ["rows"] = new JsonArray(new JsonObject { ["id"] = "p", ["meta"] = new JsonObject(), ["quantity"] = 2, ["price"] = 100 })
            }
        };

        var clean = DocumentFactory.CleanTemplate(template);

        Assert.Equal(["name", "positions"], clean.Select(pair => pair.Key).Order());
        var position = Assert.Single((JsonArray)clean["positions"]!)!.AsObject();
        Assert.Equal(["price", "quantity"], position.Select(pair => pair.Key).Order());
        Assert.NotNull(template["meta"]); // input is not modified
    }

    [Fact]
    public void ReturnFromTemplate_KeepsPricesAndNeverExceedsBaseQuantities()
    {
        var (refs, counterparty) = References();
        var plan = Plan("sales-return-flow");
        var step = plan.Step("salesreturn");
        var demand = Entity("demand", "d1", new JsonObject { ["sum"] = 100000 });
        var basePositions = new JsonArray(
            Position("p0", 3, 24990), Position("p1", 1.5m, 845000), Position("p2", 10, 129900));
        var template = new JsonObject
        {
            ["agent"] = MetaReference.To(counterparty.Entity), ["organization"] = MetaReference.To(refs.Organization),
            ["store"] = MetaReference.To(refs.Store!), ["demand"] = MetaReference.To(demand),
            ["positions"] = new JsonObject { ["rows"] = basePositions.DeepClone() }
        };

        var payload = new DocumentFactory(refs, Run).BuildFromTemplate(plan, step, template, counterparty,
            new Dictionary<string, JsonObject> { ["demand"] = demand });

        Assert.Equal(MetaReference.Href(demand), MetaReference.Href(payload["demand"]));
        var returned = ((JsonArray)payload["positions"]!).OfType<JsonObject>().ToList();
        Assert.NotEmpty(returned);
        foreach (var position in returned)
        {
            var original = basePositions.OfType<JsonObject>().Single(item =>
                MetaReference.SameEntity(MetaReference.Href(item["assortment"]), MetaReference.Href(position["assortment"])));
            Assert.Equal(JsonNumbers.Read(original["price"]), JsonNumbers.Read(position["price"]));
            Assert.InRange(JsonNumbers.Read(position["quantity"]), 0.0001m, JsonNumbers.Read(original["quantity"]));
        }

        JsonObject Position(string product, decimal quantity, long price) => new()
        {
            ["assortment"] = MetaReference.Create($"{Api}product/{product}", "product"), ["quantity"] = quantity, ["price"] = price
        };
    }

    [Fact]
    public void FactureinOnCashout_IsLinkedFromTheFactureSide()
    {
        // Live GET: cashout.factureIn sent in POST/PUT of the cashout is dropped, while
        // facturein.payments = [cashout] is stored and shows up as cashout.factureIn.
        var cashout = Entity("cashout", "c1");
        var step = ScenarioRegistry.Select(["purchase-flow"]).Single().Steps.Single(item => item.Key == "facturein-from-cashout");

        var body = DocumentFactory.BuildTemplateRequest(step, new Dictionary<string, JsonObject> { ["cashout"] = cashout });

        Assert.Equal(MetaReference.Href(cashout), MetaReference.Href(((JsonArray)body["payments"]!)[0]));
        Assert.DoesNotContain(RelationCatalog.All, relation => relation.Id is "cashout.factureIn->facturein"
            or "salesreturn.factureOut->factureout" or "purchasereturn.factureIn->facturein");
    }

    [Fact]
    public void FactureinFromTemplate_GetsIncomingNumberAndDate()
    {
        var (refs, counterparty) = References();
        var plan = Plan("purchase-flow");
        var step = plan.Step("facturein-from-supply");
        var supply = Entity("supply", "s1");

        var payload = new DocumentFactory(refs, Run).BuildFromTemplate(plan, step, new JsonObject(), counterparty,
            new Dictionary<string, JsonObject> { ["supply"] = supply });

        Assert.Equal(step.IncomingNumber, payload["incomingNumber"]!.GetValue<string>());
        Assert.Equal(MoySkladTime.Format(step.IncomingDate), payload["incomingDate"]!.GetValue<string>());
        Assert.Equal(MetaReference.Href(supply), MetaReference.Href(((JsonArray)payload["supplies"]!)[0]));
        Assert.Null(payload["store"]); // factures have no store
    }

    [Fact]
    public void PaymentFromTemplate_LinksTheSameSumAndAddsExpenseItemForOutgoingPayments()
    {
        var (refs, counterparty) = References();
        var plan = Plan("sales-return-flow");
        var step = plan.Step("cashout");
        var salesReturn = Entity("salesreturn", "r1");
        var template = new JsonObject
        {
            ["sum"] = 1_000_000,
            ["operations"] = new JsonArray(new JsonObject { ["meta"] = MetaReference.To(salesReturn)["meta"]!.DeepClone(), ["linkedSum"] = 1_000_000 })
        };

        var payload = new DocumentFactory(refs, Run).BuildFromTemplate(plan, step, template, counterparty,
            new Dictionary<string, JsonObject> { ["salesreturn"] = salesReturn });

        var sum = JsonNumbers.Read(payload["sum"]);
        Assert.Equal(Math.Round(1_000_000 * step.PaymentShare), sum);
        Assert.Equal(sum, JsonNumbers.Read(((JsonArray)payload["operations"]!)[0]!["linkedSum"]));
        Assert.Equal(MetaReference.Href(refs.ExpenseItem), MetaReference.Href(payload["expenseItem"]));
        Assert.Null(payload["agentAccount"]); // cashout has no agentAccount
    }

    [Fact]
    public void PartialPayment_TakesVatShareFromTheBaseNotFromTheTemplate()
    {
        // Live API: the cashout template on a partly paid supply had vatSum 322341 > sum 293300 (error 3007).
        var (refs, counterparty) = References();
        var plan = Plan("purchase-flow");
        var supply = Entity("supply", "s1", new JsonObject { ["sum"] = 1_000_000, ["vatSum"] = 180_000 });
        var template = new JsonObject
        {
            ["sum"] = 100_000,
            ["vatSum"] = 180_000,
            ["operations"] = new JsonArray(new JsonObject { ["meta"] = MetaReference.To(supply)["meta"]!.DeepClone(), ["linkedSum"] = 100_000 })
        };

        var payload = new DocumentFactory(refs, Run).BuildFromTemplate(plan, plan.Step("cashout"), template, counterparty,
            new Dictionary<string, JsonObject> { ["supply"] = supply, ["facturein-from-supply"] = Entity("facturein", "f1") });

        var sum = JsonNumbers.Read(payload["sum"]);
        Assert.Equal(Math.Round(sum * 0.18m), JsonNumbers.Read(payload["vatSum"]));
        Assert.True(JsonNumbers.Read(payload["vatSum"]) <= sum);
    }

    [Fact]
    public void PaymentPost_SplitsTheSumAcrossOperations()
    {
        var (refs, counterparty) = References();
        var plan = Plan("payment-multi-operations");
        var created = new Dictionary<string, JsonObject>
        {
            ["customerorder"] = Entity("customerorder", "o1", new JsonObject { ["sum"] = 500_000 }),
            ["invoiceout"] = Entity("invoiceout", "i1", new JsonObject { ["sum"] = 300_000 })
        };

        var payload = new DocumentFactory(refs, Run).BuildPaymentPost(plan, plan.Step("paymentin"), counterparty, created);

        var operations = ((JsonArray)payload["operations"]!).OfType<JsonObject>().ToList();
        Assert.Equal(2, operations.Count);
        Assert.Equal(operations.Sum(operation => JsonNumbers.Read(operation["linkedSum"])), JsonNumbers.Read(payload["sum"]));
        Assert.All(payload.Select(pair => pair.Key), key =>
            Assert.True(DocumentFieldSupport.Supports("paymentin", key) || key == "operations", key));
    }

    [Fact]
    public void Set_RejectsFieldsTheDocumentTypeDoesNotHave()
    {
        Assert.Throws<InvalidOperationException>(() => DocumentFactory.Set(new JsonObject(), "cashin", "agentAccount", "x"));
        Assert.Throws<InvalidOperationException>(() => DocumentFactory.Set(new JsonObject(), "loss", "agent", "x"));
        Assert.Throws<InvalidOperationException>(() => DocumentFactory.Set(new JsonObject(), "commissionreportout", "shared", true));
    }

    [Fact]
    public void TemplateRequest_WrapsBaseDocumentsInRelationFields()
    {
        var created = new Dictionary<string, JsonObject>
        {
            ["retailshift"] = Entity("retailshift", "sh"),
            ["customerorder"] = Entity("customerorder", "o1")
        };
        var step = ScenarioRegistry.Select(["retail-flow"]).Single().Steps.Single(item => item.Key == "retaildemand");

        var body = DocumentFactory.BuildTemplateRequest(step, created);

        Assert.Equal(MetaReference.Href(created["retailshift"]), MetaReference.Href(body["retailShift"]));
        Assert.Equal(MetaReference.Href(created["customerorder"]), MetaReference.Href(body["customerOrder"]));
    }
}
