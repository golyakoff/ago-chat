using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.GetVisitorPresence;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `17-01`: conversation access across tenants, against a real Postgres and the real
/// <c>PermissionChecker</c> reading real <c>roles</c>/<c>operator_roles</c> - not
/// <c>FakePermissionChecker</c>, whose answers are whatever the test decided they should be.
///
/// <para><b>This file exists because of a real hole, not as a formality.</b> Until `17-01`,
/// <c>AssignConversationHandler</c> checked <c>conversation:assign</c> against the site on the
/// caller's own token and then assigned whatever conversation id it was handed, never comparing the
/// two. An operator of site B could therefore claim any <em>Waiting</em> conversation of site A, and
/// - because every other operator-facing path gates on being the conversation's <em>assigned</em>
/// operator rather than on its site - would then legitimately pass all of them. The chain is what
/// made it serious, so the chain is what is tested here: the claim is refused, and the reads that
/// would have followed it are refused too.</para>
///
/// <para><b>Level.</b> Handlers composed with the real repositories and the real permission checker,
/// not driven through <c>OperatorHub</c>. The hub adds exactly one thing to this path - reading
/// <c>OperatorId</c>/<c>SiteId</c> off the connection's validated token, which
/// <see cref="CrossTenantRouteIsolationTests"/> exercises for real over HTTP - and driving SignalR
/// would buy no additional coverage of the decision under test while making the failure mode a
/// <c>HubException</c> string instead of a typed <c>Result</c>.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class CrossTenantConversationAccessTests(PostgresFixture fixture)
{
    // The current instant, truncated to a whole second - not a fixed 2026-01-01, which `messages`
    // has no monthly partition for (`2-06`): only the current month's exists on a freshly migrated
    // database, and nothing here depends on the value being pinned.
    private static readonly DateTimeOffset Now =
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    /// <param name="OperatorId">An operator of <paramref name="AttackerSiteId"/> holding every
    /// conversation permission there is - for that site.</param>
    /// <param name="ConversationId">A <c>Waiting</c> conversation belonging to
    /// <paramref name="VictimSiteId"/>, with one visitor message already in it.</param>
    private sealed record Scenario(
        OperatorId OperatorId, SiteId AttackerSiteId, SiteId VictimSiteId,
        ConversationId ConversationId, VisitorId VictimVisitorId);

    [Fact]
    public async Task TheOperatorReallyHoldsEveryConversationPermission_OnTheirOwnSite()
    {
        var scenario = await SetUpAsync();

        await using var db = fixture.CreateDbContext();
        var checker = new PermissionChecker(db);

        foreach (var permission in new[] { Permission.ConversationAssign, Permission.ConversationRead, Permission.ConversationSend })
        {
            Assert.True(
                await checker.HasPermissionAsync(scenario.OperatorId, scenario.AttackerSiteId, permission, CancellationToken.None),
                $"the caller must genuinely hold {permission.Value} on their own site");
            Assert.False(
                await checker.HasPermissionAsync(scenario.OperatorId, scenario.VictimSiteId, permission, CancellationToken.None),
                $"the caller must hold {permission.Value} on no other site");
        }
    }

    /// <summary>
    /// The hole itself. Before `17-01` this assignment <b>succeeded</b>: the permission check passes
    /// (it is scoped to the caller's own site, which is not the conversation's) and nothing else
    /// looked at where the conversation lives.
    /// </summary>
    [Fact]
    public async Task AnOperatorOfAnotherSite_CannotClaimAWaitingConversation()
    {
        var scenario = await SetUpAsync();

        await using var db = fixture.CreateDbContext();
        var result = await new AssignConversationHandler(
                new ConversationRepository(db), new ConversationAssignmentLog(db), new PermissionChecker(db),
                new OperatorCapacityStore(db), new EfUnitOfWork(db), new UuidV7Generator(), new Ago.Platform.Hosting.SystemClock())
            .HandleAsync(
                new AssignConversation(scenario.ConversationId, scenario.OperatorId, scenario.AttackerSiteId),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);

        // Not merely refused - untouched, and therefore still claimable by the tenant it belongs to.
        // A refusal that had already flipped the row to Assigned would deny the victim their own
        // conversation just as effectively as a successful hijack.
        await using var freshDb = fixture.CreateDbContext();
        var conversation = await new ConversationRepository(freshDb)
            .GetByIdAsync(scenario.ConversationId, CancellationToken.None);
        Assert.Equal(ConversationState.Waiting, conversation!.State);
        Assert.Null(conversation.OperatorId);
    }

    /// <summary>
    /// <b>The exploit, run in order.</b> The three reads below each gate on
    /// <c>conversation.OperatorId == RequestedBy</c> and nothing else, so before `17-01` every one of
    /// them answered "yes" once the claim on the first line had gone through - which is why the claim,
    /// and not each of these, is where the site comparison belongs. Written as one sequence rather
    /// than three independent refusals precisely so it reproduces the attack: a version of this test
    /// that skipped the claim would have passed against the broken code.
    /// </summary>
    [Fact]
    public async Task AnOperatorOfAnotherSite_ClaimsThenReads_AndIsRefusedAtEveryStep()
    {
        var scenario = await SetUpAsync();

        await using var db = fixture.CreateDbContext();
        var permissions = new PermissionChecker(db);
        var conversations = new ConversationRepository(db);
        var history = new GetConversationHistoryHandler(conversations, new ConversationReadStore(fixture.DataSource), permissions);

        // Step one of the real sequence: take the conversation. Everything after this depended on it.
        var claim = await new AssignConversationHandler(
                conversations, new ConversationAssignmentLog(db), permissions, new OperatorCapacityStore(db),
                new EfUnitOfWork(db), new UuidV7Generator(), new Ago.Platform.Hosting.SystemClock())
            .HandleAsync(
                new AssignConversation(scenario.ConversationId, scenario.OperatorId, scenario.AttackerSiteId),
                CancellationToken.None);
        Assert.True(claim.IsFailure);

        var page = await history.HandleAsOperatorAsync(
            new GetConversationHistoryAsOperator(scenario.ConversationId, scenario.OperatorId, scenario.AttackerSiteId, null, 50),
            CancellationToken.None);
        Assert.True(page.IsFailure);
        Assert.Equal("Conversation.Forbidden", page.Error!.Value.Code);

        var delta = await history.HandleDeltaAsOperatorAsync(
            new GetConversationDeltaAsOperator(scenario.ConversationId, scenario.OperatorId, scenario.AttackerSiteId, 0),
            CancellationToken.None);
        Assert.True(delta.IsFailure);

        var presence = await new GetVisitorPresenceHandler(conversations, permissions, new UnreachableConnectionRegistry()).HandleAsync(
            new GetVisitorPresence(scenario.ConversationId, scenario.OperatorId, scenario.AttackerSiteId),
            CancellationToken.None);
        Assert.True(presence.IsFailure);
    }

    /// <summary>
    /// `17-01`'s visitor-side Done-when, proven directly rather than inferred from ownership: a
    /// visitor token issued for one site cannot read another site's conversation.
    ///
    /// <para>The guarantee is not a site comparison at all - it is narrower. A visitor id is minted
    /// per session, by <c>AuthEndpoints</c>, already paired with the site the token is signed for
    /// (`17-06`/`adr/0034`), so a visitor cannot present an id that belongs to another tenant's
    /// conversation in the first place; the handler then compares that id against
    /// <c>conversation.VisitorId</c>. What this test pins down is that the comparison actually
    /// happens, against a real conversation on a real database, for a visitor who is otherwise
    /// entirely legitimate on their own site.</para>
    /// </summary>
    [Fact]
    public async Task AVisitorOfAnotherSite_CannotReadTheConversation()
    {
        var scenario = await SetUpAsync();
        var outsideVisitorId = new VisitorId(Guid.NewGuid());
        await using (var seedDb = fixture.CreateDbContext())
        {
            seedDb.Visitors.Add(new Visitor(outsideVisitorId, scenario.AttackerSiteId, Now));
            await seedDb.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var history = new GetConversationHistoryHandler(
            new ConversationRepository(db), new ConversationReadStore(fixture.DataSource), new PermissionChecker(db));

        var page = await history.HandleAsVisitorAsync(
            new GetConversationHistoryAsVisitor(scenario.ConversationId, outsideVisitorId, null, 50), CancellationToken.None);
        Assert.True(page.IsFailure);
        Assert.Equal("Conversation.Forbidden", page.Error!.Value.Code);

        var delta = await history.HandleDeltaAsVisitorAsync(
            new GetConversationDeltaAsVisitor(scenario.ConversationId, outsideVisitorId, 0), CancellationToken.None);
        Assert.True(delta.IsFailure);

        // The conversation's own visitor still reads it - so the refusal above is about *this*
        // visitor, not about the conversation being unreadable for some unrelated reason.
        var owner = await history.HandleAsVisitorAsync(
            new GetConversationHistoryAsVisitor(scenario.ConversationId, scenario.VictimVisitorId, null, 50),
            CancellationToken.None);
        Assert.True(owner.IsSuccess);
        Assert.Single(owner.Value.Messages);
    }

    /// <summary>Two unrelated tenants: one with a fully-permitted operator, one with a waiting
    /// conversation that has something in it worth stealing.</summary>
    private async Task<Scenario> SetUpAsync()
    {
        var attackerSiteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var victimSiteId = new SiteId(Guid.NewGuid());
        var victimVisitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(attackerSiteId, $"site_{attackerSiteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, attackerSiteId, OperatorStatus.Online, capacity: 5));
            var roleId = Guid.NewGuid();
            db.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = attackerSiteId,
                Name = "Operator",
                Permissions =
                [
                    Permission.ConversationRead.Value,
                    Permission.ConversationSend.Value,
                    Permission.ConversationAssign.Value,
                ],
            });
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });

            db.Sites.Add(new Site(victimSiteId, $"site_{victimSiteId.Value:N}", []));
            db.Visitors.Add(new Visitor(victimVisitorId, victimSiteId, Now));
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var conversation = Conversation.Start(conversationId, victimSiteId, victimVisitorId, Now);
            conversation.AddVisitorMessage(
                victimVisitorId, new MessageId(Guid.NewGuid()), new MessageBody("something the other tenant must not read"), Now);
            conversation.ClearDomainEvents();
            await new ConversationRepository(db).SaveAsync(conversation, CancellationToken.None);
        }

        return new Scenario(operatorId, attackerSiteId, victimSiteId, conversationId, victimVisitorId);
    }

    /// <summary>Presence itself is not what is under test - the refusal has to happen before the
    /// registry is ever consulted, so this asserts that rather than returning a convenient answer.
    /// Faking the registry (a Redis port, `adr/0009`) is legitimate here in a way faking the
    /// permission checker would not be: it is the thing being kept *out* of the call, not the thing
    /// making the decision.</summary>
    private sealed class UnreachableConnectionRegistry : Ago.Platform.Abstractions.IConnectionRegistry
    {
        public Task RegisterAsync(
            Ago.Platform.Abstractions.ConnectionId connectionId, Ago.Platform.Abstractions.NodeId nodeId,
            Ago.Platform.Abstractions.PrincipalKey principal, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UnregisterAsync(
            Ago.Platform.Abstractions.ConnectionId connectionId, Ago.Platform.Abstractions.NodeId nodeId,
            Ago.Platform.Abstractions.PrincipalKey principal, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveNodeAsync(Ago.Platform.Abstractions.NodeId nodeId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyCollection<Ago.Platform.Abstractions.RegisteredConnection>> GetConnectionsAsync(
            Ago.Platform.Abstractions.PrincipalKey principal, CancellationToken cancellationToken)
        {
            Assert.Fail("the connection registry must never be reached for a conversation the caller is not a party to");
            throw new InvalidOperationException("unreachable");
        }
    }
}
