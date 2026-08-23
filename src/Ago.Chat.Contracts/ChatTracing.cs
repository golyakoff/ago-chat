using System.Diagnostics;

namespace Ago.Chat.Contracts;

/// <summary>
/// `7-01`: the one <c>ActivitySource</c> every `Ago.Chat`-specific manual span (hub method entry,
/// the pipeline's batch-write span, outbox dispatch - "each broker consumer's message-processing
/// span" is instead a single generic mechanism in `Ago.Platform.Messaging.RabbitMq`, not duplicated
/// per consumer type; see that project's own `RabbitMqTracing` remarks) is started against.
///
/// Lives in `Contracts`, not `Ago.Chat.Module` (where a first instinct might put it, next to
/// `ChannelMessagePipeline` for the same "needed by more than one host" reasoning): the pipeline's
/// own DB-write span has to live in `Ago.Chat.Infrastructure.Postgres` (`MessageBatchWriter`, the
/// one project `PersistenceBoundaryTests` allows to touch Npgsql/EF Core directly - adr/0004), and
/// that project depends on `Application` and, through it, `Contracts` - never on `Module`, which
/// sits *above* it in the dependency graph (`Module` -> `Infrastructure.Postgres`, not the reverse).
/// `Contracts` is the lowest project every caller here already reaches: `Application` depends on it
/// directly, `Infrastructure.Postgres` transitively through `Application`, and `Ago.Chat.Api`/
/// `Worker` both reference it directly already (for `MessageDto` and friends). A plain
/// dependency-free static holder is a reasonable, if not perfect, fit for "integration events,
/// public and versioned, no domain types, no behaviour" - the alternative, a brand new project
/// whose only reason to exist is one `ActivitySource`, was judged premature generalisation for a
/// single field (clean-architecture.md's own "what qualifies as platform" caution generalises here:
/// an abstraction extracted from one need is a guess about the second one).
///
/// `Ago.Platform.Hosting.AddPlatformObservability`'s "Ago.*" wildcard subscription is what actually
/// makes this source's spans reach an exporter - this class only needs to pick a name starting with
/// "Ago." to be covered, not coordinate with the platform in any other way.
/// </summary>
public static class ChatTracing
{
    public const string SourceName = "Ago.Chat";

    public static readonly ActivitySource Source = new(SourceName);

    public static class SpanNames
    {
        public const string HubSendMessage = "ago.chat.hub.send_message";
        public const string OutboxDispatch = "ago.chat.outbox.dispatch";
        public const string PipelinePersistBatch = "ago.chat.pipeline.persist_batch";
    }

    /// <summary>Best-effort, shared by every caller that needs to re-parent a span from a captured
    /// W3C traceparent (`MessageBatchWriter`, `OutboxDispatcher`) - an absent or invalid value means
    /// no parent, not a failure: a message enqueued by a path this item did not instrument, or one
    /// written before this shipped, still processes correctly, just without a linked trace.</summary>
    public static bool TryParseTraceParent(string? traceParent, out ActivityContext context)
    {
        if (string.IsNullOrEmpty(traceParent))
        {
            context = default;
            return false;
        }

        return ActivityContext.TryParse(traceParent, traceState: null, out context);
    }
}
