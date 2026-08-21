using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ResolveMessageDelivery;

/// <summary>The Worker-side reaction to a persisted <c>MessageAccepted</c> (3-02): resolve who
/// should see this message live, and hand it to the fan-out path. Not a write - nothing here
/// changes <see cref="Conversation"/> state, so there is no domain event and nothing to outbox.
///
/// Named <c>...Targets</c>, not just <c>ResolveMessageDelivery</c> matching the folder: a type with
/// the exact same simple name as its own containing namespace's trailing segment shadows a `using`
/// import of itself in any file whose own namespace also ends in that segment (every test file here
/// mirrors `Tests.UseCases.&lt;folder&gt;` - `RecordUnread`/`RecordUnreadMessage` already avoids this
/// the same way, for the same reason).</summary>
public sealed record ResolveMessageDeliveryTargets(ConversationId ConversationId, int Sequence, Guid CorrelationId);
