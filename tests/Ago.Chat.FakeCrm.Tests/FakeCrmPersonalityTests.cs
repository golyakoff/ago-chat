using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Ago.Chat.FakeCrm.Tests;

/// <summary>
/// Proves all four personalities against the real, separately-running process
/// <see cref="FakeCrmProcessFixture"/> starts - a real <see cref="HttpClient"/>/<see cref="TcpClient"/>
/// over a real loopback socket, never an in-process TestServer, matching the backlog's own done-when.
/// </summary>
[Collection(FakeCrmCollection.Name)]
public sealed class FakeCrmPersonalityTests(FakeCrmProcessFixture fixture)
{
    private static readonly byte[] Body = "{\"event\":\"message.created\"}"u8.ToArray();

    [Fact]
    public async Task Succeeds_IsTheDefaultWhenNoBehaviorHeaderIsSent()
    {
        using var client = new HttpClient { BaseAddress = fixture.BaseAddress };
        using var request = SignedRequest(Body, behavior: null);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("500", HttpStatusCode.InternalServerError)]
    [InlineData("503", HttpStatusCode.ServiceUnavailable)]
    [InlineData("5xx", HttpStatusCode.ServiceUnavailable)]
    public async Task FiveXx_RespondsImmediatelyWithTheConfiguredStatus(string behavior, HttpStatusCode expected)
    {
        using var client = new HttpClient { BaseAddress = fixture.BaseAddress };
        using var request = SignedRequest(Body, behavior);

        var stopwatch = Stopwatch.StartNew();
        var response = await client.SendAsync(request);
        stopwatch.Stop();

        Assert.Equal(expected, response.StatusCode);
        // "Immediate" - not exact, but well under any hang duration this project ever tests, so a
        // regression that accidentally routed 5xx through the hang path would fail this too.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"5xx took {stopwatch.Elapsed}, expected near-instant.");
    }

