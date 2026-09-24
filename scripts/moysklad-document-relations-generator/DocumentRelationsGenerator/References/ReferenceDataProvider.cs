using System.Text.Json.Nodes;
using DocumentRelationsGenerator.Configuration;
using DocumentRelationsGenerator.Documents;
using DocumentRelationsGenerator.Execution;
using DocumentRelationsGenerator.Manifest;
using DocumentRelationsGenerator.MoySklad;
using DocumentRelationsGenerator.Randomization;

namespace DocumentRelationsGenerator.References;

/// <summary>
/// Reads the account's existing system entities (organization, store, VAT rates, currencies, employee)
/// with GET only, and creates the prefixed test entities a run needs. Organizations, currencies and
/// retail points are never created.
/// </summary>
public sealed class ReferenceDataProvider
{
    private readonly IMoySkladApi api;
    private readonly GeneratorSettings settings;
    private readonly TextWriter output;

    public ReferenceDataProvider(IMoySkladApi api, GeneratorSettings settings, TextWriter output)
    {
        this.api = api;
        this.settings = settings;
        this.output = output;
    }

    public async Task<ReferenceData> LoadExistingAsync(CancellationToken cancellationToken)
    {
        var organization = settings.OrganizationId is { } organizationId
            ? await api.GetAsync($"entity/organization/{organizationId}", cancellationToken)
            : FirstActive(await api.GetAsync("entity/organization?limit=100", cancellationToken))
              ?? throw new GeneratorException("The account has no active organization; set MS_RELTEST_ORGANIZATION_ID.");
        var resolvedOrganizationId = MetaReference.Id(MetaReference.Href(organization));

        var store = settings.StoreId is { } storeId
            ? await api.GetAsync($"entity/store/{storeId}", cancellationToken)
            : FirstActive(await TryGetAsync("entity/store?limit=100", cancellationToken));

        var currencies = Rows(await TryGetAsync("entity/currency?limit=100", cancellationToken));
        var vatRates = Rows(await TryGetAsync("entity/taxrate?limit=100", cancellationToken))
            .Where(IsActive)
            .Select(rate => rate["rate"] is JsonValue ? JsonNumbers.Read(rate["rate"]) : -1)
            .Where(rate => rate >= 0 && rate <= 100 && rate == Math.Floor(rate))
            .Select(rate => (int)rate)
            .Distinct()
            .Order()
            .ToList();

        var (retailStore, retailProblem) = await LoadRetailStoreAsync(organization, cancellationToken);
        return new ReferenceData
        {
            Organization = organization,
            OrganizationAccounts = Rows(await TryGetAsync($"entity/organization/{resolvedOrganizationId}/accounts", cancellationToken)),
            Store = store,
            Employee = await TryGetAsync("context/employee", cancellationToken),
            ExpenseItem = PickExpenseItem(Rows(await TryGetAsync("entity/expenseitem?limit=100", cancellationToken))),
            VatRates = vatRates,
            ForeignCurrency = currencies.FirstOrDefault(currency => IsActive(currency) && !IsTrue(currency["default"])),
            RetailStore = retailStore,
            RetailProblem = retailProblem
        };
    }

