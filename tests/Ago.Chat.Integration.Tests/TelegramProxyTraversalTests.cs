using System.Net;
using System.Net.Sockets;
using System.Text;
using Ago.Chat.Infrastructure.Telegram;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-07`'s own Done-when, taken literally: "a real test that proves traffic actually traverses the
/// configured proxy - not an assertion that a property was set." Everything else this item's tests cover
/// (<see cref="TelegramApiClientTests"/>, <see cref="TelegramInboundMessageParserTests"/>) proves this
/// channel's own logic; this class proves the one piece of wiring genuinely new to this codebase -
/// <c>ChatModule</c>'s <c>ConfigurePrimaryHttpMessageHandler</c> registration for
/// <see cref="TelegramApiClient"/>'s <see cref="HttpClient"/>, built the identical way the real DI
/// registration builds it (a <see cref="SocketsHttpHandler"/> with <see cref="SocketsHttpHandler.Proxy"/>
/// set to a <c>socks5://</c> <see cref="WebProxy"/>), pointed at a real SOCKS5 listener this test process
/// stands up and controls.
///
/// <para><b>Why a hand-rolled listener rather than a mocked <see cref="IWebProxy"/>.</b> A mock proxy
/// can only prove "the handler was configured with some <see cref="IWebProxy"/>" - it says nothing about
/// whether <see cref="SocketsHttpHandler"/> actually understands the <c>socks5://</c> scheme, actually
/// performs the SOCKS5 handshake, or actually routes the request's bytes through it, which is exactly the
/// part of .NET's own behaviour this item takes on faith when it registers the real client. This test
/// therefore implements just enough of RFC 1928 to complete one real handshake - greeting, no-auth
/// negotiation, a CONNECT request - and then answers the tunnelled HTTP request itself, so the response
/// <see cref="TelegramApiClient"/> receives could only have come from this listener. No target host is
/// actually reachable or resolved anywhere in this test; the listener's own record of the CONNECT
/// request's target is the proof the client asked to reach it through the proxy, not around it.</para>
/// </summary>
public sealed class TelegramProxyTraversalTests
{
    [Fact]
    public async Task ARequestBuiltTheSameWayChatModuleBuildsIt_ActuallyTraversesTheConfiguredSocks5Proxy()
    {
        using var socks5 = new FakeSocks5Listener();
        var proxyAddress = socks5.Start();

        // The exact shape ChatModule.ConfigureServices registers for TelegramApiClient's HttpClient -
        // a SocketsHttpHandler with Proxy set from a "host:port" TelegramProxyOptions.Socks5Address
        // value, built into a socks5:// Uri.
        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(new Uri($"socks5://{proxyAddress}")),
            UseProxy = true,
        };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake-telegram.invalid.example/") };
        var apiClient = new TelegramApiClient(httpClient);

        var result = await apiClient.GetMeAsync("123456:test-token-not-a-real-secret", CancellationToken.None);

        // The response can only have reached the client through this listener - nothing else answers
        // for "fake-telegram.invalid.example", a name this test never registers or resolves anywhere.
        Assert.True(result.Ok);

        await socks5.WaitForOneConnectAsync(TimeSpan.FromSeconds(5));
        Assert.True(socks5.ObservedConnect);
        Assert.Equal("fake-telegram.invalid.example", socks5.ObservedTargetHost);
        Assert.Equal(80, socks5.ObservedTargetPort);
    }

    /// <summary>A minimal RFC 1928 SOCKS5 server - just enough to complete one real handshake and prove
    /// a caller went through it: no-auth only, CONNECT only, and rather than actually opening a second
    /// connection to the requested target, it answers the tunnelled HTTP request directly over the same
    /// socket. That is a deliberate simplification, not a gap in what is being proven - the property
    /// under test is "did <see cref="SocketsHttpHandler"/> speak real SOCKS5 to this real listener and
    /// send the HTTP request through the tunnel it negotiated", which is fully exercised whether or not
    /// this stub actually forwards the bytes onward afterwards.</summary>
    private sealed class FakeSocks5Listener : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource _connectObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _cts;
        private Task? _acceptLoop;

        public bool ObservedConnect { get; private set; }

        public string? ObservedTargetHost { get; private set; }

        public int ObservedTargetPort { get; private set; }

        public string Start()
        {
            _listener.Start();
            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptOnceAsync(_cts.Token));
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            return $"127.0.0.1:{port}";
        }

        public Task WaitForOneConnectAsync(TimeSpan timeout) =>
            _connectObserved.Task.WaitAsync(timeout);

        private async Task AcceptOnceAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                using var stream = client.GetStream();

                // Greeting: VER(1) NMETHODS(1) METHODS(NMETHODS) - accept whatever methods are offered
                // and always select 0x00 (no authentication required), the only mechanism this test's
                // configured proxy needs to support.
                var greeting = await ReadExactAsync(stream, 2, cancellationToken);
                var methodCount = greeting[1];
                await ReadExactAsync(stream, methodCount, cancellationToken);
                await stream.WriteAsync(new byte[] { 0x05, 0x00 }, cancellationToken);

                // Request: VER(1) CMD(1) RSV(1) ATYP(1) DST.ADDR(var) DST.PORT(2)
                var header = await ReadExactAsync(stream, 4, cancellationToken);
                var addressType = header[3];

                string host = addressType switch
                {
                    0x01 => new IPAddress(await ReadExactAsync(stream, 4, cancellationToken)).ToString(),
                    0x03 => await ReadDomainNameAsync(stream, cancellationToken),
                    0x04 => new IPAddress(await ReadExactAsync(stream, 16, cancellationToken)).ToString(),
                    _ => throw new NotSupportedException($"Unsupported SOCKS5 address type {addressType}."),
                };

                var portBytes = await ReadExactAsync(stream, 2, cancellationToken);
                var port = (portBytes[0] << 8) | portBytes[1];

                ObservedTargetHost = host;
                ObservedTargetPort = port;
                ObservedConnect = true;
                _connectObserved.TrySetResult();

                // Reply: VER(1) REP(1)=succeeded RSV(1) ATYP(1)=IPv4 BND.ADDR(4) BND.PORT(2) - the bind
                // address/port are never inspected by SocketsHttpHandler's own SOCKS5 client, so zeros
                // are fine here (RFC 1928 leaves them meaningful only for a real relay).
                await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, cancellationToken);

                // The tunnelled plaintext HTTP request now arrives over this same socket - read until
                // the end of its headers, then answer as if this listener were Telegram itself. Reading
                // byte-by-byte keeps this simple and correct for the small, ASCII, header-only request
                // TelegramApiClient.GetMeAsync sends (no body to worry about splitting mid-multibyte
                // character).
                await ReadUntilHeadersEndAsync(stream, cancellationToken);

                var body = "{\"ok\":true,\"result\":{\"id\":1,\"is_bot\":true}}"u8.ToArray();
                var responseHeader = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: application/json\r\n"
                    + $"Content-Length: {body.Length}\r\n"
                    + "Connection: close\r\n\r\n");
                await stream.WriteAsync(responseHeader, cancellationToken);
                await stream.WriteAsync(body, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Test teardown (Dispose stopping the listener) races the accept loop - not a failure.
            }
        }

        private static async Task<string> ReadDomainNameAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var lengthByte = await ReadExactAsync(stream, 1, cancellationToken);
            var domainBytes = await ReadExactAsync(stream, lengthByte[0], cancellationToken);
            return Encoding.ASCII.GetString(domainBytes);
        }

        private static async Task ReadUntilHeadersEndAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[1];
            var tail = new byte[4];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
                if (read == 0)
                {
                    throw new IOException("The tunnelled connection closed before the request headers ended.");
                }

                tail[0] = tail[1];
                tail[1] = tail[2];
                tail[2] = tail[3];
                tail[3] = buffer[0];

                if (tail is [(byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n'])
                {
                    return;
                }
            }
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken cancellationToken)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken);
                if (read == 0)
                {
                    throw new IOException("The SOCKS5 handshake connection closed unexpectedly.");
                }

                offset += read;
            }

            return buffer;
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _listener.Stop();
            _cts?.Dispose();
        }
    }
}
