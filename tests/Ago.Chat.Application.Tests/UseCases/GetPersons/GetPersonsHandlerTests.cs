using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetPersons;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetPersons;

/// <summary>`adr/0184` decision 4: the registry's display-only read - what the consoles merge onto the
/// calendar's own booking rows by person id.</summary>
public class GetPersonsHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        GetPersonsHandler Handler, FakeVisitorRepository Visitors, FakeVisitorContactDetailRepository Details);

    private static Fixture CreateFixture(bool grantPermission = true, ContactVisibility visibility = ContactVisibility.Visible)
    {
        var visitors = new FakeVisitorRepository();
        var details = new FakeVisitorContactDetailRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        }

        var sites = new FakeSiteRepository();
        var site = new Site(SiteId, "shop", ["https://shop.test"], "Shop", Now);
        if (visibility != ContactVisibility.Visible)
        {
            site.UpdateContactVisibility(visibility, Now);
        }

        sites.Seed(site);

        var handler = new GetPersonsHandler(visitors, details, permissions, new GetSiteConfigByIdHandler(sites, new FakeCache()));
        return new Fixture(handler, visitors, details);
    }

    private static Visitor APerson(FakeVisitorRepository visitors, SiteId siteId, string? name, params string[] phones)
    {
        var visitor = new Visitor(new VisitorId(Guid.NewGuid()), siteId, Now);
        visitors.Seed(visitor);
        return visitor;
    }

    [Fact]
    public async Task HandleAsync_ReturnsTheMostRecentNameAndEveryChannel_ForEachPersonNamed()
    {
        var fixture = CreateFixture();
        var anna = APerson(fixture.Visitors, SiteId, null);
        fixture.Details.Seed(VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), anna.Id, VisitorContactDetailKind.Name, "Ann", Now));
        fixture.Details.Seed(VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), anna.Id, VisitorContactDetailKind.Name, "Anna Petrova", Now.AddMinutes(1)));
        fixture.Details.Seed(VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), anna.Id, VisitorContactDetailKind.Phone, "+79990000001", Now));
        var nameless = APerson(fixture.Visitors, SiteId, null);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetPersons.GetPersons(SiteId, OperatorId, [anna.Id, nameless.Id]), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value.Count);

        var annaProfile = Assert.Single(result.Value, p => p.PersonId == anna.Id.Value);
        // The most recently recorded name wins - and the name is never listed as a channel.
        Assert.Equal("Anna Petrova", annaProfile.DisplayName);
        var channel = Assert.Single(annaProfile.Channels);
        Assert.Equal("Phone", channel.Kind);
        Assert.Equal("+79990000001", channel.Value);
        Assert.False(channel.Masked);

        var namelessProfile = Assert.Single(result.Value, p => p.PersonId == nameless.Id.Value);
        Assert.Null(namelessProfile.DisplayName);
        Assert.Empty(namelessProfile.Channels);
    }

    /// <summary>Tenant isolation by construction: another account's person is simply absent from the
    /// answer, exactly as an unknown id is - never an error that confirms the id exists.</summary>
    [Fact]
    public async Task HandleAsync_OmitsAnotherAccountsPerson_AndAnUnknownId_Silently()
    {
        var fixture = CreateFixture();
        var mine = APerson(fixture.Visitors, SiteId, null);
        var theirs = APerson(fixture.Visitors, OtherSiteId, null);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetPersons.GetPersons(SiteId, OperatorId, [mine.Id, theirs.Id, new VisitorId(Guid.NewGuid())]),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(mine.Id.Value, Assert.Single(result.Value).PersonId);
    }

    /// <summary>The account's own rung masks the channels here exactly as it masks them on the
    /// conversation panel - a name read by person id must never reveal what a masked panel hides.</summary>
    [Fact]
    public async Task HandleAsync_OnTheMaskedRung_MasksEveryChannel_ButNeverTheName()
    {
        var fixture = CreateFixture(visibility: ContactVisibility.MaskedWithReveal);
        var anna = APerson(fixture.Visitors, SiteId, null);
        fixture.Details.Seed(VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), anna.Id, VisitorContactDetailKind.Name, "Anna", Now));
        fixture.Details.Seed(VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), anna.Id, VisitorContactDetailKind.Phone, "+79990000001", Now));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetPersons.GetPersons(SiteId, OperatorId, [anna.Id]), CancellationToken.None);

        var profile = Assert.Single(result.Value);
        Assert.Equal("Anna", profile.DisplayName);
        var channel = Assert.Single(profile.Channels);
        Assert.True(channel.Masked);
        Assert.NotEqual("+79990000001", channel.Value);
        Assert.StartsWith("+7", channel.Value, StringComparison.Ordinal);
        Assert.EndsWith("01", channel.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);
        var anna = APerson(fixture.Visitors, SiteId, null);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetPersons.GetPersons(SiteId, OperatorId, [anna.Id]), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_MoreIdsThanOneScreenCouldDraw_IsRefused()
    {
        var fixture = CreateFixture();
        var ids = Enumerable.Range(0, GetPersonsHandler.MaxIds + 1).Select(_ => new VisitorId(Guid.NewGuid())).ToList();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetPersons.GetPersons(SiteId, OperatorId, ids), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Person.TooManyIds", result.Error!.Value.Code);
    }
}
