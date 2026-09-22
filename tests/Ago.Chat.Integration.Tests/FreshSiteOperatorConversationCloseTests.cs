using Ago.Chat.Application.UseCases.CloseConversation;
using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-69`: the fresh-site proof its own Done-when box asks for - "not just the one live account this
/// was found on." Every other test that exercises `CloseConversationHandler`
/// (<see cref="CloseConversationOutboxTests"/>) hand-seeds a <c>RoleRecord</c> with
/// <see cref="Permission.ConversationClose"/> already in it, which proves the handler and
/// <c>PermissionChecker</c> work correctly once the permission is granted, but never proves that a
/// site registered the ordinary way (<see cref="RegisterSiteHandler"/>, `10-02`'s real path - the same
/// one <see cref="SelfRegisteredSiteOriginTests"/> calls directly against real Postgres, no Keycloak
/// container needed) actually grants it. Before this item, it did not: neither
/// <c>RegisterSiteHandler.OperatorRolePermissions</c> nor <c>MintDemoTenantHandler</c>'s own copy
/// carried <see cref="Permission.ConversationClose"/>, so this exact test would have failed
/// `Conversation.Forbidden` against the unfixed seed array - the reproduction this class exists to
/// keep in place, not only the fix.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FreshSiteOperatorConversationCloseTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task FreshlyRegisteredSite_OperatorHoldingTheSeededOperatorRole_CanCloseAConversationItIsAssignedTo()
    {
        var origin = $"https://{Guid.NewGuid():N}.example.com";
        var operatorId = await RegisterSiteAndReturnOperatorIdAsync(origin);

        var siteId = await GetSiteIdForOperatorAsync(operatorId);
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));

            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
            // visitor's own real first message before AssignTo, which still only accepts Waiting.
            conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
            conversation.AssignTo(operatorId, Now);
            conversation.ClearDomainEvents();
            seed.Conversations.Add(conversation);

            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var db = fixture.CreateDbContext();
        var handler = new CloseConversationHandler(
            new ConversationRepository(db),
            new ConversationAssignmentLog(db),
            new PermissionChecker(db),
            new OperatorCapacityStore(db),
            new EfOutboxWriter<AgoChatDbContext>(db),
            new UuidV7Generator(),
            new SystemClock(),
            NullLogger<CloseConversationHandler>.Instance);

        // The real application path, exactly as a console request would call it - no permission is
        // hand-seeded here. Whatever role RegisterSiteHandler actually stamped for this fresh
        // "Operator" row is what gets checked.
        var result = await handler.HandleAsync(
            new CloseConversation(conversationId, operatorId, siteId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Code : null);

        await using var verify = fixture.CreateDbContext();
        var conversationRow = await verify.Conversations.SingleAsync(c => c.Id == conversationId, CancellationToken.None);
        Assert.Equal(ConversationState.Closed, conversationRow.State);
    }

    private async Task<OperatorId> RegisterSiteAndReturnOperatorIdAsync(string origin)
    {
        var registrationDb = fixture.CreateDbContext();
        var handler = new RegisterSiteHandler(
            new SiteRegistrationRepository(
                registrationDb, new EfOutboxWriter<AgoChatDbContext>(registrationDb), new UuidV7Generator(), new SystemClock()),
            // `24-03`: real, empty-by-default port - no `required_documents` row exists in this
            // fixture's database, so this resolves to zero required keys, the same reasoning
            // SelfRegisteredSiteOriginTests' own RegisterSiteAsync gives for the identical setup.
            new RequiredDocumentRepository(registrationDb, new UuidV7Generator(), new SystemClock()),
            new DocumentRepository(registrationDb),
            new FakeRateLimiter(),
            new RegisterSiteRateLimitOptions(),
            new UuidV7Generator(),
            new SystemClock());

        var command = new RegisterSite(
            ExternalSubjectId: $"keycloak-sub-{Guid.NewGuid():N}", RequestIp: "203.0.113.9", SiteName: "Freshly Registered", origin);

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Code : null);

        return new OperatorId(result.Value.OperatorId);
    }

    private async Task<SiteId> GetSiteIdForOperatorAsync(OperatorId operatorId)
    {
        await using var db = fixture.CreateDbContext();
        var operatorRow = await db.Operators.SingleAsync(o => o.Id == operatorId, CancellationToken.None);
        return operatorRow.SiteId;
    }
}
