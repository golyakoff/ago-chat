using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Ago.Chat.Api.Auth;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `17-03`/`adr/0067`: the claim this item is actually making, exercised end to end - <b>the visitor
/// signing key can be changed without logging every visitor out.</b>
///
/// <para>`17-03`'s audit found that it could not. One key signed and the same one key validated, so
/// the instant it changed, every outstanding visitor token on every site became a 401 simultaneously.
/// That made rotation a customer-visible incident, which is the reason a key that has never been
/// rotated has an effective lifetime of "forever".</para>
///
/// <para>Real tokens through a real <c>JwtBearer</c> handler, not a direct call to
/// <see cref="IVisitorSigningKeyRing.ValidationKeys"/>: a ring that returns a correct list and is
/// wired to nothing rotates nothing, and the wiring - <c>IssuerSigningKeyResolver</c> rather than
/// <c>IssuerSigningKey</c> - is the part that was missing. The scheme's configuration is transcribed
/// from `Program.cs`, the same way <see cref="TokenSchemeSeparationTests"/> and
/// <see cref="VisitorSessionRenewalTests"/> already transcribe theirs. No fixture and no container:
/// nothing here touches Postgres, Redis or Keycloak.</para>
///
/// <para><b>Two clocks, deliberately, and it is the trick that makes these assertions
/// attributable.</b> A token's own lifetime is checked by the handler against the real system clock;
/// the drain window is checked by the ring against <see cref="MutableClock"/>. Every token below is
/// minted with a genuine, currently-valid lifetime and only the *ring's* clock moves, so a 401 can
/// only mean "this key is no longer accepted" and never "this token expired".</para>
/// </summary>
public class VisitorKeyRotationTests
{
    private const string Issuer = "ago-chat-api";

