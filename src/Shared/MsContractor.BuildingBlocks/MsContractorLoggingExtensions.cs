using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace MsContractor.BuildingBlocks.Logging;

public static class MsContractorLoggingExtensions
{
    public static ILoggingBuilder AddMsContractorLogging(this ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddConsole(options =>
        {
            options.FormatterName = MsContractorConsoleFormatter.FormatterName;
        });
        logging.AddConsoleFormatter<MsContractorConsoleFormatter, MsContractorConsoleFormatterOptions>(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        });

        return logging;
    }
}
