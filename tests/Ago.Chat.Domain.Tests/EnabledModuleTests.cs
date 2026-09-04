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

    /// <summary>`22-11`: the write <c>RotateModuleCredentialHandler</c> persists - every other field
    /// unchanged, including the row's own id (so <see cref="IEnabledModuleRepository.UpdateAsync"/> can
    /// find the right row to replace).</summary>
    [Fact]
    public void WithCredential_ReplacesOnlyTheCredential()
    {
        var module = Build();
        var rotated = new ModuleCredential("rotated-secret-of-sixteen-plus-chars-x");

        var result = module.WithCredential(rotated);

        Assert.Equal(rotated, result.Credential);
        Assert.Equal(module.Id, result.Id);
        Assert.Equal(module.SiteId, result.SiteId);
        Assert.Equal(module.ModuleKey, result.ModuleKey);
        Assert.Equal(module.TriggerWords, result.TriggerWords);
        Assert.Equal(module.EntryPoint, result.EntryPoint);
        Assert.Equal(module.EnabledAt, result.EnabledAt);
    }

    /// <summary>`22-17`: not on <see cref="EnabledModule.WithCredential"/>'s own contract, but its
    /// own remarks state the promise explicitly - a rotation is not a re-grant, so it must not
    /// silently clear an owner grant's own end date or reset who granted it.</summary>
    [Fact]
    public void WithCredential_PreservesGrantedByOwnerAndExpiresAt()
    {
        var expiresAt = Now.AddDays(30);
        var module = new EnabledModule(
            new EnabledModuleId(Guid.NewGuid()), SiteId, Calendar, ["/booking"], EntryPoint, Credential, Now,
            grantedByOwner: true, expiresAt: expiresAt);

        var result = module.WithCredential(new ModuleCredential("rotated-secret-of-sixteen-plus-chars-x"));

        Assert.True(result.GrantedByOwner);
        Assert.Equal(expiresAt, result.ExpiresAt);
    }

    [Fact]
    public void Constructor_DefaultsToNotGrantedByOwner_AndNoExpiry()
    {
        var module = Build();

        Assert.False(module.GrantedByOwner);
        Assert.Null(module.ExpiresAt);
    }

    [Fact]
    public void Constructor_WithAFutureExpiry_Succeeds()
    {
        var module = new EnabledModule(
            new EnabledModuleId(Guid.NewGuid()), SiteId, Calendar, ["/booking"], EntryPoint, Credential, Now,
            grantedByOwner: true, expiresAt: Now.AddDays(1));

        Assert.Equal(Now.AddDays(1), module.ExpiresAt);
    }

    /// <summary>`22-17`'s own construction-time guard - see this constructor's own remarks for why
    /// this is refused here rather than left for a caller to discover as a silently-already-expired
    /// row. Fails-before: before this guard existed, this call succeeded and produced a row that was
    /// dead on arrival, which <see cref="EnabledModuleReadStore"/>'s own expiry filter would have
    /// hidden from every caller the moment it was written - see this item's own report for the
    /// captured pre-guard behaviour.</summary>
    [Theory]
    [InlineData(0)] // exactly EnabledAt
    [InlineData(-1)] // strictly before EnabledAt
    public void Constructor_WithAnExpiryAtOrBeforeEnabledAt_Throws(int secondsBeforeEnabledAt)
    {
        Assert.Throws<ArgumentException>(() => new EnabledModule(
            new EnabledModuleId(Guid.NewGuid()), SiteId, Calendar, ["/booking"], EntryPoint, Credential, Now,
            grantedByOwner: true, expiresAt: Now.AddSeconds(secondsBeforeEnabledAt)));
    }
}
