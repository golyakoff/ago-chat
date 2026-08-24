using System.Diagnostics;
using System.Threading.Channels;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Contracts;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Module.Pipeline;

/// <summary>
/// `4-05`: the writer half of concurrency.md's "In-process pipeline (Api)" - implements
/// <see cref="IMessagePipeline"/> (Application's port) by owning the bounded
/// <see cref="Channel{T}"/> the diagram names.
///
/// Lives in <c>Ago.Chat.Module</c>, not <c>Ago.Chat.Api</c>, even though only <c>Ago.Chat.Api</c>
/// ever actually drives it - the same relocation <c>OperatorPresencePublisher</c> went through in
/// `4-04` for the mirror-image reason. `ChatModule.ConfigureServices` registers
/// <c>SendVisitorMessageHandler</c>/<c>SendOperatorMessageHandler</c> for *every* host, and
/// <c>Ago.Chat.Api</c> depends on <c>Ago.Chat.Module</c>, never the other way round - a Module class
/// cannot reference an Api one, so this type has to sit somewhere both Module and Api can see it.
/// <c>Ago.Chat.Worker</c> never resolves it in practice, but it must still be resolvable there:
/// ASP.NET Core's default `ValidateOnBuild` walks every registered service's constructor graph at
/// startup, in Development, regardless of whether anything ever actually asks for it. Not a new
/// dedicated Infrastructure project either - clean-architecture.md's qualifying rule: a
/// `Ago.Chat.Infrastructure.Pipeline` project would suggest a second implementation could reasonably
/// replace this one the way `Ago.Chat.Infrastructure.Postgres` can be swapped for another database;
/// nothing about an in-process channel is swappable in that sense.
/// </summary>
public sealed class ChannelMessagePipeline : IMessagePipeline, IDisposable
{
    private readonly Channel<InboundMessage> _channel;
    private readonly MessagePipelineOptions _options;
    private readonly IDisposable _stoppingRegistration;

    public ChannelMessagePipeline(IOptions<MessagePipelineOptions> options, IHostApplicationLifetime lifetime)
    {
        _options = options.Value;
        _channel = Channel.CreateBounded<InboundMessage>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });

        // Registered in the constructor, not discovered via ExecuteAsync - this type has no
        // BackgroundService lifecycle at all (it is a plain singleton), so the constructor is the
        // only, and earliest, place to hook ApplicationStopping (the same lesson 3-06's
        // ConnectionDrainCoordinator learned the hard way: StartAsync returning is not "running").
        // Completing the channel here is what lets MessagePipelineWorkerHost's own drain loop
        // terminate once genuinely empty, instead of needing a second shutdown signal.
        _stoppingRegistration = lifetime.ApplicationStopping.Register(() => _channel.Writer.TryComplete());

        // `7-02`: nfr.md's "channel occupancy" - registers this instance's own live reader count
        // against its configured capacity with ChatMetrics's shared gauge (that class's own remarks
        // on why the gauge itself lives there, created once, rather than here per instance).
        ChatMetrics.RegisterChannelOccupancyProvider(() => _channel.Reader.Count, _options.ChannelCapacity);
    }

    internal ChannelReader<InboundMessage> Reader => _channel.Reader;

    public async Task<Result<int>> EnqueueAsync(PendingMessage message, CancellationToken cancellationToken)
    {
        var ack = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        // `7-02`: nfr.md's "enqueue-wait histogram" - captured here, at the moment this item first
        // becomes queueable, and consumed by MessagePipelineWorkerHost once a worker actually
        // dequeues it (that class's own remarks).
        var inbound = new InboundMessage(message, ack, Stopwatch.GetTimestamp());

        using var timeoutCts = new CancellationTokenSource(_options.EnqueueTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await _channel.Writer.WriteAsync(inbound, linked.Token);
        }
        catch (ChannelClosedException)
        {
            return ConversationErrors.Unavailable("The server is shutting down - try again.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The channel stayed full for the whole EnqueueTimeout - this is the timeoutCts firing,
            // not the caller's own token. 4-05's Scope, the author's decision: block with a timeout,
            // then fail explicitly, never an unbounded wait and never a silent drop.
            return ConversationErrors.Unavailable("Server is busy, try again.");
        }

        return await ack.Task.WaitAsync(cancellationToken);
    }

    public void Dispose() => _stoppingRegistration.Dispose();
}
