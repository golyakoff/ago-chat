using Ago.Chat.Api.Demo;
using Ago.Chat.Application.UseCases.MintDemoTenant;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-45`: the guard on the direction this item made the console's demo band fail in.
///
/// <para>Before it, a console route that could not say who its reader was showed the <i>stricter</i>
/// warning. Now the band appears only for sites this list names, so an empty list on a demo deployment
/// removes the warning from the one account whose password is on a public page - silently, as a missing
/// sentence on a console that otherwise works perfectly. This validator turns that into a refusal to
/// start.</para>
///
/// <para>Plain unit tests despite living in the integration project: this repository has no
/// <c>Ago.Chat.Api.Tests</c>, and the type under test is a host type. No container, no fixture, no
/// collection attribute - so it costs this suite nothing.</para>
/// </summary>
public class DemoTenantOptionsValidatorTests
{
    private static readonly DemoTenantOptionsValidator Validator = new();

    [Fact]
    public void Validate_WhenMintingIsOnAndNoPublishedSiteIsNamed_Fails()
    {
        var result = Validator.Validate(null, new DemoTenantOptions { Enabled = true });

        Assert.True(result.Failed);
        Assert.Contains("PublishedCredentialSitePublicKeys", result.FailureMessage);
    }

    [Fact]
    public void Validate_WhenMintingIsOnAndAPublishedSiteIsNamed_Succeeds()
    {
        var result = Validator.Validate(null, new DemoTenantOptions
        {
            Enabled = true,
            PublishedCredentialSitePublicKeys = ["demo_site"],
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_OnAnOrdinaryDeploymentWithNoMinting_SucceedsWithAnEmptyList()
    {
        // Every real installation: minting off, nobody's credentials published, nothing to demand.
        // The rule must not turn "this is not a demo deployment" into a startup failure.
        var result = Validator.Validate(null, new DemoTenantOptions { Enabled = false });

        Assert.True(result.Succeeded);
    }
}
