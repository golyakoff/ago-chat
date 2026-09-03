using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `ago-root#353`: <see cref="ErrorExtensions.ToProblem"/>'s new <c>retryAfter</c> parameter, at the
/// same level `DemoEndpointErrorStatusMappingTests` already tests this method's status-code mapping -
/// no handler, no hosting pipeline, since the thing under test is the header-writing itself, not any
/// of the business rules that decide a wait.
/// </summary>
public sealed class ErrorExtensionsRetryAfterTests
{
    [Fact]
    public async Task ToProblem_NoRetryAfterGiven_SetsNoHeader()
    {
        var header = await InvokeAsync(ConversationErrors.RateLimited(TimeSpan.FromSeconds(5)), retryAfter: null);

        Assert.True(string.IsNullOrEmpty(header));
    }

    [Fact]
    public async Task ToProblem_RetryAfterGiven_SetsTheHeaderAsWholeDeltaSeconds()
    {
        var header = await InvokeAsync(ConversationErrors.RateLimited(TimeSpan.FromSeconds(5)), TimeSpan.FromSeconds(120));

        Assert.Equal("120", header);
    }

    [Fact]
    public async Task ToProblem_RetryAfterHasAFraction_RoundsUpRatherThanDown()
    {
        // 90.1s must not become "90" - a caller that retried at 90s exactly would still be too early.
        var header = await InvokeAsync(ConversationErrors.RateLimited(TimeSpan.FromSeconds(5)), TimeSpan.FromSeconds(90.1));

        Assert.Equal("91", header);
    }

    [Fact]
    public async Task ToProblem_RetryAfterIsUnderOneSecond_ClampsToOneRatherThanZero()
    {
        // A `0` (or negative) Retry-After reads as "retry immediately" to a real client
        // (VisitorSessionRenewalTests' own reason for treating `0` as a bug) - this item's own trap to
        // avoid, restated as a unit test on the exact clamp rather than trusted by inspection alone.
        var header = await InvokeAsync(ConversationErrors.RateLimited(TimeSpan.FromSeconds(5)), TimeSpan.FromMilliseconds(200));

        Assert.Equal("1", header);
    }

    [Fact]
    public async Task ToProblem_RetryAfterIsExactlyZero_ClampsToOne()
    {
        var header = await InvokeAsync(ConversationErrors.RateLimited(TimeSpan.FromSeconds(5)), TimeSpan.Zero);

        Assert.Equal("1", header);
    }

    private static async Task<string?> InvokeAsync(Ago.Platform.Kernel.Error error, TimeSpan? retryAfter)
    {
        // Result.ExecuteAsync (ProblemHttpResult included) resolves services off
        // HttpContext.RequestServices to serialize the response - DefaultHttpContext leaves it null by
        // default, since this is normally supplied by the real ASP.NET Core pipeline
        // (DemoEndpointErrorStatusMappingTests' own precedent for this exact minimal set).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new JsonOptions()));
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };

        var result = error.ToProblem(httpContext, retryAfter);
        await result.ExecuteAsync(httpContext);

        return httpContext.Response.Headers.RetryAfter.FirstOrDefault();
    }
}
