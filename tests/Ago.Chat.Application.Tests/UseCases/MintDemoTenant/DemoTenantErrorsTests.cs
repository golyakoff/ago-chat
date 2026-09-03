using Ago.Chat.Application.UseCases.MintDemoTenant;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Tests.UseCases.MintDemoTenant;

/// <summary>
/// `ago-root#347`: the round trip <see cref="DemoTenantErrors.TryGetRateLimitedRetryAfterSeconds"/>'s
/// own remarks promise - <c>RateLimited</c> writes a number into its message because
/// <see cref="Ago.Platform.Kernel.Error"/> has nowhere structured to put it, and this method reads it
/// back out for <c>DemoEndpoints</c>'s <c>Retry-After</c> header. If a future edit changes
/// <c>RateLimited</c>'s wording without keeping this pair in sync, this file is what turns that into a
/// build-time-adjacent test failure instead of a silently missing header.
/// </summary>
public class DemoTenantErrorsTests
{
    [Fact]
    public void TheSecondsRateLimitedWritesIntoItsMessage_ComeBackOutUnchanged()
    {
        var error = DemoTenantErrors.RateLimited(TimeSpan.FromSeconds(732));

        var seconds = DemoTenantErrors.TryGetRateLimitedRetryAfterSeconds(error);

        Assert.Equal(732, seconds);
    }

    [Fact]
    public void ARetryAfterUnderOneSecond_RoundsToZero_NotToNull()
    {
        // The boundary a truncating parse could get wrong silently: zero is a valid, meaningful
        // Retry-After ("effectively immediately"), not the same thing as "no number was found".
        var error = DemoTenantErrors.RateLimited(TimeSpan.FromMilliseconds(400));

        var seconds = DemoTenantErrors.TryGetRateLimitedRetryAfterSeconds(error);

        Assert.Equal(0, seconds);
    }

    [Fact]
    public void AnyOtherDemoTenantErrorCode_HasNoRetryAfterToRecover()
    {
        // demo.capacity_reached and the rest carry no "wait this long" promise at all - the cap does
        // not refill on a clock, so there is nothing here to parse and null (no header) is the honest
        // answer, not zero or a guess.
        var error = DemoTenantErrors.CapacityReached(50);

        var seconds = DemoTenantErrors.TryGetRateLimitedRetryAfterSeconds(error);

        Assert.Null(seconds);
    }

    [Fact]
    public void AnUnrelatedErrorCode_HasNoRetryAfterToRecover()
    {
        var error = new Error("Message.RateLimited", "Too many messages - retry after 12.0s.");

        var seconds = DemoTenantErrors.TryGetRateLimitedRetryAfterSeconds(error);

        Assert.Null(seconds);
    }
}
