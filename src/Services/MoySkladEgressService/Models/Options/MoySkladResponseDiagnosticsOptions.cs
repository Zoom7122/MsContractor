namespace MsContractor.MoySkladEgressService.Models.Options;

public sealed record MoySkladResponseDiagnosticsOptions(int MaximumDiagnosticBodyLength)
{
    public const int DefaultMaximumDiagnosticBodyLength = 32 * 1024;

    public static MoySkladResponseDiagnosticsOptions Default { get; } =
        new(DefaultMaximumDiagnosticBodyLength);

    public static MoySkladResponseDiagnosticsOptions Parse(string? maximumDiagnosticBodyLengthValue) =>
        int.TryParse(maximumDiagnosticBodyLengthValue, out var parsed) && parsed > 0
            ? new MoySkladResponseDiagnosticsOptions(parsed)
            : Default;
}
