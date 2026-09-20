using MergeVerifier.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; cancellation.Cancel(); };
return await VerifierApplication.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
