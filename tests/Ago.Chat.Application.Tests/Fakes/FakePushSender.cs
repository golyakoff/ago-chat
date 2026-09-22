using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`26-05`: `NotifyOperatorDevicesHandlerTests`' own fake of the `26-04` port - never a
/// network call, so every outcome is scripted by the test rather than actually reaching RuStore
/// (`IPushSender`'s own remarks on why this port exists at all: a handler that must be testable
/// without a real RuStore project or service token).</summary>
public sealed class FakePushSender : IPushSender
{
    private readonly Queue<PushSendOutcome> _outcomes;

    public FakePushSender(params PushSendOutcome[] outcomes)
    {
        _outcomes = new Queue<PushSendOutcome>(outcomes.Length == 0 ? [new PushSendOutcome.Delivered()] : outcomes);
    }

    public List<PushMessage> Calls { get; } = [];

    public Task<PushSendOutcome> SendAsync(PushMessage message, CancellationToken cancellationToken)
    {
        Calls.Add(message);
        // The last scripted outcome repeats once the queue is exhausted, so a test that only cares
        // about the first send of several devices need not script one entry per device.
        var outcome = _outcomes.Count > 1 ? _outcomes.Dequeue() : _outcomes.Peek();
        return Task.FromResult(outcome);
    }
}
