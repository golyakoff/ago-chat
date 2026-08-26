using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// `14-01`'s Done-when: "a fake channel adapter (test-only, logs instead of calling a real provider)".
/// It records what it was asked to send and answers however the test tells it to - and, critically, it
/// is written the way a real adapter is written: no resilience, no retry loop, no knowledge that a
/// pipeline might wrap it. That it can be written that way is the evidence that
/// <see cref="IInboundChannelAdapter"/> keeps provider concerns and cross-cutting concerns apart.
///
/// <para><see cref="Fail"/> makes it throw instead, which is the contract's transient-fault direction
/// (that interface's own remarks) - the half a <c>Delivered: false</c> return must never be used
/// for.</para>
/// </summary>
public sealed class FakeInboundChannelAdapter(ChannelKind kind) : IInboundChannelAdapter
{
    private readonly List<OutboundChannelMessage> _sent = [];

    public ChannelKind Kind { get; } = kind;

    public IReadOnlyList<OutboundChannelMessage> Sent => _sent;

    public int Attempts { get; private set; }

    /// <summary>How many of the next calls should throw a transient fault before one succeeds.</summary>
    public int FailuresBeforeSuccess { get; set; }

    /// <summary>When set, the adapter reports a terminal provider refusal instead of succeeding.</summary>
    public string? RefuseWith { get; set; }

    public Task<ChannelSendOutcome> SendAsync(
        OutboundChannelMessage message, CancellationToken cancellationToken)
    {
        Attempts++;

        if (FailuresBeforeSuccess > 0)
        {
            FailuresBeforeSuccess--;
            throw new InvalidOperationException("fake transient provider fault");
        }

        if (RefuseWith is { } reason)
        {
            return Task.FromResult(ChannelSendOutcome.Refused(reason));
        }

        _sent.Add(message);
        return Task.FromResult(ChannelSendOutcome.Sent($"fake-{_sent.Count}"));
    }
}
