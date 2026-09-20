using MergeVerifier.Models;

namespace MergeVerifier.Reporting;

public static class ConsoleReportWriter
{
    public static void Write(VerificationReport report, TextWriter writer)
    {
        writer.WriteLine("MERGE VERIFICATION REPORT");
        writer.WriteLine("=========================");
        foreach (var type in report.Types)
        {
            writer.WriteLine($"\n{type.EntityType} [{type.TransferMode}]");
            writer.WriteLine($"Before: {type.Before}   After main: {type.AfterMain}");
            foreach (var status in Enum.GetValues<VerificationStatus>().Where(x => x != VerificationStatus.FetchError || type.Count(x) > 0))
                writer.WriteLine($"{status,-20} {type.Count(status)}");
            foreach (var entry in type.Documents.Where(x => x.Status != VerificationStatus.Matched))
            {
                var name = entry.Data is { } data && data.TryGetProperty("name", out var n) ? n.ToString() : "";
                writer.WriteLine($"  {entry.Status} x{entry.Count} {Safe(name)}");
                if (entry.StableDocumentId is not null) writer.WriteLine($"    document-id: {entry.StableDocumentId}");
                if (entry.Note is not null) writer.WriteLine($"    {Safe(entry.Note)}");
                foreach (var difference in entry.Differences) writer.WriteLine($"    diff: {Safe(difference.Path)} (values in report.json)");
            }
            writer.WriteLine(type.Passed ? "PASS" : "FAIL");
        }
        writer.WriteLine($"\nTOTAL BEFORE: {report.TotalBefore}");
        foreach (var (status, count) in report.Totals) writer.WriteLine($"{status,-20} {count}");
        if (report.Error is not null) writer.WriteLine(report.Error);
        writer.WriteLine($"RESULT: {(report.ExitCode == 2 ? "ERROR" : report.Passed ? "PASSED" : "FAILED")}");
    }

    // Document names are untrusted; prevent terminal control sequences in progress/report output.
    private static string Safe(string value) => string.Concat(value.Where(c => !char.IsControl(c)));
}
