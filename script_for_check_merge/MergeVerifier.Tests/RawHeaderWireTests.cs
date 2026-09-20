using System.Net;
using System.Net.Sockets;
using System.Text;
using MergeVerifier.MoySklad;
using Xunit;

namespace MergeVerifier.Tests;

public sealed class RawHeaderWireTests
{
    [Fact]
    public async Task AcceptHeaderOnWireHasRequiredNoSpaceFormat()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var stream = socket.GetStream();
            var buffer = new byte[8192]; var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total)); total += read;
                var text = Encoding.ASCII.GetString(buffer, 0, total);
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) { received.SetResult(text); break; }
            }
            var body = "{}";
            var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        });
        try
        {
            using var client = new MoySkladClient(new Uri($"http://127.0.0.1:{port}/api/remap/1.2/"), "login", "password");
            await client.GetAsync("entity/customerorder");
            var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("Accept: application/json;charset=utf-8\r\n", request, StringComparison.Ordinal);
            Assert.DoesNotContain("Accept: application/json; charset=utf-8\r\n", request, StringComparison.Ordinal);
        }
        finally { listener.Stop(); await server; }
    }
}
