using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Infrastructure.Postgres.Pipeline;

/// <summary>
/// What actually flows through the pipeline's channels: the technology-free <see
/// cref="PendingMessage"/> Application handed to <see cref="IMessagePipeline"/>'s implementation,
/// plus the <see cref="TaskCompletionSource{T}"/> the enqueuing hub call is awaiting - `4-05`'s Scope
/// names this exact shape ("the caller's ack: a TaskCompletionSource&lt;Result&lt;int&gt;&gt;...
/// created when the hub method enqueues, completed by the pipeline worker once the batch commits").
///
/// Lives beside <see cref="MessageBatchWriter"/> in <c>Ago.Chat.Infrastructure.Postgres</c>, not
/// <c>Ago.Chat.Module</c> where the rest of the pipeline sits - `PersistenceBoundaryTests`
/// (`adr/0004`) forbids anything outside this project from referencing Npgsql/EF Core directly, and
/// <see cref="MessageBatchWriter"/> is exactly that kind of code. Internal, reached from
/// <c>Ago.Chat.Module.Pipeline</c> via this project's own `InternalsVisibleTo` (`AssemblyInfo.cs`) -
/// nothing further out than that ever sees a queued item directly, only the
/// <see cref="IMessagePipeline"/> port's <c>Result&lt;int&gt;</c>.
/// </summary>
internal sealed record InboundMessage(PendingMessage Message, TaskCompletionSource<Result<int>> Ack);
