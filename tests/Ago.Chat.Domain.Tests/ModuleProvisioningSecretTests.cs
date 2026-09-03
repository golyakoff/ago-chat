namespace Ago.Chat.Domain.Tests;

public class ModuleProvisioningSecretTests
{
    [Fact]
    public void Constructor_AcceptsAValidSecret()
    {
        var secret = new ModuleProvisioningSecret("a-provisioning-secret-of-sixteen-plus-chars");

        Assert.Equal("a-provisioning-secret-of-sixteen-plus-chars", secret.Value);
    }

    [Fact]
    public void Constructor_Trims()
    {
        var secret = new ModuleProvisioningSecret("  a-provisioning-secret-of-sixteen-plus-chars  ");

        Assert.Equal("a-provisioning-secret-of-sixteen-plus-chars", secret.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsEmpty(string value) =>
        Assert.Throws<ArgumentException>(() => new ModuleProvisioningSecret(value));

    [Fact]
    public void Constructor_RejectsTooShort() =>
        Assert.Throws<ArgumentException>(() => new ModuleProvisioningSecret(new string('a', ModuleProvisioningSecret.MinLength - 1)));

    [Fact]
    public void Constructor_AcceptsExactlyMinLength()
    {
        var value = new string('a', ModuleProvisioningSecret.MinLength);
        var secret = new ModuleProvisioningSecret(value);

        Assert.Equal(value, secret.Value);
    }

    [Fact]
    public void Constructor_RejectsTooLong() =>
        Assert.Throws<ArgumentException>(() => new ModuleProvisioningSecret(new string('a', ModuleProvisioningSecret.MaxLength + 1)));

    [Fact]
    public void ToString_NeverPrintsTheRawValue()
    {
        var secret = new ModuleProvisioningSecret("a-secret-that-must-never-appear-in-a-log-line-anywhere");

        Assert.DoesNotContain("a-secret-that-must-never-appear-in-a-log-line-anywhere", secret.ToString());
    }
}
