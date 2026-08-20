using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests;

/// <summary>
/// date-and-time.md: "at least one test runs across a DST boundary in a non-UTC zone... if that test
/// never existed, the code has not been proven." The instant below is the 2026 spring-forward for
/// Europe/Berlin: local clocks skip 02:00-03:00 CET, both mapping to the same 01:00 UTC. Application
/// never converts to a local zone (that happens at the edge, per the same doc) - which is exactly what
/// this proves: stepping straight through that UTC instant is a non-event, because nothing here reads
/// a local hour. A `DateTime`-based implementation could duplicate or lose this hour; a
/// `DateTimeOffset` one, driven entirely by a fake clock, cannot.
/// </summary>
public class ClockBoundaryTests
{
    private static readonly DateTimeOffset BeforeBerlinSpringForward =
        new(2026, 3, 29, 0, 55, 0, TimeSpan.Zero); // 01:55 CET local
    private static readonly DateTimeOffset AfterBerlinSpringForward =
        new(2026, 3, 29, 1, 5, 0, TimeSpan.Zero); // 03:05 CEST local - 02:xx never happens there

    [Fact]
    public async Task StartConversation_SteppingTheClockAcrossTheDstInstant_KeepsTimestampsMonotonicInUtc()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var visitors = new FakeVisitorRepository();
        var conversations = new FakeConversationRepository();
        var clock = new FakeClock(BeforeBerlinSpringForward);
        var handler = new StartConversationHandler(visitors, conversations, clock, new FakeIdGenerator());

        await handler.HandleAsync(new StartConversation(siteId, visitorId), CancellationToken.None);
        var afterFirstContact = await visitors.GetByIdAsync(visitorId, CancellationToken.None);

        clock.UtcNow = AfterBerlinSpringForward;
        await handler.HandleAsync(new StartConversation(siteId, visitorId), CancellationToken.None);
        var afterReturnVisit = await visitors.GetByIdAsync(visitorId, CancellationToken.None);

        Assert.NotNull(afterFirstContact);
        Assert.NotNull(afterReturnVisit);
        Assert.Equal(BeforeBerlinSpringForward, afterFirstContact.FirstSeenAt);
        Assert.Equal(AfterBerlinSpringForward, afterReturnVisit.LastSeenAt);
        Assert.Equal(TimeSpan.FromMinutes(10), afterReturnVisit.LastSeenAt - afterFirstContact.FirstSeenAt);
    }
}
