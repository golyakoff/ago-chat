namespace Ago.Chat.Domain.Tests;

public class EnabledModuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly ModuleKey Calendar = new("calendar");
    private static readonly Uri EntryPoint = new("https://calendar.example.com");
    private static readonly ModuleCredential Credential = new("a-shared-secret-of-sixteen-plus-chars");

    private static EnabledModule Build(
        IReadOnlyList<string>? triggerWords = null, Uri? entryPoint = null, ModuleCredential? credential = null) =>
        new(
            new EnabledModuleId(Guid.NewGuid()), SiteId, Calendar, triggerWords ?? ["/booking"],
            entryPoint ?? EntryPoint, credential ?? Credential, Now);

    [Fact]
    public void Constructor_WithValidInput_Succeeds()
    {
        var module = Build(["/booking", "book"]);

        Assert.Equal(SiteId, module.SiteId);
        Assert.Equal(Calendar, module.ModuleKey);
        Assert.Equal(["/booking", "book"], module.TriggerWords);
        Assert.Equal(EntryPoint, module.EntryPoint);
        Assert.Equal(Credential, module.Credential);
    }

    [Fact]
    public void Constructor_WithNoTriggerWords_Throws() =>
        Assert.Throws<ArgumentException>(() => Build([]));

    [Fact]
    public void Constructor_WithTooManyTriggerWords_Throws() =>
        Assert.Throws<ArgumentException>(() => Build(
            Enumerable.Range(0, EnabledModule.MaxTriggerWords + 1).Select(i => $"trigger{i}").ToList()));

    [Fact]
    public void Constructor_WithAnEmptyTriggerWord_Throws() =>
        Assert.Throws<ArgumentException>(() => Build(["book", ""]));

    [Fact]
    public void Constructor_WithATooLongTriggerWord_Throws() =>
        Assert.Throws<ArgumentException>(() => Build([new string('a', EnabledModule.MaxTriggerWordLength + 1)]));

    /// <summary>Two spellings of the same word, different casing, on one module - internally
    /// ambiguous before it is ever compared against another module's own words.</summary>
    [Fact]
    public void Constructor_WithTwoTriggerWordsDifferingOnlyByCasing_Throws() =>
        Assert.Throws<ArgumentException>(() => Build(["Book", "book"]));

    [Theory]
    [InlineData("ftp://calendar.example.com")]
    [InlineData("not-a-url")]
    public void Constructor_WithANonHttpEntryPoint_Throws(string url) =>
        Assert.Throws<ArgumentException>(() => Build(entryPoint: new Uri(url, UriKind.RelativeOrAbsolute)));
}
