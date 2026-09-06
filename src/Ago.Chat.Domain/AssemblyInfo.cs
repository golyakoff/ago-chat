using System.Runtime.CompilerServices;

// `24-10`: lets a fake IConversationRepository seed a blocked Conversation for an Application-layer
// handler test, without a change-tracked EF context to load one through - Conversation.BlockedAt/
// BlockedBy have no domain method the way every other mutable property on this aggregate does
// (Conversation.MarkBlockedForTesting's own remarks explain why), so a test needs this seam or cannot
// construct the state at all. The same "expose the real seam via InternalsVisibleTo rather than
// re-implementing it in the test" precedent Ago.Chat.Infrastructure.Postgres/Ago.Chat.Worker's own
// AssemblyInfo.cs already establish, applied to Ago.Chat.Domain for the first time.
[assembly: InternalsVisibleTo("Ago.Chat.Application.Tests")]
