namespace Ago.Chat.Domain.Tests;

public class SiteTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WhenPublicKeyIsEmptyOrWhitespace_Throws(string publicKey)
    {
        Assert.Throws<ArgumentException>(() => new Site(new SiteId(Guid.NewGuid()), publicKey, []));
    }

    [Fact]
    public void Constructor_WhenValid_SetsProperties()
    {
        var id = new SiteId(Guid.NewGuid());
        string[] origins = ["https://shop.example"];

        var site = new Site(id, "shop_7f3a", origins);

        Assert.Equal(id, site.Id);
        Assert.Equal("shop_7f3a", site.PublicKey);
        Assert.Equal(origins, site.AllowedOrigins);
    }
}
