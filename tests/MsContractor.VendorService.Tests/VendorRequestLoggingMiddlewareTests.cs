using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MsContractor.VendorService.Middleware;

namespace MsContractor.VendorService.Tests;

public sealed class VendorRequestLoggingMiddlewareTests
{
    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    public async Task InvokeAsync_LogsClientAndServerErrorsAtErrorLevel(int statusCode)
    {
        var loggerProvider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var middleware = new VendorRequestLoggingMiddleware(
            context =>
            {
                context.Response.StatusCode = statusCode;
                return Task.CompletedTask;
            },
            loggerFactory.CreateLogger<VendorRequestLoggingMiddleware>());
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        var completedRequest = Assert.Single(
            loggerProvider.Entries,
            entry => entry.Message.StartsWith("VendorService request completed", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Error, completedRequest.Level);
    }

    [Fact]
    public async Task InvokeAsync_LogsSuccessfulRequestsAtInformationLevel()
    {
        var loggerProvider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var middleware = new VendorRequestLoggingMiddleware(
            _ => Task.CompletedTask,
            loggerFactory.CreateLogger<VendorRequestLoggingMiddleware>());
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        var completedRequest = Assert.Single(
            loggerProvider.Entries,
            entry => entry.Message.StartsWith("VendorService request completed", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, completedRequest.Level);
    }

    private sealed class TestLoggerProvider : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new TestLogger(Entries);

        public void Dispose()
        {
        }
    }

    private sealed class TestLogger(List<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}
