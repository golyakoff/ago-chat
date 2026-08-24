using Ago.Chat.Application.UseCases.RegisterSite;

namespace Ago.Chat.Application.Tests.UseCases.RegisterSite;

public class OriginValidatorTests
{
    [Theory]
    [InlineData("https://shop.example.com")]
    [InlineData("http://localhost:5173")]
    [InlineData("http://localhost:8080")]
    [InlineData("https://api.shop.example.com:8443")]
    public void Validate_AWellFormedOrigin_IsAllowed(string origin)
    {
        var result = OriginValidator.Validate(origin);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("shop.example.com")]
    [InlineData("ftp://shop.example.com")]
    [InlineData("https://shop.example.com/")]
    [InlineData("https://shop.example.com/path")]
    [InlineData("https://shop.example.com?query=1")]
    [InlineData("https://shop.example.com#fragment")]
    public void Validate_AMalformedOrigin_IsRejected(string origin)
    {
        var result = OriginValidator.Validate(origin);

        Assert.NotNull(result);
    }
}
