using DocumentRelationsGenerator.Cli;
using DocumentRelationsGenerator.Configuration;
using DocumentRelationsGenerator.MoySklad;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var application = new GeneratorApplication(
    Console.Out,
    Console.Error,
    EnvFile.ProcessEnvironment(),
    (settings, trace) => new MoySkladApiClient(settings.BaseUrl, settings.Credentials!, settings.MinRequestInterval, trace: trace),
    () => DateTimeOffset.Now);

return await application.RunAsync(args, cancellation.Token);
