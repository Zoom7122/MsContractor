using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Options;
using MsContractor.MoySkladEgressService.Services.Documents.Purchasereturn;

namespace MsContractor.Sync.Tests;

public sealed class PurchaseReturnCreateMapperTests
{
    [Fact]
    public void BuildPayload_UsesAllowlistReplacesAgentAndCleansReferences()
    {
        var documentId = Guid.NewGuid();
        var mainCounterpartyId = Guid.NewGuid();
        var supplyId = Guid.NewGuid();
        var assortmentId = Guid.NewGuid();
        var positionId = Guid.NewGuid();
        var mapper = new PurchaseReturnCreateMapper(Options.Create(new EgressOptions
        {
            JsonApiBaseUrl = new Uri("https://api.example.test/api/remap/1.2/")
        }));

        var payload = JsonNode.Parse(mapper.BuildPayload(
            $$"""
            {
              "id": "{{documentId:D}}", "accountId": "{{Guid.NewGuid():D}}", "created": "2026-01-01",
              "updated": "2026-01-02", "deleted": false, "sum": 10, "vatSum": 1, "payedSum": 0,
              "printed": true, "published": true,
              "organization": { "meta": { "href": "https://old/entity/organization/{{Guid.NewGuid():D}}", "type": "organization", "mediaType": "application/json", "extra": "remove" } },
              "store": { "meta": { "href": "https://old/entity/store/{{Guid.NewGuid():D}}", "type": "store" } },
              "supply": { "meta": { "href": "https://old/entity/supply/{{supplyId:D}}", "type": "supply" } },
              "agent": { "meta": { "href": "https://old/entity/counterparty/{{Guid.NewGuid():D}}", "type": "counterparty" } },
              "moment": "2026-01-03 10:00:00", "applicable": true, "name": "pr-1", "code": "c-1",
              "externalCode": "ext", "description": "description", "vatEnabled": true, "vatIncluded": false,
              "rate": { "value": 100, "currency": { "meta": { "href": "https://old/entity/currency/{{Guid.NewGuid():D}}", "type": "currency", "extra": "remove" } } },
              "attributes": [{ "meta": { "href": "https://old/entity/metadata/attribute/{{Guid.NewGuid():D}}", "type": "attributemetadata", "extra": "remove" }, "value": "value", "name": "remove" }],
              "positions": [{ "meta": { "href": "https://old/entity/purchasereturn/{{documentId:D}}/positions/{{positionId:D}}", "type": "purchasereturnposition" }, "id": "{{positionId:D}}", "accountId": "{{Guid.NewGuid():D}}" }]
            }
            """,
            new Dictionary<Guid, string>
            {
                [positionId] = $$"""
                {
                  "id": "{{positionId:D}}", "accountId": "{{Guid.NewGuid():D}}",
                  "meta": { "href": "https://old/position/{{positionId:D}}", "type": "purchasereturnposition" },
                  "assortment": { "meta": { "href": "https://old/entity/assortment/{{assortmentId:D}}", "type": "product", "extra": "remove" } },
                  "quantity": 2, "price": 100, "discount": 0, "vat": 20, "vatEnabled": true,
                  "pack": { "meta": { "href": "https://old/entity/uom/{{Guid.NewGuid():D}}", "type": "uom" } },
                  "slot": { "meta": { "href": "https://old/entity/slot/{{Guid.NewGuid():D}}", "type": "storage" } },
                  "things": [], "name": "must not be copied"
                }
                """
            },
            mainCounterpartyId));

        Assert.NotNull(payload);
        Assert.Equal($"https://api.example.test/api/remap/1.2/entity/counterparty/{mainCounterpartyId:D}",
            payload!["agent"]!["meta"]!["href"]!.GetValue<string>());
        Assert.Equal($"https://old/entity/supply/{supplyId:D}", payload["supply"]!["meta"]!["href"]!.GetValue<string>());
        Assert.Equal("application/json", payload["organization"]!["meta"]!["mediaType"]!.GetValue<string>());
        Assert.Null(payload["id"]);
        Assert.Null(payload["accountId"]);
        Assert.Null(payload["created"]);
        Assert.Null(payload["sum"]);
        Assert.Null(payload["printed"]);

        var position = payload["positions"]![0]!.AsObject();
        Assert.Equal($"https://old/entity/assortment/{assortmentId:D}", position["assortment"]!["meta"]!["href"]!.GetValue<string>());
        Assert.Null(position["id"]);
        Assert.Null(position["accountId"]);
        Assert.Null(position["meta"]);
        Assert.Null(position["name"]);
        Assert.Equal(2, position["quantity"]!.GetValue<int>());
        Assert.NotNull(payload["attributes"]![0]!["meta"]);
        Assert.Equal("value", payload["attributes"]![0]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void BuildPayload_RequiresOrganizationStoreSupplyAndPositions()
    {
        var mapper = new PurchaseReturnCreateMapper(Options.Create(new EgressOptions()));
        var exception = Assert.Throws<InvalidOperationException>(() => mapper.BuildPayload(
            "{\"organization\":{},\"store\":{},\"supply\":{}}",
            new Dictionary<Guid, string>(),
            Guid.NewGuid()));

        Assert.NotEmpty(exception.Message);
    }

    [Fact]
    public void BuildPayload_UsesSelectedContractAndAccountAndAddsSyncId()
    {
        var mainCounterpartyId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var syncId = Guid.NewGuid();
        var mapper = new PurchaseReturnCreateMapper(Options.Create(new EgressOptions
        {
            JsonApiBaseUrl = new Uri("https://api.example.test/api/remap/1.2/")
        }));

        var payload = JsonNode.Parse(mapper.BuildPayload(
            $$"""
            {
              "organization": { "meta": { "href": "https://old/entity/organization/{{Guid.NewGuid():D}}", "type": "organization" } },
              "store": { "meta": { "href": "https://old/entity/store/{{Guid.NewGuid():D}}", "type": "store" } },
              "supply": { "meta": { "href": "https://old/entity/supply/{{Guid.NewGuid():D}}", "type": "supply" } },
              "contract": { "meta": { "href": "https://old/entity/contract/{{Guid.NewGuid():D}}", "type": "contract" } },
              "agentAccount": { "meta": { "href": "https://old/entity/counterparty/{{Guid.NewGuid():D}}/accounts/{{Guid.NewGuid():D}}", "type": "account" } },
              "factureIn": { "id": "must-not-copy" }, "factureOut": { "id": "must-not-copy" },
              "payments": [], "files": [], "sum": 1
            }
            """,
            new Dictionary<Guid, string>
            {
                [Guid.NewGuid()] = "{\"assortment\":{\"meta\":{\"href\":\"https://old/entity/product/00000000-0000-0000-0000-000000000001\",\"type\":\"product\"}}}"
            },
            mainCounterpartyId,
            syncId,
            new PurchaseReturnRecreationReferences(contractId, accountId)));

        Assert.Equal(syncId.ToString("D"), payload!["syncId"]!.GetValue<string>());
        Assert.EndsWith($"/entity/contract/{contractId:D}", payload["contract"]!["meta"]!["href"]!.GetValue<string>());
        Assert.EndsWith($"/accounts/{accountId:D}", payload["agentAccount"]!["meta"]!["href"]!.GetValue<string>());
        Assert.Null(payload["factureIn"]);
        Assert.Null(payload["factureOut"]);
        Assert.Null(payload["payments"]);
        Assert.Null(payload["files"]);
    }
}
