using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.AiAddOn;
using Ago.Chat.Application.UseCases.CategorizeConversation;
using Ago.Chat.Application.UseCases.GenerateReplyDraft;
using Ago.Chat.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.AiAddOn;

/// <summary>
/// `25-04`'s first Done-when, both call sites: <b>with the module disabled - which is every tenant
/// until they act - nothing reaches the vendor, proven by a test that fails if the client is
/// constructed at all.</b>
///
/// <para><b>Why <see cref="Lazy{T}.IsValueCreated"/> rather than a call count.</b> A fake with a call
/// count has already been constructed by the time the assertion runs, so a handler that resolved the
/// real YandexGPT client, opened its typed <c>HttpClient</c> and then decided not to call it would pass
/// such a test while doing exactly the thing this item exists to prevent. The factories below throw,
/// and the assertions check the value was never created - so these tests fail on *construction*, which
/// is the bar the item actually set.</para>
/// </summary>
public sealed class NothingReachesTheVendorWhenDisabledTests
{
    private static readonly SiteId Site = new(Guid.NewGuid());
    private static readonly VisitorId Visitor = new(Guid.NewGuid());
    private static readonly OperatorId Operator = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReplyDraft_ForATenantWithoutTheAddOn_NeverConstructsTheGenerator()
    {
        var conversations = new FakeConversationRepository();
        var readStore = new FakeConversationReadStore();
        var permissions = new FakePermissionChecker();

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), Site, Visitor, Now);
        conversation.AssignTo(Operator, Now);
        conversation.AddVisitorMessage(Visitor, new MessageId(Guid.NewGuid()), new MessageBody("do you ship to Kazan?"), Now);
        conversations.Seed(conversation);
        readStore.Seed(conversation);
        permissions.Grant(Operator, Site, Permission.ConversationSend);

        var generator = new Lazy<IReplyDraftGenerator>(ThrowingReplyDraftGenerator);
        var handler = new GenerateReplyDraftHandler(
            conversations, readStore, permissions, new FakeRateLimiter(), generator, AiGates.Denying(),
            new ReplyDraftOptions(), new ReplyDraftRateLimitOptions());

        var result = await handler.HandleAsync(
            new GenerateReplyDraftAsOperator(conversation.Id, Operator, Site), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.False(generator.IsValueCreated);
    }

    [Fact]
    public async Task Categorisation_ForATenantWithoutTheAddOn_NeverConstructsTheCategorizer()
    {
        var readStore = new FakeConversationReadStore();
        var tags = new FakeTagRepository();

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), Site, Visitor, Now);
        conversation.AddVisitorMessage(Visitor, new MessageId(Guid.NewGuid()), new MessageBody("do you ship to Kazan?"), Now);
        conversation.AssignTo(Operator, Now);
        conversation.Close(Now.AddMinutes(5));
        readStore.Seed(conversation);
        tags.Seed(Tag.Create(new TagId(Guid.NewGuid()), Site, "Billing", Now));

        var categorizer = new Lazy<IConversationCategorizer>(ThrowingCategorizer);
        var handler = new CategorizeConversationHandler(
            readStore, tags, categorizer, AiGates.Denying(), new CategorizationOptions(),
            NullLogger<CategorizeConversationHandler>.Instance);

        var result = await handler.HandleAsync(new global::Ago.Chat.Application.UseCases.CategorizeConversation.CategorizeConversation(conversation.Id, Site), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(CategorizationOutcome.NotPermitted, result.Value);
        Assert.False(categorizer.IsValueCreated);
        Assert.Empty(await tags.GetForConversationAsync(conversation.Id, CancellationToken.None));
    }

    /// <summary>The cut-off, at the handler: the add-on is bought and on, but this conversation predates
    /// it - and the categorizer is still never constructed.</summary>
    [Fact]
    public async Task Categorisation_ForAConversationCreatedBeforeTheCutOff_NeverConstructsTheCategorizer()
    {
        var readStore = new FakeConversationReadStore();
        var tags = new FakeTagRepository();

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), Site, Visitor, Now);
        conversation.AddVisitorMessage(Visitor, new MessageId(Guid.NewGuid()), new MessageBody("older than the add-on"), Now);
        conversation.AssignTo(Operator, Now);
        conversation.Close(Now.AddMinutes(5));
        readStore.Seed(conversation);
        tags.Seed(Tag.Create(new TagId(Guid.NewGuid()), Site, "Billing", Now));

        var categorizer = new Lazy<IConversationCategorizer>(ThrowingCategorizer);
        var handler = new CategorizeConversationHandler(
            readStore, tags, categorizer, AiGates.Allowing(Site, effectiveFrom: Now.AddHours(1)),
            new CategorizationOptions(), NullLogger<CategorizeConversationHandler>.Instance);

        var result = await handler.HandleAsync(new global::Ago.Chat.Application.UseCases.CategorizeConversation.CategorizeConversation(conversation.Id, Site), CancellationToken.None);

        Assert.Equal(CategorizationOutcome.NotPermitted, result.Value);
        Assert.False(categorizer.IsValueCreated);
    }

    private static IReplyDraftGenerator ThrowingReplyDraftGenerator() =>
        throw new InvalidOperationException(
            "The reply-draft generator must never be constructed for a tenant the AI add-on gate refuses.");

    private static IConversationCategorizer ThrowingCategorizer() =>
        throw new InvalidOperationException(
            "The conversation categorizer must never be constructed for a tenant the AI add-on gate refuses.");
}
