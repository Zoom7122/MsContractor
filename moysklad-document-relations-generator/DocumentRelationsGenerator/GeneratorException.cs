namespace DocumentRelationsGenerator;

/// <summary>Configuration, CLI or safety error that stops the tool before or instead of API work.</summary>
public sealed class GeneratorException : Exception
{
    public GeneratorException(string message) : base(message)
    {
    }
}
