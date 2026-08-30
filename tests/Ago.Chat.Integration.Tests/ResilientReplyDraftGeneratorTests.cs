using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.YandexGpt;
using Ago.Chat.Module.ReplyDraft;
using Ago.Platform.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.CircuitBreaker;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `19-01`: proves the resilience wrapping is actually wired, not merely described - the half a
/// reviewer would otherwise have to take on faith, since this item ships no real provider credential to
/// fail against. No container, no fixture: the thing under test is a Polly pipeline and a decorator,
/// and the fake provider is an in-process delegate - `ResilientInboundChannelAdapterTests`' own
/// precedent and its own reasoning for living in this project rather than
/// <c>Ago.Chat.Application.Tests</c> (`Ago.Chat.Module.ReplyDraft` is not visible from there, the
/// dependency rule enforced by `LayeringTests`).
///
/// <para><b>The one behaviour this class proves that `ResilientInboundChannelAdapterTests` does not
/// need to</b>: `19-01`'s own Done-when - "the resilience pipeline's own unreachable-provider path
/// degrades to 'suggestion unavailable', never a stuck or silently-failing UI control." Every fault
/// that survives the pipeline (a broken circuit, an exhausted retry budget, a terminal refusal) comes
/// back from <see cref="ResilientReplyDraftGenerator"/> as <see cref="ReplyDraftGenerationResult.Unavailable"/>,
/// never as a thrown exception - the opposite of <c>ResilientInboundChannelAdapter</c>'s own contract,
/// and <see cref="ResilientReplyDraftGenerator"/>'s own remarks explain why the two decorators are
/// allowed to disagree.</para>
/// </summary>
public sealed class ResilientReplyDraftGeneratorTests
{
    private static readonly ReplyDraftGenerationRequest Request = new(
        [new ReplyDraftHistoryMessage(ReplyDraftAuthorKind.Visitor, "hi")]);

    private static ReplyDraftResiliencePipeline Pipelines(Action<ResiliencePipelineOptions> configure)
    {
        var options = new ResiliencePipelineOptions();
        configure(options);
        return new ReplyDraftResiliencePipeline(options);
    }

    [Fact]
    public async Task WhenTheProviderAnswers_TheDraftIsPassedThroughUnchanged()
    {
        var inner = new StubGenerator { Result = new ReplyDraftGenerationResult.Success("a suggested reply") };
        var wrapped = new ResilientReplyDraftGenerator(inner, Pipelines(_ => { }), NullLogger<ResilientReplyDraftGenerator>.Instance);

        var result = await wrapped.GenerateDraftAsync(Request, CancellationToken.None);

        var success = Assert.IsType<ReplyDraftGenerationResult.Success>(result);
        Assert.Equal("a suggested reply", success.DraftText);
        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task ATransientFault_IsRetried_AndTheEventualSuccessIsReturned()
    {
        var inner = new StubGenerator { FailuresBeforeSuccess = 2, Result = new ReplyDraftGenerationResult.Success("recovered") };
        var wrapped = new ResilientReplyDraftGenerator(inner, Pipelines(options =>
            options.Retry = new ResilienceRetryOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
            }), NullLogger<ResilientReplyDraftGenerator>.Instance);

        var result = await wrapped.GenerateDraftAsync(Request, CancellationToken.None);

        var success = Assert.IsType<ReplyDraftGenerationResult.Success>(result);
        Assert.Equal("recovered", success.DraftText);
        Assert.Equal(3, inner.Attempts);
    }

    /// <summary>The Done-when this whole class exists for: a fault that survives every retry never
    /// reaches the caller as an exception - it degrades to <see cref="ReplyDraftGenerationResult.Unavailable"/>,
    /// which is the only outcome an operator's "Suggest a reply" button needs to render "not available
    /// right now" instead of a stuck spinner or an unhandled 500.</summary>
    [Fact]
    public async Task WhenEveryRetryIsExhausted_DegradesToUnavailable_RatherThanThrowing()
    {
        var inner = new StubGenerator { FailuresBeforeSuccess = int.MaxValue };
        var wrapped = new ResilientReplyDraftGenerator(inner, Pipelines(options =>
            options.Retry = new ResilienceRetryOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
            }), NullLogger<ResilientReplyDraftGenerator>.Instance);

