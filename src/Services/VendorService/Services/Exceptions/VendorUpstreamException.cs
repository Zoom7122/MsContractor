namespace MsContractor.VendorService.Services.Exceptions;

public sealed class VendorUpstreamException : Exception
{
    public VendorUpstreamException(string message)
        : base(message)
    {
    }

    public VendorUpstreamException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
