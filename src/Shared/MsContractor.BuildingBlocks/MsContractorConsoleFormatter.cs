using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace MsContractor.BuildingBlocks.Logging;

public sealed class MsContractorConsoleFormatterOptions : ConsoleFormatterOptions
{
    public string ServiceName { get; set; } = ResolveServiceName();

    private static string ResolveServiceName()
    {
        var applicationName = Assembly.GetEntryAssembly()?.GetName().Name ?? "unknown";
        const string prefix = "MsContractor.";
        return applicationName.StartsWith(prefix, StringComparison.Ordinal)
            ? applicationName[prefix.Length..]
            : applicationName;
    }
}

public sealed class MsContractorConsoleFormatter(
    IOptions<MsContractorConsoleFormatterOptions> options)
    : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "mscontractor-json";

    private static readonly JavaScriptEncoder UnicodeEncoder = JavaScriptEncoder.Create(UnicodeRanges.All);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = UnicodeEncoder
    };
    private static readonly HashSet<string> ReservedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "timestamp", "level", "service", "category", "message", "exception",
        "traceId", "spanId", "eventId"
    };

    private readonly MsContractorConsoleFormatterOptions formatterOptions = options.Value;

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (formatterOptions.IncludeScopes && scopeProvider is not null)
        {
            scopeProvider.ForEachScope(
                static (scope, target) => AddProperties(scope, target),
                properties);
        }

        AddProperties(logEntry.State, properties);

        var activity = Activity.Current;
        var traceId = activity?.TraceId.ToString() ?? TakeString(properties, "traceId");
        var spanId = activity?.SpanId.ToString() ?? TakeString(properties, "spanId");

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = UnicodeEncoder
        }))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "timestamp",
                DateTimeOffset.UtcNow.ToString(
                    formatterOptions.TimestampFormat ?? "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture));
            writer.WriteString("level", logEntry.LogLevel.ToString().ToLowerInvariant());
            writer.WriteString("service", formatterOptions.ServiceName);
            writer.WriteString("category", logEntry.Category);
            writer.WriteString("message", logEntry.Formatter(logEntry.State, logEntry.Exception));

            if (logEntry.Exception is not null)
            {
                writer.WritePropertyName("exception");
                WriteException(writer, logEntry.Exception);
            }

            if (!string.IsNullOrWhiteSpace(traceId))
                writer.WriteString("traceId", traceId);
            if (!string.IsNullOrWhiteSpace(spanId))
                writer.WriteString("spanId", spanId);
            if (logEntry.EventId.Id != 0 || logEntry.EventId.Name is not null)
                WriteEventId(writer, logEntry.EventId);

            foreach (var (name, value) in properties)
            {
                if (ReservedProperties.Contains(name))
                    continue;

                writer.WritePropertyName(name);
                WriteValue(writer, value);
            }

            writer.WriteEndObject();
        }

        textWriter.Write(Encoding.UTF8.GetString(buffer.WrittenSpan));
        textWriter.WriteLine();
    }

    private static void AddProperties(object? value, IDictionary<string, object?> properties)
    {
        if (value is not IEnumerable<KeyValuePair<string, object?>> pairs)
            return;

        foreach (var (rawName, propertyValue) in pairs)
        {
            if (rawName == "{OriginalFormat}" || rawName.Equals("Message", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = JsonNamingPolicy.CamelCase.ConvertName(rawName);
            properties[name] = propertyValue;
        }
    }

    private static string? TakeString(IDictionary<string, object?> properties, string name)
    {
        if (!properties.Remove(name, out var value))
            return null;

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static void WriteException(Utf8JsonWriter writer, Exception exception)
    {
        writer.WriteStartObject();
        writer.WriteString("type", exception.GetType().FullName);
        writer.WriteString("message", exception.Message);
        writer.WriteString("stackTrace", exception.StackTrace);
        if (exception.InnerException is not null)
        {
            writer.WritePropertyName("innerException");
            WriteException(writer, exception.InnerException);
        }
        writer.WriteEndObject();
    }

    private static void WriteEventId(Utf8JsonWriter writer, EventId eventId)
    {
        writer.WriteStartObject("eventId");
        writer.WriteNumber("id", eventId.Id);
        if (eventId.Name is not null)
            writer.WriteString("name", eventId.Name);
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        try
        {
            JsonSerializer.SerializeToElement(
                    value,
                    value?.GetType() ?? typeof(object),
                    SerializerOptions)
                .WriteTo(writer);
        }
        catch (Exception)
        {
            writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
        }
    }
}
