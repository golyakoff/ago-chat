using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Dapper;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-78`'s own core mechanism, against a real Postgres: <see cref="ConversationAttachmentUploadGrantRepository"/>'s
/// atomic grant/revoke and its three-way outcome - "a visitor cannot obtain an upload slot for a
/// conversation with no grant... an operator can grant and revoke it" (the backlog item's own
/// Done-when), proven against real rows rather than the SQL text looking right. The identical shape
/// <see cref="ConversationBlockRepositoryTests"/> already establishes for its sibling interface, minus
/// that class's own audit-trail assertions - this item builds no
/// <c>conversation_block_records</c>-shaped table for itself
/// (<see cref="IConversationAttachmentUploadGrantRepository"/>'s own remarks).
/// </summary>
[Collection(PostgresCollection.Name)]
public class ConversationAttachmentUploadGrantRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private ConversationAttachmentUploadGrantRepository Repository => new(fixture.DataSource);

    private async Task<(SiteId SiteId, ConversationId ConversationId)> SeedConversation()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return (siteId, conversation.Id);
    }

    private async Task<(DateTimeOffset? GrantedAt, Guid? GrantedBy)> ReadCurrentStateAsync(ConversationId conversationId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var row = await connection.QuerySingleAsync<(DateTimeOffset? attachment_upload_granted_at, Guid? attachment_upload_granted_by)>(
            "select attachment_upload_granted_at, attachment_upload_granted_by from conversations where id = @id",
            new { id = conversationId.Value });
        return (row.attachment_upload_granted_at, row.attachment_upload_granted_by);
    }

    [Fact]
    public async Task GrantAsync_OnAnExistingUngrantedConversation_SetsBothColumns_AndReturnsApplied()
    {
        var (siteId, conversationId) = await SeedConversation();
        var grantedBy = new OperatorId(Guid.NewGuid());

        var outcome = await Repository.GrantAsync(conversationId, siteId, grantedBy, Now, CancellationToken.None);

        Assert.Equal(AttachmentUploadGrantOutcome.Applied, outcome);
        var (grantedAt, grantedByColumn) = await ReadCurrentStateAsync(conversationId);
        Assert.Equal(Now, grantedAt);
        Assert.Equal(grantedBy.Value, grantedByColumn);
    }

    [Fact]
    public async Task GrantAsync_OnAConversationThatIsAlreadyGranted_ReturnsAlreadyInState_AndChangesNothing()
    {
        var (siteId, conversationId) = await SeedConversation();
        var firstGrantedBy = new OperatorId(Guid.NewGuid());
        await Repository.GrantAsync(conversationId, siteId, firstGrantedBy, Now, CancellationToken.None);

        var secondOutcome = await Repository.GrantAsync(
            conversationId, siteId, new OperatorId(Guid.NewGuid()), Now.AddMinutes(5), CancellationToken.None);

        Assert.Equal(AttachmentUploadGrantOutcome.AlreadyInState, secondOutcome);
        var (grantedAt, grantedByColumn) = await ReadCurrentStateAsync(conversationId);
        // The original grant wins - a second, redundant request neither overwrites the timestamp nor
        // reassigns responsibility to whoever happened to ask twice.
        Assert.Equal(Now, grantedAt);
        Assert.Equal(firstGrantedBy.Value, grantedByColumn);
    }

    [Fact]
    public async Task GrantAsync_OnAConversationThatDoesNotExist_ReturnsNotFound()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var missingConversationId = new ConversationId(Guid.NewGuid());

        var outcome = await Repository.GrantAsync(
            missingConversationId, siteId, new OperatorId(Guid.NewGuid()), Now, CancellationToken.None);

        Assert.Equal(AttachmentUploadGrantOutcome.NotFound, outcome);
    }

    // The same not-found-not-forbidden cross-tenant guard ConversationBlockRepositoryTests' own
    // remarks establish - existence is never leaked cross-tenant.
    [Fact]
    public async Task GrantAsync_WhenTheConversationBelongsToADifferentSite_ReturnsNotFound()
    {
        var (_, conversationId) = await SeedConversation();
        var otherSiteId = new SiteId(Guid.NewGuid());

        var outcome = await Repository.GrantAsync(
            conversationId, otherSiteId, new OperatorId(Guid.NewGuid()), Now, CancellationToken.None);

        Assert.Equal(AttachmentUploadGrantOutcome.NotFound, outcome);
    }

    [Fact]
    public async Task RevokeAsync_OnAGrantedConversation_ClearsBothColumns_AndReturnsApplied()
    {
        var (siteId, conversationId) = await SeedConversation();
        await Repository.GrantAsync(conversationId, siteId, new OperatorId(Guid.NewGuid()), Now, CancellationToken.None);

        var outcome = await Repository.RevokeAsync(
            conversationId, siteId, new OperatorId(Guid.NewGuid()), Now.AddHours(1), CancellationToken.None);

        Assert.Equal(AttachmentUploadGrantOutcome.Applied, outcome);
        var (grantedAt, grantedByColumn) = await ReadCurrentStateAsync(conversationId);
        Assert.Null(grantedAt);
        Assert.Null(grantedByColumn);
    }

    [Fact]
    public async Task RevokeAsync_OnAConversationThatIsNotGranted_ReturnsAlreadyInState()
    {
        var (siteId, conversationId) = await SeedConversation();

        var outcome = await Repository.RevokeAsync(
            conversationId, siteId, new OperatorId(Guid.NewGuid()), Now, CancellationToken.None);

        Assert.Equal(AttachmentUploadGrantOutcome.AlreadyInState, outcome);
    }

    [Fact]
    public async Task RevokeAsync_OnAConversationThatDoesNotExist_ReturnsNotFound()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var missingConversationId = new ConversationId(Guid.NewGuid());

        var outcome = await Repository.RevokeAsync(
            missingConversationId, siteId, new OperatorId(Guid.NewGuid()), Now, CancellationToken.None);

        Assert.Equal(AttachmentUploadGrantOutcome.NotFound, outcome);
    }

    // `23-78`: revoking a grant that came from the tenant-level default (no operator behind it -
    // `Conversation.Start`'s own remarks) is not a special case for this repository - the `UPDATE`
    // clears both columns regardless of whether `attachment_upload_granted_by` was ever populated.
    [Fact]
    public async Task RevokeAsync_OnAConversationGrantedWithNoOperatorAttribution_StillClearsBothColumns()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(
            new ConversationId(Guid.NewGuid()), siteId, visitorId, Now, attachmentUploadGrantedByDefault: true);

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        var outcome = await Repository.RevokeAsync(
            conversation.Id, siteId, new OperatorId(Guid.NewGuid()), Now.AddHours(1), CancellationToken.None);

        Assert.Equal(AttachmentUploadGrantOutcome.Applied, outcome);
        var (grantedAt, grantedByColumn) = await ReadCurrentStateAsync(conversation.Id);
        Assert.Null(grantedAt);
        Assert.Null(grantedByColumn);
    }
}
