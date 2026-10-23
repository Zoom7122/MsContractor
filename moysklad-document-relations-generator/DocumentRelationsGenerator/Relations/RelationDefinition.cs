namespace DocumentRelationsGenerator.Relations;

/// <summary>Shape of the reference field on the source document.</summary>
public enum RelationKind
{
    /// <summary><c>"field": {"meta": ...}</c></summary>
    Reference,

    /// <summary><c>"field": [{"meta": ...}]</c></summary>
    Collection,

    /// <summary>Payment <c>"operations": [{"meta": ..., "linkedSum": ...}]</c></summary>
    Operation
}

/// <summary>Documented way to write the link (see DOCUMENT_RELATIONS.md, column Creation).</summary>
public enum CreationPath
{
    /// <summary>Base document goes into the body of <c>PUT /entity/{source}/new</c>.</summary>
    Template,

    /// <summary>Payment operation that has no template base; the operation is passed in the POST body.</summary>
    PostOperations
}

public sealed record RelationDefinition(
    string SourceType,
    string Field,
    string TargetType,
    RelationKind Kind,
    CreationPath Creation,
    string? ReverseField)
{
    public string Id => $"{SourceType}.{Field}->{TargetType}";

    public string Label => $"{SourceType} -> {TargetType} ({Field})";
}