    public async Task PrepareTestEntitiesAsync(ReferenceData refs, RunIdentity run, int counterpartyCount,
        bool needsExpenseItem, bool needsCommissionContract, ManifestStore manifest, CancellationToken cancellationToken)
    {
        var random = TestDataRandomizer.For(run.Seed, "reference-data");
        RecordReused(manifest, "organization", refs.Organization);
        if (refs.RetailStore is not null) RecordReused(manifest, "retailStore", refs.RetailStore);

        if (refs.Store is null)
            refs.Store = await CreateAsync(manifest, "store", "store", new JsonObject
            {
                ["name"] = run.EntityName("STORE", 1),
                ["externalCode"] = run.EntityExternalCode("store", 1),
                ["description"] = "Склад для тестовых документов MSContractor relations generator"
            }, null, cancellationToken);
        else RecordReused(manifest, "store", refs.Store);

        if (needsExpenseItem && refs.ExpenseItem is null)
            refs.ExpenseItem = await CreateAsync(manifest, "expenseItem", "expenseitem", new JsonObject
            {
                ["name"] = run.EntityName("EXPENSE", 1),
                ["description"] = "Статья расходов для тестовых платежей MSContractor"
            }, null, cancellationToken);
        else if (refs.ExpenseItem is not null) RecordReused(manifest, "expenseItem", refs.ExpenseItem);

        refs.Project = await CreateAsync(manifest, "project", "project", new JsonObject
        {
            ["name"] = run.EntityName("PROJECT", 1),
            ["code"] = $"RT-{run.RunId}-PRJ",
            ["externalCode"] = run.EntityExternalCode("project", 1),
            ["description"] = "Проект тестовых документов MSContractor relations generator"
        }, null, cancellationToken);

        for (var i = 1; i <= ReferenceData.ProductCount; i++)
            refs.Products.Add(await CreateAsync(manifest, "product", "product", new JsonObject
            {
                ["name"] = run.EntityName("PRODUCT", i),
                ["code"] = $"RT-{run.RunId}-P{i:00}",
                ["article"] = $"ART-{random.NextDigits(6)}",
                ["externalCode"] = run.EntityExternalCode("product", i),
                ["description"] = "Тестовый товар MSContractor relations generator"
            }, null, cancellationToken));

        for (var i = 1; i <= ReferenceData.ServiceCount; i++)
            refs.Services.Add(await CreateAsync(manifest, "service", "service", new JsonObject
            {
                ["name"] = run.EntityName("SERVICE", i),
                ["code"] = $"RT-{run.RunId}-S{i:00}",
                ["externalCode"] = run.EntityExternalCode("service", i),
                ["description"] = "Тестовая услуга MSContractor relations generator"
            }, null, cancellationToken));

        for (var i = 1; i <= counterpartyCount; i++)
            refs.Counterparties.Add(await CreateCounterpartyAsync(refs, run, i, random, needsCommissionContract,
                manifest, cancellationToken));
    }

    private async Task<CounterpartyData> CreateCounterpartyAsync(ReferenceData refs, RunIdentity run, int index,
        TestDataRandomizer random, bool needsCommissionContract, ManifestStore manifest, CancellationToken cancellationToken)
    {
        var accounts = new JsonArray();
        for (var j = 0; j < ReferenceData.AccountsPerCounterparty; j++)
            accounts.Add(new JsonObject
            {
                ["accountNumber"] = "40702810" + random.NextDigits(12),
                ["bankName"] = "MSCONTRACTOR RELTEST BANK",
                ["bic"] = "04" + random.NextDigits(7),
                ["correspondentAccount"] = "30101810" + random.NextDigits(12),
                ["isDefault"] = j == 0
            });

        var payload = new JsonObject
        {
            ["name"] = run.EntityName("CP", index),
            ["code"] = $"RT-{run.RunId}-CP{index:00}",
            ["externalCode"] = run.EntityExternalCode("cp", index),
            ["description"] = "Тестовый контрагент MSContractor relations generator",
            ["email"] = $"reltest-{run.RunId}-{index:000}@example.com",
            ["phone"] = "+7999" + random.NextDigits(7),
            ["companyType"] = "legal",
            ["accounts"] = accounts,
            ["syncId"] = run.SyncId("counterparty", index).ToString("D")
        };
        var entity = await api.CreateAsync("counterparty", payload, cancellationToken);
        manifest.Manifest.Counterparties.Add(ToManifestEntity("counterparty", entity, created: true, manifest, index));
        manifest.Save();
        output.WriteLine($"  created counterparty {entity["name"]} ({MetaReference.Id(MetaReference.Href(entity))})");

        var counterparty = new CounterpartyData(index, entity);
        var id = MetaReference.Id(MetaReference.Href(entity));
        counterparty.Accounts.AddRange(Rows(await api.GetAsync($"entity/counterparty/{id}/accounts", cancellationToken)));

        counterparty.SalesContract = await CreateContractAsync(refs, run, entity, index, "Sales", manifest, cancellationToken);
        if (needsCommissionContract)
            counterparty.CommissionContract =
                await CreateContractAsync(refs, run, entity, index, "Commission", manifest, cancellationToken);
        return counterparty;
    }

