namespace Ago.Chat.Domain.Tests;

public class SiteConsentDocumentKeyTests
{
    [Fact]
    public void For_ProducesAKeyThatSatisfiesDocumentsOwnKeyFormatRule()
    {
        var siteId = new SiteId(Guid.NewGuid());

        var key = SiteConsentDocumentKey.For(siteId, VisitorConsentPurpose.Contact);

        // Document.Create is the actual rule this key must satisfy (lowercase letters, digits, single
        // hyphens, never a leading/trailing one) - proven here by feeding the key straight through it
        // rather than re-implementing the pattern as a second regex only this test would maintain.
        var document = Document.Create(new DocumentId(Guid.NewGuid()), key);
        Assert.Equal(key, document.DocumentKey);
    }

    [Fact]
    public void For_DifferentPurposes_OnTheSameSite_ProduceDifferentKeys()
    {
        var siteId = new SiteId(Guid.NewGuid());

        var contact = SiteConsentDocumentKey.For(siteId, VisitorConsentPurpose.Contact);
        var marketing = SiteConsentDocumentKey.For(siteId, VisitorConsentPurpose.Marketing);

        Assert.NotEqual(contact, marketing);
    }

    [Fact]
    public void For_TheSamePurpose_OnDifferentSites_ProducesDifferentKeys()
    {
        var siteA = new SiteId(Guid.NewGuid());
        var siteB = new SiteId(Guid.NewGuid());

        var keyA = SiteConsentDocumentKey.For(siteA, VisitorConsentPurpose.Contact);
        var keyB = SiteConsentDocumentKey.For(siteB, VisitorConsentPurpose.Contact);

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void For_IsDeterministic_ForTheSameSiteAndPurpose()
    {
        var siteId = new SiteId(Guid.NewGuid());

        var first = SiteConsentDocumentKey.For(siteId, VisitorConsentPurpose.Contact);
        var second = SiteConsentDocumentKey.For(siteId, VisitorConsentPurpose.Contact);

        Assert.Equal(first, second);
    }
}
