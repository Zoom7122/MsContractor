using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MsContractor.Gateway.Bff.Middleware;

namespace MsContractor.Sync.Tests;

public sealed class VendorRequestCorrelationMiddlewareTests
{
    [Theory]
    [InlineData(StatusCodes.Status200OK, LogLevel.Information)]
    [InlineData(StatusCodes.Status400BadRequest, LogLevel.Error)]
    [InlineData(StatusCodes.Status500InternalServerError, LogLevel.Error)]
    public async Task InvokeAsync_LogsCompletionAtExpectedLevel(int statusCode, LogLevel expectedLevel)
    {
        var logger = new CaptureLogger<VendorRequestCorrelationMiddleware>();
        var middleware = new VendorRequestCorrelationMiddleware(context =>
        {
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        }, logger);

        await middleware.InvokeAsync(new DefaultHttpContext());

        Assert.Equal(expectedLevel, logger.Entries.Single(entry =>
            entry.Message.StartsWith("Gateway request completed", StringComparison.Ordinal)).Level);
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}