    private Task<JsonObject> CreateContractAsync(ReferenceData refs, RunIdentity run, JsonObject counterparty, int index,
        string contractType, ManifestStore manifest, CancellationToken cancellationToken)
    {
        var kind = $"CONTRACT-{contractType.ToUpperInvariant()}";
        // Contract belongs to the same counterparty (agent) and organization (ownAgent), dated before all documents.
        var payload = new JsonObject
        {
            ["name"] = run.EntityName(kind, index),
            ["externalCode"] = run.EntityExternalCode(kind, index),
            ["description"] = $"Тестовый договор ({contractType}) MSContractor relations generator",
            ["moment"] = MoySkladTime.Format(run.AnchorDate.ToDateTime(new TimeOnly(9, 0)).AddDays(-400)),
            ["contractType"] = contractType,
            ["ownAgent"] = MetaReference.To(refs.Organization),
            ["agent"] = MetaReference.To(counterparty)
        };
        return CreateAsync(manifest, $"contract{contractType}", "contract", payload, index, cancellationToken);
    }

    private async Task<JsonObject> CreateAsync(ManifestStore manifest, string role, string type, JsonObject payload,
        int? counterpartyIndex, CancellationToken cancellationToken)
    {
        var entity = await api.CreateAsync(type, payload, cancellationToken);
        manifest.Manifest.Entities.Add(ToManifestEntity(role, entity, created: true, manifest, counterpartyIndex));
        manifest.Save();
        output.WriteLine($"  created {role} {entity["name"]}");
        return entity;
    }

    private static void RecordReused(ManifestStore manifest, string role, JsonObject entity)
    {
        manifest.Manifest.Entities.Add(ToManifestEntity(role, entity, created: false, manifest, null));
        manifest.Save();
    }

    private static ManifestEntity ToManifestEntity(string role, JsonObject entity, bool created, ManifestStore manifest,
        int? counterpartyIndex)
    {
        var href = MetaReference.Href(entity) ?? throw new GeneratorException($"API returned {role} without meta.href.");
        return new ManifestEntity
        {
            Role = role,
            Type = MetaReference.Type(entity) ?? role,
            Id = MetaReference.Id(href)!,
            Name = entity["name"]?.GetValue<string>(),
            Href = href,
            Created = created,
            ExternalCode = entity["externalCode"]?.GetValue<string>(),
            CounterpartyIndex = counterpartyIndex,
            Sequence = created ? manifest.NextSequence() : 0
        };
    }

    private async Task<(JsonObject? Store, string? Problem)> LoadRetailStoreAsync(JsonObject organization,
        CancellationToken cancellationToken)
    {
        if (settings.RetailStoreId is null)
            return (null, "MS_RELTEST_RETAIL_STORE_ID is not set (retail shifts are opened only on an explicitly chosen retail store)");
        var retailStore = await api.GetAsync($"entity/retailstore/{settings.RetailStoreId}", cancellationToken);
        if (IsTrue(retailStore["archived"]) || retailStore["active"] is JsonValue active && !active.GetValue<bool>())
            return (null, "the configured retail store is archived or inactive");
        if (!MetaReference.SameEntity(MetaReference.Href(retailStore["organization"]), MetaReference.Href(organization)))
            return (null, "the configured retail store belongs to another organization; set MS_RELTEST_ORGANIZATION_ID to it");
        return (retailStore, null);
    }

    private async Task<JsonObject?> TryGetAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await api.GetAsync(path, cancellationToken);
        }
        catch (MoySkladApiException ex) when (!ex.IsAuthenticationFailure || ex.StatusCode == 403)
        {
            // Missing permission or tariff for an optional dictionary: the feature is disabled, not guessed.
            output.WriteLine($"  note: {ex.Method} {ex.Path} unavailable (HTTP {ex.StatusCode?.ToString() ?? "-"}); related values are not used");
            return null;
        }
    }

    private static List<JsonObject> Rows(JsonObject? list) =>
        list?["rows"] is JsonArray rows ? rows.OfType<JsonObject>().ToList() : [];

    private static JsonObject? FirstActive(JsonObject? list) => Rows(list).FirstOrDefault(IsActive);

    /// <summary>
    /// Fallback only: payment templates normally carry their own expense item. "Перемещение" is the item for
    /// transfers between the company's own accounts, so it is used only when nothing else exists.
    /// </summary>
    private static JsonObject? PickExpenseItem(IReadOnlyList<JsonObject> items)
    {
        var active = items.Where(IsActive).ToList();
        return active.FirstOrDefault(item => item["name"]?.GetValue<string>() is not "Перемещение") ?? active.FirstOrDefault();
    }

    private static bool IsActive(JsonObject row) => !IsTrue(row["archived"]);

    private static bool IsTrue(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
