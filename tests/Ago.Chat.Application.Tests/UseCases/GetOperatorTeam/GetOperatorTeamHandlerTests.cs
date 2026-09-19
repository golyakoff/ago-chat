using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetOperatorTeam;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetOperatorTeam;

public class GetOperatorTeamHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId RequestedBy = new(Guid.NewGuid());

    private sealed record Fixture(GetOperatorTeamHandler Handler, FakeOperatorTeamReadStore Team, FakePermissionChecker Permissions);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var team = new FakeOperatorTeamReadStore();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(RequestedBy, SiteId, Permission.SiteManageOperators);
        }

        var handler = new GetOperatorTeamHandler(team, permissions);
        return new Fixture(handler, team, permissions);
    }

    [Fact]
    public async Task HandleAsync_WhenCallerLacksPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetOperatorTeam.GetOperatorTeam(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_ReturnsEveryActiveOperator_ByNameAndSeatStatus()
    {
        var fixture = CreateFixture();
        var named = new OperatorId(Guid.NewGuid());
        var unnamed = new OperatorId(Guid.NewGuid());
        fixture.Team.Seed(
            SiteId,
            new OperatorTeamMemberItem(named, "Ada Lovelace", "ada@example.invalid", [new OperatorRoleSeatAssignment("Admin", true)]),
            new OperatorTeamMemberItem(unnamed, null, null, [new OperatorRoleSeatAssignment("Operator", false)]));

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetOperatorTeam.GetOperatorTeam(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Operators.Count);

        var adaRow = Assert.Single(result.Value.Operators, o => o.OperatorId == named.Value);
        Assert.Equal("Ada Lovelace", adaRow.DisplayName);
        Assert.Equal("ada@example.invalid", adaRow.Email);
        // `23-72`/`25-170`: Roles joined the wire shape - the console team screen needs it to show who
        // already administers and whether each role's own seat is held.
        var adaAdminRole = Assert.Single(adaRow.Roles);
        Assert.Equal("Admin", adaAdminRole.RoleName);
        Assert.True(adaAdminRole.HoldsSeat);

        var unnamedRow = Assert.Single(result.Value.Operators, o => o.OperatorId == unnamed.Value);
        Assert.Null(unnamedRow.DisplayName);
        Assert.Null(unnamedRow.Email);
        var unnamedOperatorRole = Assert.Single(unnamedRow.Roles);
        Assert.Equal("Operator", unnamedOperatorRole.RoleName);
        Assert.False(unnamedOperatorRole.HoldsSeat);
    }

    [Fact]
    public async Task HandleAsync_QueriesTheReadStoreForTheRequestedSiteOnly()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(new Application.UseCases.GetOperatorTeam.GetOperatorTeam(RequestedBy, SiteId), CancellationToken.None);

        Assert.Equal(SiteId, fixture.Team.LastSiteId);
    }
}