        var result = await wrapped.GenerateDraftAsync(Request, CancellationToken.None);

        Assert.IsType<ReplyDraftGenerationResult.Unavailable>(result);
    }

    /// <summary>Same degrade, once the breaker itself is what refuses the call
    /// (<see cref="BrokenCircuitException"/>, never surfaced to the caller either).</summary>
    [Fact]
    public async Task WhenTheBreakerIsOpen_DegradesToUnavailable()
    {
        var pipelines = Pipelines(options => options.CircuitBreaker = new ResilienceCircuitBreakerOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 2,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(30),
        });

        var inner = new StubGenerator { FailuresBeforeSuccess = int.MaxValue };
        var wrapped = new ResilientReplyDraftGenerator(inner, pipelines, NullLogger<ResilientReplyDraftGenerator>.Instance);

        for (var i = 0; i < 4; i++)
        {
            await wrapped.GenerateDraftAsync(Request, CancellationToken.None);
        }

        // The breaker is now open: the next call is rejected without the provider being asked again.
        var attemptsBefore = inner.Attempts;
        var result = await wrapped.GenerateDraftAsync(Request, CancellationToken.None);

        Assert.IsType<ReplyDraftGenerationResult.Unavailable>(result);
        Assert.Equal(attemptsBefore, inner.Attempts);
    }

    /// <summary>A terminal refusal (a bad key, a malformed request) degrades the identical way a
    /// transient one does, from the caller's own point of view - <see cref="ReplyDraftProviderRefusedException"/>
    /// is not retried (`ReplyDraftResiliencePipeline.IsRetryWorthy`'s own remarks), so exactly one
    /// attempt is made, and it still ends in <see cref="ReplyDraftGenerationResult.Unavailable"/> rather
    /// than propagating a message that would expose "your API key is wrong" to an operator.</summary>
    [Fact]
    public async Task ATerminalRefusal_IsNotRetried_AndStillDegradesToUnavailable()
    {
        var inner = new StubGenerator { ThrowRefused = true };
        var wrapped = new ResilientReplyDraftGenerator(inner, Pipelines(options =>
            options.Retry = new ResilienceRetryOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
            }), NullLogger<ResilientReplyDraftGenerator>.Instance);

        var result = await wrapped.GenerateDraftAsync(Request, CancellationToken.None);

        Assert.IsType<ReplyDraftGenerationResult.Unavailable>(result);
        Assert.Equal(1, inner.Attempts);
    }

    /// <summary>The caller's own cancellation is never turned into "suggestion unavailable" - there is
    /// no caller left to read that answer, so it propagates instead (`ResilientReplyDraftGenerator`'s
    /// own remarks).</summary>
    [Fact]
    public async Task ACancellation_PropagatesRatherThanDegrading()
    {
        var inner = new StubGenerator { ThrowCancelled = true };
        var wrapped = new ResilientReplyDraftGenerator(inner, Pipelines(_ => { }), NullLogger<ResilientReplyDraftGenerator>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => wrapped.GenerateDraftAsync(Request, CancellationToken.None));
    }

    /// <summary>An implementation written with no awareness of resilience at all - which is the point
    /// of the decorator, the same shape `ResilientInboundChannelAdapterTests.StubAdapter` establishes.</summary>
    private sealed class StubGenerator : IReplyDraftGenerator
    {
        public int Attempts { get; private set; }

        public int FailuresBeforeSuccess { get; set; }

        public bool ThrowRefused { get; set; }

        public bool ThrowCancelled { get; set; }

        public ReplyDraftGenerationResult Result { get; set; } = new ReplyDraftGenerationResult.Success("stub");

        public Task<ReplyDraftGenerationResult> GenerateDraftAsync(
            ReplyDraftGenerationRequest request, CancellationToken cancellationToken)
        {
            Attempts++;

            if (ThrowCancelled)
            {
                throw new OperationCanceledException();
            }

            if (ThrowRefused)
            {
                throw new ReplyDraftProviderRefusedException("stub terminal refusal");
            }

            if (FailuresBeforeSuccess > 0)
            {
                if (FailuresBeforeSuccess != int.MaxValue)
                {
                    FailuresBeforeSuccess--;
                }

                throw new InvalidOperationException("stub transient provider fault");
            }

            return Task.FromResult(Result);
        }
    }
}
