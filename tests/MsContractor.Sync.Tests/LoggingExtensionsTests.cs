using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using MsContractor.BuildingBlocks.Logging;

namespace MsContractor.Sync.Tests;

public sealed class LoggingExtensionsTests
{
    [Fact]
    public void AddMsContractorLogging_RegistersOnlyConfiguredConsoleProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddMsContractorLogging());
        using var serviceProvider = services.BuildServiceProvider();

        var providers = serviceProvider.GetServices<ILoggerProvider>().ToArray();
        var consoleOptions = serviceProvider
            .GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>()
            .CurrentValue;
        var formatterOptions = serviceProvider
            .GetRequiredService<IOptionsMonitor<MsContractorConsoleFormatterOptions>>()
            .CurrentValue;

        Assert.Single(providers);
        Assert.IsType<ConsoleLoggerProvider>(providers[0]);
        Assert.Equal(MsContractorConsoleFormatter.FormatterName, consoleOptions.FormatterName);
        Assert.True(formatterOptions.IncludeScopes);
        Assert.True(formatterOptions.UseUtcTimestamp);
        Assert.Equal("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", formatterOptions.TimestampFormat);
    }

    [Fact]
    public void Formatter_WritesExpectedSingleLineProductionSchema()
    {
        var formatter = new MsContractorConsoleFormatter(
            Options.Create(new MsContractorConsoleFormatterOptions
            {
                IncludeScopes = true,
                UseUtcTimestamp = true,
                TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                ServiceName = "MoySkladEgressService"
            }));
        var scopes = new LoggerExternalScopeProvider();
        using var requestScope = scopes.Push(new Dictionary<string, object?>
        {
            ["RequestPath"] = "/internal/accounts/test/counterparties"
        });
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var exception = CaptureException();
        IReadOnlyList<KeyValuePair<string, object?>> state =
        [
            new("AccountId", Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new("{OriginalFormat}", "Request failed for {AccountId}")
        ];
        var entry = new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(
            LogLevel.Error,
            "Test.Category",
            new EventId(1),
            state,
            exception,
            static (_, _) => "Ошибка чтения данных");
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        formatter.Write(entry, scopes, output);

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var root = JsonDocument.Parse(Assert.Single(lines)).RootElement;
        Assert.EndsWith("Z", root.GetProperty("timestamp").GetString(), StringComparison.Ordinal);
        Assert.Equal("error", root.GetProperty("level").GetString());
        Assert.Equal("MoySkladEgressService", root.GetProperty("service").GetString());
        Assert.Equal("Test.Category", root.GetProperty("category").GetString());
        Assert.Equal("Ошибка чтения данных", root.GetProperty("message").GetString());
        Assert.Contains("Ошибка чтения данных", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\\u041E", lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(typeof(InvalidOperationException).FullName, root.GetProperty("exception").GetProperty("type").GetString());
        Assert.Equal("Test failure", root.GetProperty("exception").GetProperty("message").GetString());
        Assert.Contains(nameof(CaptureException), root.GetProperty("exception").GetProperty("stackTrace").GetString());
        Assert.Equal(activity.TraceId.ToString(), root.GetProperty("traceId").GetString());
        Assert.Equal(activity.SpanId.ToString(), root.GetProperty("spanId").GetString());
        Assert.Equal("/internal/accounts/test/counterparties", root.GetProperty("requestPath").GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", root.GetProperty("accountId").GetString());
    }

    private static Exception CaptureException()
    {
        try
        {
            throw new InvalidOperationException("Test failure");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
