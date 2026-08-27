using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// Found live, against the deployed cluster, not in review: `demo-mint:ip:*` and `register-site:ip:*`
/// Redis keys were the Gateway pod's own cluster-internal address
/// (<c>::ffff:10.42.0.77</c>, confirmed against <c>kubectl get pods -o wide</c>), not any real
/// visitor's IP - `edge.md`'s own stated-but-never-enforced note ("the app must be configured to
/// trust [X-Forwarded-For], or every per-IP limit silently applies to the ingress itself") had never
/// actually been wired up. Every "per-IP" rate limiter in this codebase
/// (<see cref="Api.Demo.DemoEndpoints"/>, <see cref="Api.Sites.SitesEndpoints"/>) was therefore one
/// shared bucket for every visitor on the internet - one person's testing could, and on 2026-08-27
/// did, lock every other visitor out of minting a demo tenant.
///
/// <para><b>Verified here against a real Kestrel host, not asserted from `ForwardedHeadersOptions`'
/// own documentation</b> - the exact configuration `Program.cs` now carries, reduced to the one
/// question that matters: does the resolved <see cref="Microsoft.AspNetCore.Http.HttpContext.Connection"/>'s
/// <c>RemoteIpAddress</c> become the forwarded value when the connecting peer is inside the trusted
/// network, and does it correctly ignore a forged header when the peer is not. The second case is the
/// one a fix that only proved the first half could get wrong: `X-Forwarded-For` is entirely
/// caller-controlled, so trusting it unconditionally would let any visitor pick their own rate-limit
/// bucket - a bigger hole than the one being closed.</para>
/// </summary>
public sealed class ForwardedHeadersTests
{
    [Fact]
    public async Task A_forwarded_header_from_a_trusted_network_becomes_the_remote_address()
    {
        // The real Kestrel loopback peer for every request in this test is 127.0.0.1 - trusting
        // 127.0.0.0/8 here stands in for `Program.cs`'s own 10.42.0.0/16 (this cluster's pod network,
        // where the real Gateway's connections actually originate), the same trust *relationship*
        // with a network this test can actually connect from.
        await using var host = await BuildHostAsync(trustedNetworkCidr: "127.0.0.0/8");
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");

        var observedRemoteIp = await client.GetStringAsync("/whoami");

        Assert.Equal("203.0.113.7", observedRemoteIp);
    }

    [Fact]
    public async Task A_forwarded_header_from_an_untrusted_network_is_ignored()
    {
        // Trusting a network the real peer (127.0.0.1) is not inside - the shape of every real
        // caller that is not the Gateway. If this test failed, any visitor could set
        // X-Forwarded-For themselves and pick their own fresh rate-limit bucket on demand, which is
        // the exact hole `KnownIPNetworks`/`KnownProxies` exist to close (Program.cs's own remarks).
        await using var host = await BuildHostAsync(trustedNetworkCidr: "10.42.0.0/16");
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");

        var observedRemoteIp = await client.GetStringAsync("/whoami");

        Assert.Equal("127.0.0.1", observedRemoteIp);
    }

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port - deliberately not
    /// `TestServer`, whose in-memory transport does not run the same connection-level IP resolution
    /// `ForwardedHeadersMiddleware` reads from. Carries the identical `ForwardedHeadersOptions` shape
    /// `Program.cs` configures, with only the trusted network parameterised per test.</summary>
    private static async Task<TestHost> BuildHostAsync(string trustedNetworkCidr)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(trustedNetworkCidr));
        });

        var app = builder.Build();
        app.UseForwardedHeaders();
        app.MapGet("/whoami", (HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "none");

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseUrl = addresses.First() + "/";

        return new TestHost(app, baseUrl);
    }
}
