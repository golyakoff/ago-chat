namespace Ago.Chat.FakeMax;

/// <summary>Bound from <c>FakeMax:*</c> config keys - <c>Ago.Chat.FakeCrm.FakeCrmOptions</c>'
/// <c>DefaultBehavior</c> own shape, minus everything this harness has no use for (no signing secret,
/// no disappearing port - see this project's own csproj remarks on why).</summary>
public sealed class FakeMaxOptions
{
    public const string SectionName = "FakeMax";

    /// <summary>One of <c>ok</c>, <c>500</c>, or <c>hang</c> - fixed for the whole process lifetime,
    /// set via <c>FakeMax__DefaultBehavior</c> before the process starts (mirroring
    /// <c>FakeCrmOptions.DefaultBehavior</c>'s own "one running process, one personality" shape).
    /// Defaults to <c>ok</c> so a test that only needs a healthy MAX does not have to set anything.</summary>
    public string DefaultBehavior { get; set; } = "ok";

    /// <summary>How long the <c>hang</c> personality blocks before finally answering 200 - long enough
    /// that a caller's own timeout always fires first in practice; the caller giving up before this
    /// elapses (not this harness voluntarily ending early) is the whole point, matching
    /// <c>Ago.Chat.FakeCrm</c>'s own <c>Hang</c> personality.</summary>
    public int HangSeconds { get; set; } = 30;
}
