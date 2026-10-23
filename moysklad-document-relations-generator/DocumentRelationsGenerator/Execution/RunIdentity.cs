using System.Security.Cryptography;
using System.Text;

namespace DocumentRelationsGenerator.Execution;

/// <summary>
/// Run ID (unique per launch) plus the reproducibility inputs. Names, externalCode and syncId depend on
/// the run ID, never on the seed alone: a rerun with the same seed creates new objects instead of
/// resolving to the previous run's syncIds.
/// </summary>
public sealed record RunIdentity(string RunId, int Seed, DateOnly AnchorDate)
{
    public const string Prefix = "MSCONTRACTOR-RELTEST";

    public static string NewRunId(DateTimeOffset now) =>
        $"{now:yyyyMMdd-HHmmss}-{RandomNumberGenerator.GetHexString(4, lowercase: true)}";

    public string EntityName(string kind, int index) => $"{Prefix}-{kind}-{RunId}-{index:000}";

    public string EntityExternalCode(string kind, int index) => $"mscontractor-reltest-{RunId}-{kind.ToLowerInvariant()}-{index:000}";

    public string DocumentExternalCode(int counterparty, string scenario, string step) =>
        $"mscontractor-reltest-{RunId}-c{counterparty}-{scenario}-{step}";

    public string DocumentName(int counterparty, int scenarioOrdinal, int stepIndex) =>
        $"RT-{RunId}-C{counterparty}-{scenarioOrdinal + 1:00}-{stepIndex + 1:00}";

    /// <summary>Deterministic within the run, so a repeated POST after a timeout reuses the same syncId.</summary>
    public Guid SyncId(params object[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(RunId + "|" + string.Join("|", parts)));
        var guid = bytes[..16];
        guid[7] = (byte)((guid[7] & 0x0F) | 0x50); // version 5-style marker
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(guid);
    }
}
