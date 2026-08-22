using System.Runtime.CompilerServices;

// MessageBatchWriter.FlushAsync stays internal to everything outside this project - only the
// integration/concurrency tests that need a synchronous, single-item "pipeline" (for tests about
// something other than the pipeline itself - fanout, outbox, rate limiting, reconnect) call it
// directly instead of going through the real Channel/BackgroundService machinery, matching
// Ago.Chat.Infrastructure.Postgres/Ago.Chat.Worker's own InternalsVisibleTo precedent for the same
// reason (`4-05`).
[assembly: InternalsVisibleTo("Ago.Chat.Integration.Tests")]
[assembly: InternalsVisibleTo("Ago.Chat.Concurrency.Tests")]
