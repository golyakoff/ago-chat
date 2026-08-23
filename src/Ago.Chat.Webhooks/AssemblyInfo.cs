using System.Runtime.CompilerServices;

// `6-07`: ConcurrentWebhookDispatchPump/PartitionSequencer stay internal to this host - only
// Ago.Chat.Concurrency.Tests needs them directly, to prove concurrency and per-partition-key
// ordering without a real broker (Ago.Chat.Worker's own InternalsVisibleTo precedent for the same
// reason).
[assembly: InternalsVisibleTo("Ago.Chat.Concurrency.Tests")]
