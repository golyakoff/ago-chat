using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeSiteLogoPublicUrlBuilder : ISiteLogoPublicUrlBuilder
{
    public string Build(string objectKey) => $"https://files.test/attachments/{objectKey}";
}
