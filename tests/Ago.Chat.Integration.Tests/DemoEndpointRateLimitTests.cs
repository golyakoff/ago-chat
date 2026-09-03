using System.Globalization;
using Ago.Chat.Api.Demo;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.MintDemoTenant;
using Ago.Chat.Domain;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `ago-root#347`: a deliberate refusal ("too many demo tenants requested from this address") must not
/// read as a server fault. Before this fix, exceeding the per-IP limit answered `500` - not because any
/// case in `ErrorExtensions`' switch mapped it there, but because no case named `demo.rate_limited` at
/// all, and every unmatched code falls through to that switch's own `_ => 500` default. A test that only
/// exercised the happy path, or that stopped at "the caller was refused", would never have noticed - the
/// refusal itself was already correct.
///
/// <para><b>No Testcontainers here on purpose.</b> <see cref="MintDemoTenantHandler"/> checks the per-IP
/// limit before touching Postgres or Keycloak (its own remarks: "a bad caller still costs them a token,
/// never costs us a query"), so a denied check never reaches any of its other four dependencies - each
/// is a throwing stub below, which proves that ordering as a side effect of the test passing at all.
/// Same shape as `RateLimitingTests.VisitorSessionEndpoint_..._Returns429WithARetryAfterHeader`: the
/// endpoint's own route-handler method, called directly with a `DefaultHttpContext`, no hosting pipeline
/// needed.</para>
/// </summary>
public sealed class DemoEndpointRateLimitTests
{
    [Fact]
    public async Task ExceedingThePerIpLimit_Returns429_WithARetryAfterHeaderMatchingTheDetail()
    {
        var handler = new MintDemoTenantHandler(
            new NeverCalledDemoTenantRepository(),
            new NeverCalledSiteRegistrationRepository(),
            new NeverCalledDemoIdentityProvisioner(),
            new NeverCalledDemoCredentialGenerator(),
            new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(732)),
            new DemoTenantOptions { Enabled = true, VisitorOrigin = "https://demo.example" },
            new DemoTenantRateLimitOptions(),
            new UuidV7Generator(),
            new SystemClock());

        // Result.ExecuteAsync (ProblemHttpResult included) resolves services off
        // HttpContext.RequestServices to serialize the response - DefaultHttpContext leaves it null by
        // default, since this is normally supplied by the real ASP.NET Core pipeline
        // (RateLimitingTests' own precedent for this exact minimal set).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new JsonOptions()));
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };

        var result = await DemoEndpoints.HandleMintAsync(handler, httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, httpContext.Response.StatusCode);

        var retryAfterHeader = httpContext.Response.Headers.RetryAfter.FirstOrDefault();
        Assert.False(string.IsNullOrEmpty(retryAfterHeader), "Retry-After header was not set.");
        Assert.True(
            int.TryParse(retryAfterHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var retryAfterSeconds),
            $"Retry-After ('{retryAfterHeader}') did not parse as a whole number of seconds.");

        // Not just "a" number - the same one the detail string already computes (the item's own
        // Done-when). A header that quietly disagreed with the prose would be worse than no header: a
        // caller who trusted it would retry at the wrong time.
        httpContext.Response.Body.Position = 0;
        var body = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
        Assert.Contains($"Retry after {retryAfterSeconds}s.", body, StringComparison.Ordinal);
        Assert.Contains("\"demo.rate_limited\"", body, StringComparison.Ordinal);
    }

    private sealed class NeverCalledDemoTenantRepository : IDemoTenantRepository
    {
        public Task<int> CountLiveAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The per-IP limit must refuse before the total cap is ever read.");

        public Task<IReadOnlyList<ExpiredDemoTenant>> ListExpiredAsync(
            DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of minting.");

        public Task DeleteSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of minting.");

        public Task<IReadOnlyList<string>> ListAttachmentObjectKeysAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of minting.");
    }

    private sealed class NeverCalledSiteRegistrationRepository : ISiteRegistrationRepository
    {
        public Task<bool> TryRegisterAsync(SiteRegistration registration, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rate-limited caller must never reach registration.");
    }

    private sealed class NeverCalledDemoIdentityProvisioner : IDemoIdentityProvisioner
    {
        public Task<Result<string>> CreateAsync(string username, string password, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rate-limited caller must never reach the identity provider.");

        public Task DeleteAsync(string subjectId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of minting.");
    }

    private sealed class NeverCalledDemoCredentialGenerator : IDemoCredentialGenerator
    {
        public string NewPassword() =>
            throw new InvalidOperationException("A rate-limited caller must never reach credential generation.");

        public string NewUsernameSuffix() =>
            throw new InvalidOperationException("A rate-limited caller must never reach credential generation.");
    }
}
