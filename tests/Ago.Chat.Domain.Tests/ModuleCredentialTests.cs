namespace Ago.Chat.Domain.Tests;

public class ModuleCredentialTests
{
    [Fact]
    public void Constructor_AcceptsAValidSecret()
    {
        var credential = new ModuleCredential("a-shared-secret-of-sixteen-plus-chars");

        Assert.Equal("a-shared-secret-of-sixteen-plus-chars", credential.Value);
    }

    [Fact]
    public void Constructor_Trims()
    {
        var credential = new ModuleCredential("  a-shared-secret-of-sixteen-plus-chars  ");

        Assert.Equal("a-shared-secret-of-sixteen-plus-chars", credential.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsEmpty(string value) =>
        Assert.Throws<ArgumentException>(() => new ModuleCredential(value));

    [Fact]
    public void Constructor_RejectsTooShort() =>
        Assert.Throws<ArgumentException>(() => new ModuleCredential(new string('a', ModuleCredential.MinLength - 1)));

    [Fact]
    public void Constructor_AcceptsExactlyMinLength()
    {
        var value = new string('a', ModuleCredential.MinLength);
        var credential = new ModuleCredential(value);

        Assert.Equal(value, credential.Value);
    }

    [Fact]
    public void Constructor_RejectsTooLong() =>
        Assert.Throws<ArgumentException>(() => new ModuleCredential(new string('a', ModuleCredential.MaxLength + 1)));

    [Fact]
    public void ToString_NeverPrintsTheRawValue()
    {
        var credential = new ModuleCredential("a-shared-secret-that-must-never-appear-in-a-log-line");

        Assert.DoesNotContain("a-shared-secret-that-must-never-appear-in-a-log-line", credential.ToString());
    }
}
