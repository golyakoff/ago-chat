namespace Ago.Chat.Domain.Tests;

/// <summary>`18-04`: the same domain-unit shape as <see cref="ConversationNoteTests"/>.</summary>
public class TagTests
{
    private static readonly TagId Id = new(Guid.NewGuid());
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithAnEmptyName_Throws(string name) =>
        Assert.Throws<ArgumentException>(() => Tag.Create(Id, SiteId, name, Now));

    [Fact]
    public void Create_WithAnOversizedName_Throws() =>
        Assert.Throws<ArgumentException>(() => Tag.Create(Id, SiteId, new string('t', Tag.MaxNameLength + 1), Now));

    [Fact]
    public void Create_WhenValid_SetsProperties()
    {
        var tag = Tag.Create(Id, SiteId, "VIP", Now);

        Assert.Equal(Id, tag.Id);
        Assert.Equal(SiteId, tag.SiteId);
        Assert.Equal("VIP", tag.Name);
        Assert.Equal(Now, tag.CreatedAt);
    }

    [Fact]
    public void Create_TrimsName() =>
        Assert.Equal("VIP", Tag.Create(Id, SiteId, "  VIP  ", Now).Name);

    [Fact]
    public void Rename_WhenValid_ReplacesName()
    {
        var tag = Tag.Create(Id, SiteId, "VIP", Now);

        tag.Rename("Priority");

        Assert.Equal("Priority", tag.Name);
    }

    [Fact]
    public void Rename_KeepsIdAndSiteId()
    {
        var tag = Tag.Create(Id, SiteId, "VIP", Now);

        tag.Rename("Priority");

        Assert.Equal(Id, tag.Id);
        Assert.Equal(SiteId, tag.SiteId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_WithAnEmptyName_Throws(string name)
    {
        var tag = Tag.Create(Id, SiteId, "VIP", Now);

        Assert.Throws<ArgumentException>(() => tag.Rename(name));
    }
}
