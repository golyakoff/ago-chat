namespace Ago.Chat.Domain;

/// <summary>
/// `14-09`: the one piece of state every other channel adapter has never needed - enough of an email
/// thread's own history to build <c>In-Reply-To</c>/<c>References</c> headers on an operator's reply, so
/// a visitor's own mail client threads the conversation instead of showing every reply as a new message.
/// No other channel has this problem: MAX/Telegram/VK/WhatsApp/Avito all reply to a chat id or a phone
/// number, and the provider's own app does the threading; email's threading is the *client's* job, driven
/// entirely by headers this system must set correctly on every outbound send
/// (`14-09`'s own backlog note names this as "a real, easy-to-get-wrong detail worth naming explicitly").
///
/// <para><b>Why this exists at all, and why <see cref="ExternalMessageId"/> could not simply be
/// reused.</b> Every other channel's own provider-message-id is used exactly once, to derive a
/// deterministic <c>ClientMessageId</c> hash (<see cref="ExternalMessageId.ToClientMessageId"/>),
/// and is then discarded - nothing about MAX's or VK's own outbound send needs the *specific* id of any
/// earlier message. Email is the first channel where the raw provider id (an RFC 5322 <c>Message-ID</c>
/// header, e.g. <c>&lt;abc123@mail.example&gt;</c>) has to be read back later, verbatim, to put on a
/// *different* message's own header. A one-way hash cannot support that - the same "reversible versus
/// one-way" split <see cref="ChannelCredential"/>'s own remarks draw between <see cref="ChannelCredential.TokenCiphertext"/>
/// and <see cref="ChannelCredential.WebhookSecretHash"/>, applied here to a plaintext id instead of a
/// secret.</para>
///
/// <para><b>Its own row, keyed by <see cref="ConversationId"/> directly - not a synthetic id, and not a
/// column bolted onto <see cref="Conversation"/>.</b> Unlike <see cref="ChannelCredential"/>/
/// <see cref="ChannelIdentity"/> (each addressed by its own id from a console endpoint or a webhook path
/// segment), nothing ever needs to name one of these rows on its own - the only lookup this type's own
/// repository ever does is "the thread state for conversation X", so <see cref="ConversationId"/> is
/// already the natural key and a second, unused id would be exactly the kind of gratuitous surface
/// `clean-architecture.md` warns against. Not folded onto <see cref="Conversation"/> itself for the
/// matching reason <see cref="VisitorContactDetail"/>'s own remarks give for its own table: this fact is
/// true of an email-channel conversation specifically, and every non-email conversation would carry three
/// permanently-null columns for a fact that will never apply to it.</para>
///
/// <para><b>Deliberately minimal <c>References</c> support - a real, named scope cut, not an
/// oversight.</b> RFC 5322 recommends <c>References</c> accumulate every prior <c>Message-ID</c> in the
/// thread; this type keeps only the first (<see cref="RootMessageId"/>) and the most recent
/// (<see cref="LastInboundMessageId"/>), so <c>EmailChannelAdapter</c> can build a two-entry
/// <c>References</c> list rather than an ever-growing one. Most real mail clients thread correctly from
/// <c>In-Reply-To</c> alone (set to <see cref="LastInboundMessageId"/>) plus a stable root reference;
/// building the full accumulated chain would mean storing and returning an unbounded list per
/// conversation for a case no client this item could test against was observed to need. If a real mail
/// client is ever found that threads incorrectly without the full chain, this is the first place to
/// look.</para>
///
/// <para><b>Written only from <c>Ago.Chat.Api</c>'s own <c>EmailWebhookEndpoints</c>, after
/// <c>ReceiveChannelMessageHandler</c> has already resolved the <see cref="ConversationId"/> - not from
/// inside that handler itself.</b> <c>ReceiveChannelMessage</c> (the shared, channel-neutral
/// command every adapter's webhook builds) has no slot for a raw provider <c>Message-ID</c> and must not
/// gain one - `ChannelPortTests`' sibling rule against a provider timestamp exists for the identical
/// reason: a channel-neutral contract must not grow a field only one channel ever populates. Recording the
/// inbound <c>Message-ID</c> is therefore a second, Email-specific write the webhook endpoint makes after
/// the shared pipeline returns, the same "compose two things rather than widen one shared contract"
/// discipline <c>ReceiveChannelMessageHandler</c>'s own remarks already apply to composing
/// <c>StartConversationHandler</c> and <c>SendVisitorMessageHandler</c> instead of reaching into either.</para>
/// </summary>
public sealed class EmailThreadState
{
    public ConversationId ConversationId { get; }

    /// <summary>The very first inbound email's own <c>Message-ID</c> in this conversation - never
    /// overwritten after <see cref="Start"/>. Anchors <c>References</c> so every outbound reply, however
    /// many messages deep, still names the message that began the thread.</summary>
    public string RootMessageId { get; private set; } = string.Empty;

    /// <summary>The most recently received inbound email's own <c>Message-ID</c> - what an outbound
    /// reply's own <c>In-Reply-To</c> header names, updated by <see cref="RecordInbound"/> on every new
    /// inbound message.</summary>
    public string LastInboundMessageId { get; private set; } = string.Empty;

    /// <summary>Captured once, at <see cref="Start"/>, from the first inbound email's own <c>Subject</c>
    /// header (or a deployment default if the visitor sent none) - reused, prefixed with <c>Re:</c> where
    /// not already present, on every outbound reply. Not re-captured on a later inbound message: real mail
    /// clients already vary a reply's own subject with a growing <c>Re: Re: Re:</c> prefix on their own,
    /// and this system does not need to track that - only the root subject an operator's first reply
    /// should echo.</summary>
    public string Subject { get; private set; } = string.Empty;

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private EmailThreadState()
    {
    }

    private EmailThreadState(ConversationId conversationId, string rootMessageId, string lastInboundMessageId, string subject)
    {
        ConversationId = conversationId;
        RootMessageId = rootMessageId;
        LastInboundMessageId = lastInboundMessageId;
        Subject = subject;
    }

    /// <summary>Begins tracking one conversation's own email thread, from its first inbound message.
    /// Called exactly once per conversation - a second call for the same <see cref="ConversationId"/> is a
    /// caller bug (the webhook endpoint's own job is to call <see cref="RecordInbound"/> instead once a row
    /// already exists), so it is not guarded here the way <see cref="ChannelCredential.Revoke"/> guards its
    /// own already-revoked case; the repository's upsert (detached-means-insert) is what actually decides
    /// which of the two the caller should have called.</summary>
    public static EmailThreadState Start(ConversationId conversationId, string messageId, string subject) =>
        new(conversationId, messageId, messageId, subject);

    /// <summary>Records a later inbound message in the same thread - updates only
    /// <see cref="LastInboundMessageId"/>; <see cref="RootMessageId"/> and <see cref="Subject"/> stay
    /// exactly as <see cref="Start"/> set them.</summary>
    public void RecordInbound(string messageId)
    {
        LastInboundMessageId = messageId;
    }
}
