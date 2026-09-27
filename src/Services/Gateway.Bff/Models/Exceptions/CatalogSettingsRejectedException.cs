namespace MsContractor.Gateway.Bff.Models.Exceptions;

public sealed class CatalogSettingsRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = message;
}
