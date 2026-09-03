using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.MintDemoTenant;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `ago-root#352`: `ErrorExtensions`' switch had no `demo.*` case at all until `#347` added
/// `demo.rate_limited`; these four still fell through to its `_ =&gt; 500` default. A deliberate refusal
/// answering `500` is indistinguishable from a fault to anything that reads status codes - and no
/// functional test would ever notice, because the request *is* refused either way, correctly, regardless
/// of which number rides along with it. These tests exercise <see cref="ErrorExtensions.ToProblem"/>
/// directly, the same level `DemoEndpointRateLimitTests` already tests `demo.rate_limited` at - no
/// handler, no hosting pipeline, since the thing under test is the mapping itself, not any of the
/// business rules that produce these codes.
///
/// <para><b>demo.unavailable's own test is a pin, not a bug fix.</b> That code was already landing on
/// `500` through the switch's `_` default before this change - <see cref="ErrorExtensions"/>' own remarks
/// explain why it stays there deliberately. Its test therefore passes against `main` as well as against
/// this change; what it guards against is a future edit accidentally moving `demo.unavailable` off `500`
/// without that being a deliberate decision, not a status code this ticket had to fix.</para>
/// </summary>
public sealed class DemoEndpointErrorStatusMappingTests
{
    [Fact]
    public async Task ToProblem_DemoDisabled_Returns501NotImplemented()
    {
        await AssertMapsToAsync(DemoTenantErrors.Disabled(), StatusCodes.Status501NotImplemented);
    }

    [Fact]
    public async Task ToProblem_DemoCapacityReached_Returns503ServiceUnavailable()
    {
        await AssertMapsToAsync(DemoTenantErrors.CapacityReached(maxLiveTenants: 10), StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task ToProblem_DemoIdentityRejected_Returns503ServiceUnavailable()
    {
        await AssertMapsToAsync(DemoTenantErrors.IdentityRejected("409"), StatusCodes.Status503ServiceUnavailable);
    }

    // Deliberately still 500 - see this class's own remarks above.
    [Fact]
    public async Task ToProblem_DemoUnavailable_Returns500InternalServerError()
    {
        await AssertMapsToAsync(DemoTenantErrors.Unavailable(), StatusCodes.Status500InternalServerError);
    }

    private static async Task AssertMapsToAsync(Error error, int expectedStatusCode)
    {
        // Result.ExecuteAsync (ProblemHttpResult included) resolves services off
        // HttpContext.RequestServices to serialize the response - DefaultHttpContext leaves it null by
        // default, since this is normally supplied by the real ASP.NET Core pipeline
        // (DemoEndpointRateLimitTests' own precedent for this exact minimal set).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new JsonOptions()));
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };

        var result = error.ToProblem(httpContext);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(expectedStatusCode, httpContext.Response.StatusCode);

        // `type`/`title` are API contract (api-design.md) - proves the code itself still rides along
        // unchanged, not only the status.
        httpContext.Response.Body.Position = 0;
        var body = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
        Assert.Contains($"\"{error.Code}\"", body, StringComparison.Ordinal);
    }
}
