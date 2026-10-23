using System.Text.Json.Nodes;
using DocumentRelationsGenerator.Execution;
using DocumentRelationsGenerator.MoySklad;
using DocumentRelationsGenerator.Relations;

namespace DocumentRelationsGenerator.Tests;

public sealed class RelationVerifierTests
{
    private const string Api = "https://api.moysklad.ru/api/remap/1.2/entity/";

    [Fact]
    public void Reference_IsMatchedByTypeAndId()
    {
        var relation = RelationCatalog.Get("salesreturn.demand->demand");
        var source = new JsonObject { ["demand"] = MetaReference.Create($"{Api}demand/d1?expand=positions", "demand") };
        Assert.True(RelationVerifier.SourceHasTarget(source, relation, $"{Api}demand/d1"));
        Assert.False(RelationVerifier.SourceHasTarget(source, relation, $"{Api}demand/d2"));
        Assert.False(RelationVerifier.SourceHasTarget(new JsonObject(), relation, $"{Api}demand/d1"));
    }

    [Fact]
    public void Collection_AcceptsPlainArraysAndMetaArrays()
    {
        var relation = RelationCatalog.Get("factureout.demands->demand");
        var plain = new JsonObject { ["demands"] = new JsonArray(MetaReference.Create($"{Api}demand/d1", "demand")) };
        var metaArray = new JsonObject { ["demands"] = new JsonObject { ["rows"] = new JsonArray(MetaReference.Create($"{Api}demand/d1", "demand")) } };
        Assert.True(RelationVerifier.SourceHasTarget(plain, relation, $"{Api}demand/d1"));
        Assert.True(RelationVerifier.SourceHasTarget(metaArray, relation, $"{Api}demand/d1"));
    }

    [Fact]
    public void Operation_IsCheckedInTheOperationsCollection()
    {
        var relation = RelationCatalog.Get("paymentin.operations->demand");
        var operation = MetaReference.Create($"{Api}demand/d1", "demand");
        operation["linkedSum"] = 1000;
        var payment = new JsonObject { ["operations"] = new JsonArray(operation) };
        Assert.True(RelationVerifier.SourceHasTarget(payment, relation, $"{Api}demand/d1"));
        Assert.False(RelationVerifier.SourceHasTarget(payment, RelationCatalog.Get("paymentin.operations->customerorder"),
            $"{Api}customerorder/d1"));
    }

    [Fact]
    public void Reverse_IsNullWhenTheDocumentationDescribesNoReverseField()
    {
        Assert.Null(RelationVerifier.ReverseHasSource(new JsonObject(), RelationCatalog.Get("retaildemand.retailShift->retailshift"), "x"));
        var demand = new JsonObject { ["returns"] = new JsonArray(MetaReference.Create($"{Api}salesreturn/r1", "salesreturn")) };
        Assert.True(RelationVerifier.ReverseHasSource(demand, RelationCatalog.Get("salesreturn.demand->demand"), $"{Api}salesreturn/r1"));
    }
}
