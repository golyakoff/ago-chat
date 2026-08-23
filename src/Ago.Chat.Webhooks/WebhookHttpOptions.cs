using System.ComponentModel.DataAnnotations;

namespace Ago.Chat.Webhooks;

/// <summary>
/// `6-05`: the two timeout layers `Ago.Platform.Resilience.ResiliencePipelineOptions.Timeout` cannot
/// express on its own - that one knob is the *total* per-attempt deadline
/// (`WebhookResiliencePipelines`' own remarks), applied through the shared
/// `Ago.Platform.Resilience.ResiliencePolicyBuilder`. `ConnectTimeout` and `ResponseHeadersTimeout`
/// are HTTP-transport-specific concerns the shared builder was never meant to model (it stays
/// generic across every boundary it wraps, Redis and S3 included, neither of which speaks HTTP at
/// all) - `resilience.md`'s own "layered timeouts: connect, response-headers, total" is three
/// distinct enforcement points because a target that never even opens a TCP connection, a target that
/// accepts the connection but never sends headers, and a target that streams a body forever, are three
/// different failure shapes worth distinguishing in a log, even though all three end in the same
/// dead-lettered outcome today.
/// </summary>
public sealed class WebhookHttpOptions
{
    public const string SectionName = "Webhooks:Http";

    [Range(typeof(TimeSpan), "00:00:00.001", "00:01:00")]
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(2);

    [Range(typeof(TimeSpan), "00:00:00.001", "00:01:00")]
    public TimeSpan ResponseHeadersTimeout { get; set; } = TimeSpan.FromSeconds(2);
}
