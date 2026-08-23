namespace Ago.Chat.FakeCrm;

/// <summary>
/// Bound from <c>FakeCrm:*</c> config keys, validated at startup - a typo here must fail the process
/// (naming-and-structure.md's options-validation rule), not silently let an unsigned request through
/// or leave the "disappears" personality unreachable.
/// </summary>
public sealed class FakeCrmOptions
{
    public const string SectionName = "FakeCrm";

    /// <summary>
    /// No built-in default, unlike the rate-limit options elsewhere in this repo - a missing signing
    /// secret is not "a reasonable starting point," it is a signature check that verifies nothing.
    /// Program.cs fails fast on an empty value instead. The literal value this project's own
    /// appsettings.Development.json ships is a fixed, published test-only string with no
    /// relationship to anything real, the same category as AttachmentFixture's MinIO credentials.
    /// </summary>
    public string SigningSecret { get; set; } = "";

    /// <summary>
    /// How far a request's <c>t=</c> timestamp may drift from now before the signature is rejected as
    /// a replay, independent of whether the HMAC itself checks out. Default 300s (Stripe's own
    /// published tolerance) - `6-03`'s webhook-registration ADR, which actually owns this scheme, was
    /// being written concurrently and was not readable while this harness was built, so this number is
    /// this project's own assumption, not a value taken from that ADR. See this project's README for
    /// the full note; reconcile against `6-03`'s final text once it exists.
    /// </summary>
    public int SignatureToleranceSeconds { get; set; } = 300;

    /// <summary>
    /// The dedicated port the "disappears" personality listens on. Selected by which port a caller
    /// connects to, not by the <c>X-Fake-Crm-Behavior</c> header the other three personalities share -
    /// refusing a TCP connection has to happen before any HTTP request is readable, so it cannot
    /// depend on a header inside one (Program.cs's own remark; this project's README explains it in
    /// full).
    /// </summary>
    public int DisappearPort { get; set; } = 5291;

    /// <summary>
    /// True (default): every connection made to <see cref="DisappearPort"/> is accepted and then
    /// immediately RST'd, proving the caller sees a <see cref="System.Net.Sockets.SocketException"/>
    /// with <see cref="System.Net.Sockets.SocketError.ConnectionReset"/>. False: the port is never
    /// bound at all, proving the other half of the backlog's own wording - "closed port, or an
    /// immediate RST" - as <see cref="System.Net.Sockets.SocketError.ConnectionRefused"/> instead.
    /// </summary>
    public bool DisappearPortListens { get; set; } = true;

    /// <summary>
    /// `6-05`: an additive extension, not a change to the three-personalities-via-header scheme this
    /// project's own README documents - the header, when a caller sends one, still wins outright
    /// (`Program.cs`'s own `HandleDeliverAsync`). Every existing test in
    /// `Ago.Chat.FakeCrm.Tests.FakeCrmPersonalityTests` sends its own header on every request and never
    /// sets this, so its behaviour is unchanged by this option's mere existence.
    ///
    /// The gap this closes: `X-Fake-Crm-Behavior` can only be set by whoever calls this harness, and
    /// `6-05`'s own dispatcher - a *production* HTTP client calling a tenant's real endpoint - has no
    /// business ever sending a header this specific test double invented. Proving the breaker opens
    /// per-endpoint (one endpoint `5xxs`, a different one `succeeds`, concurrently) or the bulkhead
    /// holds per-tenant (one site's endpoints all `hang`, another site's do not) needs several
    /// endpoints answering with *different, fixed* personalities at once, driven only by which URL a
    /// registered `WebhookEndpoint` points at - not by a header the real dispatcher would never send.
    /// This is that fixed personality: same grammar as the header
    /// (<see cref="FakeCrmBehavior.Parse"/> - <c>succeeds</c>/<c>500</c>/<c>503</c>/<c>5xx</c>/
    /// <c>hang</c>/<c>hang-&lt;seconds&gt;</c>), read only when a request carries no header of its own,
    /// so one running process, started with a distinct <c>FakeCrm__DefaultBehavior</c> per instance,
    /// stands in for one endpoint's fixed, known-in-advance personality for the whole test.
    /// </summary>
    public string? DefaultBehavior { get; set; }
}
