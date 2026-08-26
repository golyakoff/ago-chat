using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.MintDemoTenant;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Infrastructure.Keycloak;

/// <summary>
/// The only place that knows the demo identity provisioner is Keycloak
/// (`clean-architecture.md`: "AddPostgresPersistence() extension methods live in their own
/// Infrastructure projects and are selected by configuration").
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IDemoIdentityProvisioner"/> against a real Keycloak.
    ///
    /// <para><b>Called only by hosts that need it</b> - `Ago.Chat.Api` mints and `Ago.Chat.Worker`
    /// expires - and deliberately not from <c>ChatModule</c>, unlike almost everything else. ChatModule
    /// runs in every host, and a registration there would make
    /// <see cref="KeycloakAdminOptions.ClientSecret"/> a required setting for
    /// `Ago.Chat.Webhooks` too, which has no business holding it. The blast radius of a credential is
    /// partly a function of how many processes are handed it.</para>
    /// </summary>
    public static IServiceCollection AddKeycloakDemoIdentities(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Validated only when the feature is on, which is why this is a delegate rather than
        // `[Required]` on the property. Every host that can mint or expire registers this
        // unconditionally, so a `[Required]` secret would make an unset credential a **startup
        // failure for every deployment that does not use the feature at all** - including the local
        // docker-compose loop, where nobody has a provisioner client. The flag and the credential have
        // to agree, and the flag is the one a deployment sets deliberately.
        var demoEnabled = configuration.GetValue<bool>($"{DemoTenantOptions.SectionName}:Enabled");
        services
            .AddOptions<KeycloakAdminOptions>()
            .Bind(configuration.GetSection(KeycloakAdminOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => !demoEnabled
                    || (!string.IsNullOrWhiteSpace(options.BaseUrl)
                        && !string.IsNullOrWhiteSpace(options.ClientSecret)),
                $"{KeycloakAdminOptions.SectionName}:BaseUrl and :ClientSecret are required when "
                + $"{DemoTenantOptions.SectionName}:Enabled is true (8-07, adr/0058).")
            .ValidateOnStart();

        // A named HttpClient rather than a bare `new HttpClient()`: this is the seam a host wraps with
        // a resilience handler, and the one place a total request timeout can be set. Two minutes is
        // the handler lifetime default; the timeout here is what stops a hung Keycloak holding a
        // viewer's click open indefinitely.
        services.AddHttpClient<IDemoIdentityProvisioner, KeycloakDemoIdentityProvisioner>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
            });

        // The options value is unwrapped once here, not injected as IOptions<T> into the client - the
        // same shape ChatModule uses for every other options group whose consumer is a plain class
        // (MessageSendRateLimitOptions, AttachmentOptions).
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value);

        return services;
    }
}
