namespace Ago.Chat.Application.UseCases.HandleLinkIdentityCommand;

/// <summary>Every non-<see cref="ConversationErrors"/>-failure way <c>HandleLinkIdentityCommandHandler</c>
/// can resolve one trigger message - the same "a skip is an ack, not a nack" shape
/// `RouteConversationToModuleOutcome`/`OfflineAutoReplyOutcome` both already establish for their own
/// consumers.</summary>
public enum LinkIdentityCommandOutcome
{
    /// <summary>The loop guard: not a visitor-authored message.</summary>
    NotAVisitorMessage,

    /// <summary>The message's first token was not <c>linkidentity</c>/<c>/linkidentity</c> - ordinary
    /// conversation, the overwhelming majority of messages.</summary>
    NotThisCommand,

    /// <summary>The command word matched but the channel-kind argument was missing or not a real
    /// <see cref="Domain.ChannelKind"/> name - the visitor was told how to use the command correctly, but
    /// no <see cref="Domain.PendingChannelLinkRequest"/> was created.</summary>
    InvalidArgument,

    /// <summary>A live pending link request now exists, and the visitor was told the code and where to
    /// send it from.</summary>
    RequestCreated,

    /// <summary>`CLAUDE.md` rule 5: this exact trigger message was already processed by this consumer -
    /// a redelivery, correctly producing no second effect (no second pending request, no second
    /// reply).</summary>
    AlreadyProcessed,
}
