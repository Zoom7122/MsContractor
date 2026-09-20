using MergeVerifier.Models;

namespace MergeVerifier.Reporting;

public static class ConsoleReportWriter
{
    public static void Write(VerificationReport report)
    {
        Console.WriteLine("MERGE VERIFICATION");
        Console.WriteLine("==================");
        Console.WriteLine($"Account: {report.AccountId:D}");
        Console.WriteLine($"Main: {report.MainCounterpartyId:D}");
        Console.WriteLine();
        Console.WriteLine("COUNTERPARTIES");
        WriteStatus(report.Counterparties.MainExists && report.Counterparties.MainArchived == false,
            $"Main exists; archived={report.Counterparties.MainArchived?.ToString().ToLowerInvariant() ?? "unknown"}");
        foreach (var duplicate in report.Counterparties.Duplicates)
            WriteStatus(duplicate.Passed, $"Duplicate {duplicate.CounterpartyId:D} exists={duplicate.Exists}; archived={duplicate.Archived?.ToString().ToLowerInvariant() ?? "unknown"}");
        foreach (var change in report.Counterparties.MainChanges)
            Console.WriteLine($"  main {change.Field}: {change.Before} -> {change.After}");
        Console.WriteLine();
        Console.WriteLine("DOCUMENTS");
        foreach (var type in report.Types)
        {
            Console.WriteLine($"{type.DocumentType}: before main={type.BeforeMain}, before duplicates={type.BeforeDuplicates}, after main={type.AfterMain}; matched={type.Matched}, missing={type.Missing}, changed={type.Changed}, ambiguous={type.Ambiguous}, unexpected={type.Unexpected}");
            foreach (var item in report.Documents.Where(item => item.DocumentType == type.DocumentType && item.Status != "Matched"))
                Console.WriteLine($"  {Symbol(item.Status)} before={Format(item.BeforeDocumentId)} after={Format(item.AfterDocumentId)} operation={item.Operation} result={item.Status}{(item.Detail is null ? string.Empty : $"; {item.Detail}")}");
        }
        Console.WriteLine();
        Console.WriteLine($"SUMMARY: matched={report.Matched}; missing={report.Missing}; changed={report.Changed}; ambiguous={report.Ambiguous}; unexpected={report.Unexpected}");
        WriteStatus(report.Passed, $"RESULT: {(report.Passed ? "PASSED" : "FAILED")}");
    }
    private static string Format(Guid? id) => id is null || id == Guid.Empty ? "-" : id.Value.ToString("D");
    private static void WriteStatus(bool passed, string text) => Console.WriteLine($"{(passed ? "OK" : "FAIL")} {text}");
    private static string Symbol(string status) => status is "Matched" ? "OK" : "FAIL";
}
