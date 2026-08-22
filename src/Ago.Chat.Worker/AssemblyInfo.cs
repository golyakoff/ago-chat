using System.Runtime.CompilerServices;

// ConversationAssignmentJob.RunOnceAsync/PartitionMaintenanceJob.EnsurePartitionsAsync stay internal
// to everything outside this host - only the concurrency/integration tests that directly exercise a
// single tick (instead of running the whole BackgroundService loop) need them, matching
// Ago.Chat.Infrastructure.Postgres's own InternalsVisibleTo precedent for the same reason.
[assembly: InternalsVisibleTo("Ago.Chat.Integration.Tests")]
[assembly: InternalsVisibleTo("Ago.Chat.Concurrency.Tests")]