    [Fact]
    public async Task Hang_HoldsForTheConfiguredDuration_ThenSucceeds()
    {
        using var client = new HttpClient { BaseAddress = fixture.BaseAddress, Timeout = TimeSpan.FromSeconds(10) };
        using var request = SignedRequest(Body, "hang-2s");

        var stopwatch = Stopwatch.StartNew();
        var response = await client.SendAsync(request);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(2), $"Only held for {stopwatch.Elapsed}, expected >= 2s.");
    }

    [Fact]
    public async Task Hang_WithoutAConfiguredDuration_NeverRespondsFirst_TheCallersOwnTimeoutEndsIt()
    {
        // Behavior "hang" (no "-<seconds>" suffix) means indefinite - Program.cs awaits
        // Timeout.InfiniteTimeSpan on the caller's own RequestAborted token, so nothing on the harness
        // side ever decides to give up. This test's own 1s client-side timeout is what must end the
        // call, not the harness.
        using var client = new HttpClient { BaseAddress = fixture.BaseAddress, Timeout = TimeSpan.FromSeconds(1) };
        using var request = SignedRequest(Body, "hang");

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Client timeout took {stopwatch.Elapsed} to fire, expected ~1s.");
    }

    [Fact]
    public async Task Disappears_RefusesTheConnectionAtTheTransportLayer_RawSocket()
    {
        using var client = new TcpClient();

        // Accept-then-RST: the reset can surface either during the connect handshake itself or on
        // the first read right after, depending on the OS TCP stack's own timing - observed live,
        // not assumed from docs: Windows tends to let ConnectAsync succeed and the RST shows up on
        // read; Linux (this project's CI runner) can surface it during ConnectAsync itself. Both are
        // the same accept-then-reset behaviour the harness performs, just observed at a different
        // point, so the assertion covers the whole sequence rather than pinning down where.
        var exception = await Record.ExceptionAsync(async () =>
        {
            await client.ConnectAsync(IPAddress.Loopback, fixture.DisappearPort);
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            var stream = client.GetStream();
            var buffer = new byte[1];
            _ = await stream.ReadAsync(buffer);
        });

        var socketException = FindInChain<SocketException>(exception);
        Assert.NotNull(socketException);
        Assert.Equal(SocketError.ConnectionReset, socketException!.SocketErrorCode);
    }

    [Fact]
    public async Task Disappears_RefusesTheConnectionAtTheTransportLayer_ViaHttpClient()
    {
        // Same personality, proven again through a real HttpClient rather than a raw TcpClient - the
        // point the backlog itself makes: this must surface as a transport failure on the caller's
        // side, not a fast HTTP error with a status code. That claim is what is asserted here, and
        // deliberately nothing narrower.
        //
        // It used to pin `HttpRequestException` and `SocketError.ConnectionReset` exactly, and that
        // was wrong in a way only CI eventually showed (2026-08-26, run 32951412968, on a PR that
        // touched nothing near this code). When the reset lands fast enough, `HttpClient` throws a
        // *bare* `SocketException` with `NotConnected` instead:
        //
        //     HttpConnectionPool.ConnectAsync -> GetRemoteEndPoint(stream) -> Socket.RemoteEndPoint
        //
        // the TCP connect succeeds, the listener resets immediately, and the pool's own call to
        // `RemoteEndPoint` finds a torn-down socket - outside the frame that would have wrapped it as
        // `HttpRequestException`. So both the exception type and the error code depend on how many
        // microseconds the reset took, which is not a fact about the harness's behaviour and is not
        // what this test is for. The raw-socket test above already made the same allowance for the
        // same reason; this one had not caught up.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var uri = new Uri($"http://127.0.0.1:{fixture.DisappearPort}/webhooks/deliver");

        var exception = await Record.ExceptionAsync(() => client.PostAsync(uri, new ByteArrayContent(Body)));

        // It failed at all, rather than returning any HttpResponseMessage - the half that matters.
        Assert.NotNull(exception);

        // And it failed at the transport, not with HTTP semantics: a SocketException is somewhere in
        // the chain, whether HttpClient wrapped it or let it through.
        var socketException = FindInChain<SocketException>(exception);
        Assert.NotNull(socketException);
        Assert.Contains(socketException!.SocketErrorCode, new[]
        {
            SocketError.ConnectionReset,   // the usual observation, and what Windows reports
            SocketError.ConnectionAborted,
            SocketError.NotConnected,      // the CI failure above: torn down before the pool looked
        });
    }

    private static T? FindInChain<T>(Exception? exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    [Fact]
    public async Task Deliver_TamperedBody_Returns401()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = WebhookSignatureVerifier.Sign(Body, timestamp, FakeCrmProcessFixture.SigningSecret);

        using var client = new HttpClient { BaseAddress = fixture.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/deliver")
        {
            // Signed one body, sent a different one - the tamper the signature must catch.
            Content = new ByteArrayContent("{\"event\":\"message.deleted\"}"u8.ToArray()),
        };
        request.Headers.Add("X-Ago-Signature", signature);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Deliver_StaleTimestamp_Returns401()
    {
        var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
        var signature = WebhookSignatureVerifier.Sign(Body, staleTimestamp, FakeCrmProcessFixture.SigningSecret);

        using var client = new HttpClient { BaseAddress = fixture.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/deliver") { Content = new ByteArrayContent(Body) };
        request.Headers.Add("X-Ago-Signature", signature);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Deliver_MissingSignature_Returns401()
    {
        using var client = new HttpClient { BaseAddress = fixture.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/deliver") { Content = new ByteArrayContent(Body) };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private HttpRequestMessage SignedRequest(byte[] body, string? behavior)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/deliver") { Content = new ByteArrayContent(body) };
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        request.Headers.Add("X-Ago-Signature", WebhookSignatureVerifier.Sign(body, timestamp, FakeCrmProcessFixture.SigningSecret));
        if (behavior is not null)
        {
            request.Headers.Add("X-Fake-Crm-Behavior", behavior);
        }

        return request;
    }
}
