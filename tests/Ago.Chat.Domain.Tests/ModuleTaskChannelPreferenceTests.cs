namespace Ago.Chat.Domain.Tests;

/// <summary>`20-11`: the one invariant this small entity enforces itself - `Add` rejects a non-positive
/// priority. Every other guarantee ("never an arbitrary/unverified channel identity", "unique per
/// booking") is enforced one layer out, by the handler and the storage-level index respectively - this
/// type's own remarks explain why it carries a plain <see cref="ChannelIdentityId"/> reference rather
/// than re-validating anything about the identity itself.</summary>
public class ModuleTaskChannelPreferenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Add_WithAPositivePriority_Succeeds()
    {
        var preference = ModuleTaskChannelPreference.Add(
            new ModuleTaskChannelPreferenceId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), new ModuleTaskId(Guid.NewGuid()),
            new VisitorId(Guid.NewGuid()), new ChannelIdentityId(Guid.NewGuid()), priority: 1, Now);

        Assert.Equal(1, preference.Priority);
        Assert.Equal(Now, preference.AddedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Add_WithANonPositivePriority_Throws(int priority)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModuleTaskChannelPreference.Add(
            new ModuleTaskChannelPreferenceId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), new ModuleTaskId(Guid.NewGuid()),
            new VisitorId(Guid.NewGuid()), new ChannelIdentityId(Guid.NewGuid()), priority, Now));
    }
}