    /// <summary>
    /// The whole point. A token minted under the outgoing key keeps working after the rotation, for
    /// the length of the drain window - and a token minted after it works too, so the rotation is
    /// genuinely complete rather than merely tolerated.
    /// </summary>
    [Fact]
    public async Task ATokenMintedUnderThePreviousKey_StillValidatesAfterTheKeyHasChanged()
    {
        var previousKey = NewKey();
        var rotatedAt = DateTimeOffset.UtcNow;

        var beforeRotation = Ring(new MutableClock(rotatedAt), Entry("2026-08", previousKey));
        var tokenFromBeforeTheRotation = Mint(beforeRotation);

        var afterRotation = Ring(
            new MutableClock(rotatedAt.AddHours(1)),
            Entry("2026-08", previousKey, rotatedAt),
            Entry("2026-09", NewKey()));
        var tokenFromAfterTheRotation = Mint(afterRotation);

        // The new key is unambiguously the one that signs now - read off the token, not asserted
        // about the configuration.
        Assert.Equal("2026-09", KeyIdOf(tokenFromAfterTheRotation));
        Assert.Equal("2026-08", KeyIdOf(tokenFromBeforeTheRotation));

        using var host = await BuildHostAsync(afterRotation);
        using var client = host.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, tokenFromBeforeTheRotation));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, tokenFromAfterTheRotation));
    }

    /// <summary>
    /// The other half, and the half that makes the first one worth anything. If a retired key were
    /// accepted forever, the "rotation" would be an addition and the old key would never stop being a
    /// working credential. The same token, the same host, the same real lifetime - only the ring's
    /// clock has crossed <c>RetiredAt + RetirementDelay</c>.
    /// </summary>
    [Fact]
    public async Task ATokenSignedByARetiredKey_IsRejectedOnceTheDrainWindowHasClosed()
    {
        var retiredKey = NewKey();
        var rotatedAt = DateTimeOffset.UtcNow;
        var clock = new MutableClock(rotatedAt);
        var ring = Ring(clock, Entry("2026-08", retiredKey, rotatedAt), Entry("2026-09", NewKey()));

        var token = Mint(Ring(new MutableClock(rotatedAt), Entry("2026-08", retiredKey)));

        using var host = await BuildHostAsync(ring);
        using var client = host.GetTestClient();

        // Inside the window, an hour after the rotation.
        clock.UtcNow = rotatedAt.AddHours(1);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, token));

        // Past it. Nothing restarted, nothing was redeployed, and the same client is asking with the
        // same bytes: the resolver is consulted per token, so the set simply no longer contains this
        // key.
        clock.UtcNow = rotatedAt + JwtTokenService.VisitorTokenLifetime + TimeSpan.FromSeconds(1);
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(client, token));
    }

    /// <summary>
    /// The drain window is configuration, proven where it matters rather than in a property getter:
    /// two hosts, one token, one instant, two answers.
    ///
    /// <para>This is the assertion `17-03` asked for by name. The visitor token's lifetime has
    /// already moved once - thirty days (`17-06`/`adr/0034`) to seven (`17-07`+`17-08`/`adr/0048`) -
    /// and the drain window is derived from it, so a literal in the validation path is a number with
    /// a demonstrated history of changing sitting in a place that needs a release to change.</para>
    /// </summary>
    [Fact]
    public async Task TheDrainWindowIsConfiguration_AndADifferentValueMovesWhenTheOldKeyStops()
    {
        var retiredKey = NewKey();
        var rotatedAt = DateTimeOffset.UtcNow;
        var tenDaysLater = rotatedAt.AddDays(10);
        var token = Mint(Ring(new MutableClock(rotatedAt), Entry("2026-08", retiredKey)));

        using var sevenDayWindow = await BuildHostAsync(Ring(
            new MutableClock(tenDaysLater), TimeSpan.FromDays(7),
            Entry("2026-08", retiredKey, rotatedAt), Entry("2026-09", NewKey())));
        using var twentyOneDayWindow = await BuildHostAsync(Ring(
            new MutableClock(tenDaysLater), TimeSpan.FromDays(21),
            Entry("2026-08", retiredKey, rotatedAt), Entry("2026-09", NewKey())));

        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(sevenDayWindow.GetTestClient(), token));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(twentyOneDayWindow.GetTestClient(), token));
    }

    /// <summary>
    /// A key the ring has never heard of is rejected, which is what says the tests above are about
    /// the *set* rather than about the handler having stopped checking signatures at all.
    /// </summary>
    [Fact]
    public async Task ATokenSignedByAKeyThatIsNotInTheRing_IsRejected()
    {
        var stranger = Mint(Ring(new MutableClock(DateTimeOffset.UtcNow), Entry("elsewhere", NewKey())));

        using var host = await BuildHostAsync(Ring(
            new MutableClock(DateTimeOffset.UtcNow), Entry("2026-09", NewKey())));

        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(host.GetTestClient(), stranger));
    }

    // ------------------------------------------------------------------------------------------

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static VisitorSigningKeyEntry Entry(string id, string value, DateTimeOffset? retiredAt = null) =>
        new() { Id = id, Value = value, RetiredAt = retiredAt };

    private static VisitorSigningKeyRing Ring(IClock clock, params VisitorSigningKeyEntry[] keys) =>
        Ring(clock, JwtTokenService.VisitorTokenLifetime, keys);

    private static VisitorSigningKeyRing Ring(
        IClock clock, TimeSpan retirementDelay, params VisitorSigningKeyEntry[] keys) =>
        new(new VisitorSigningKeyOptions { RetirementDelay = retirementDelay, Keys = keys }, clock);

    /// <summary>Minted against the real clock on purpose - see this class's own remarks on the two
    /// clocks. Every token these tests produce is genuinely unexpired.</summary>
    private static string Mint(IVisitorSigningKeyRing ring) =>
        new JwtTokenService(ring, Issuer, new RealClock())
            .IssueVisitorToken(new VisitorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()));

    private static string? KeyIdOf(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token).Header.Kid;

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/visitor-only");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<IHost> BuildHostAsync(IVisitorSigningKeyRing signingKeys)
    {
        var hostBuilder = new HostBuilder().ConfigureWebHost(webHost =>
        {
            webHost.UseTestServer();
            webHost.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthorization();
                services.AddSingleton<IVisitorSigningKeyRing>(signingKeys);
                services.AddAuthentication().AddJwtBearer(JwtSchemes.Visitor, options =>
                    options.MapInboundClaims = false);

                // Transcribed from `Program.cs` including its *shape*, not only its values: the
                // validation parameters are attached through a second, service-provider-aware
                // `Configure` registered after `AddJwtBearer`, because the key ring is a dependency
                // and that overload has nowhere to resolve one from. Written this way deliberately -
                // an inline copy would pass even if that ordering silently did not apply in the real
                // host, which is the one part of this wiring a transcription could get wrong without
                // noticing.
                services
                    .AddOptions<JwtBearerOptions>(JwtSchemes.Visitor)
                    .Configure<IVisitorSigningKeyRing>((options, ring) =>
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true,
                            ValidIssuer = Issuer,
                            ValidateAudience = true,
                            ValidAudience = JwtSchemes.Visitor,
                            ValidateIssuerSigningKey = true,
                            // The one line this whole file exists to exercise. `IssuerSigningKey` -
                            // what `Program.cs` had before `17-03` - is a single key captured while
                            // the host is starting; a resolver is asked on every token, which is what
                            // lets a retired key leave the accepted set without a restart.
                            IssuerSigningKeyResolver = (_, _, _, _) => ring.ValidationKeys(),
                            ValidateLifetime = true,
                        });
            });
            webHost.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints
                    .MapGet("/visitor-only", (HttpContext context) => Results.Ok(context.User.GetVisitorId().Value))
                    .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = JwtSchemes.Visitor }));
            });
        });

        return await hostBuilder.StartAsync();
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    /// <summary>Fully qualified elsewhere in this project because
    /// `Microsoft.AspNetCore.Authentication` exposes its own obsolete <c>SystemClock</c>; named
    /// differently here so this file needs neither the qualification nor the explanation.</summary>
    private sealed class RealClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
