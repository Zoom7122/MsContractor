using System.Text.Json;
using MergeVerifier.Capture;
using MergeVerifier.Comparison;
using MergeVerifier.Configuration;
using MergeVerifier.Models;
using MergeVerifier.MoySklad;
using MergeVerifier.Reporting;
using MergeVerifier.Storage;

namespace MergeVerifier.Cli;

public static class VerifierApplication
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        try
        {
            var command = CommandLine.Parse(args);
            if (command.Command == "help")
            {
                output.WriteLine("MergeVerifier: standalone read-only MoySklad JSON API 1.2 verifier (.NET 10).");
                output.WriteLine("capture --main UUID --duplicates UUID,UUID");
                output.WriteLine("verify --snapshot snapshots/<capture>/before.json");
                output.WriteLine("Required environment: MOYSKLAD_LOGIN, MOYSKLAD_PASSWORD.");
                output.WriteLine("Optional: MOYSKLAD_BASE_URL, MERGE_VERIFIER_SNAPSHOT_DIR.");
                output.WriteLine("Exit codes: 0 passed/captured, 1 verification failed or Unsupported, 2 error.");
                return 0;
            }
            // Validate the snapshot before making any HTTP calls.
            var before = command.Command == "verify" ? await SnapshotStore.ReadAsync(command.SnapshotPath!, ct) : null;
            var options = MergeVerifierOptions.FromEnvironment();
            using var client = MoySkladClient.FromEnvironment(options);
            var collector = new SnapshotCollector(client, output);
            if (before is null)
            {
                output.WriteLine("Capturing BEFORE snapshot...");
                var snapshot = await collector.CaptureAsync(command.Main, command.Duplicates, ct);
                SnapshotStore.Validate(snapshot);
                var folder = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}_{command.Main.ToString("N")[..8]}_{Guid.NewGuid().ToString("N")[..6]}";
                var path = Path.Combine(options.SnapshotDirectory, folder, "before.json");
                await SnapshotStore.WriteAsync(path, snapshot, overwrite: false, ct);
                output.WriteLine($"BEFORE saved: {Path.GetFullPath(path)}");
                output.WriteLine("Capture complete; rules and evidence: docs/document-transfer-rules.md.");
                return 0;
            }
            return await VerifyAsync(before, command.SnapshotPath!, collector, output, error, ct);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error.WriteLine(SafeError(exception));
            return 2;
        }
    }

    public static async Task<int> VerifyAsync(MergeSnapshot before, string snapshotPath, SnapshotCollector collector,
        TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(snapshotPath))!;
        var afterSaved = false;
        try
        {
            output.WriteLine("Capturing AFTER snapshot...");
            var after = await collector.CaptureAsync(before.MainCounterpartyId, before.DuplicateCounterpartyIds, ct);
            SnapshotStore.Validate(after);
            await SnapshotStore.WriteAsync(Path.Combine(directory, "after.json"), after, overwrite: true, ct);
            afterSaved = true;
            var report = DocumentComparisonService.Compare(before, after);
            await SnapshotStore.WriteAsync(Path.Combine(directory, "report.json"), report, overwrite: true, ct);
            ConsoleReportWriter.Write(report, output);
            return report.ExitCode;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var message = SafeError(exception);
            var report = new VerificationReport
            {
                GeneratedAt = DateTimeOffset.UtcNow, MainCounterpartyId = before.MainCounterpartyId,
                Error = message, AfterCaptureCompleted = afterSaved,
                Types = [new("capture", Documents.DocumentTransferMode.Unsupported, before.Documents.Count, 0,
                    [new() { Status = VerificationStatus.FetchError, Note = "No successful verification. Any older after.json is not evidence for this run." }])]
            };
            try { await SnapshotStore.WriteAsync(Path.Combine(directory, "report.json"), report, overwrite: true, CancellationToken.None); }
            catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException)
            { error.WriteLine("Could not write error report.json."); }
            error.WriteLine(message);
            ConsoleReportWriter.Write(report, output);
            return 2;
        }
    }

    public static string SafeError(Exception exception) => exception switch
    {
        VerifierException => exception.Message,
        JsonException => "Invalid JSON in API response or snapshot; verification is incomplete.",
        OperationCanceledException => "Operation cancelled; verification is incomplete.",
        IOException or UnauthorizedAccessException => "Cannot read or write verifier files. Check paths and permissions.",
        _ => "Verifier runtime error; verification is incomplete. No remote changes were made."
    };
}
