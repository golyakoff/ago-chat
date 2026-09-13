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

    /// <summary>
    /// `25-73`: registers <see cref="IOperatorInviteEmailProvisioner"/> against the same real Keycloak
    /// <see cref="AddKeycloakDemoIdentities"/> targets - the identical `KeycloakAdminOptions` binding
    /// (same service-account client, same `manage-users` scope), since both provisioners are ordinary
    /// Admin API callers against the one realm this deployment has.
    ///
    /// <para><b>Called only by `Ago.Chat.Api`</b> - unlike the demo path, nothing else ever creates a
    /// real operator invite (`Ago.Chat.Worker` only expires demo tenants, never sends invite mail) - and
    /// deliberately not from `ChatModule`, the identical "a credential's blast radius is partly a
    /// function of how many processes are handed it" reasoning <see cref="AddKeycloakDemoIdentities"/>'s
    /// own remarks already give.</para>
    ///
    /// <para>Binds <see cref="KeycloakAdminOptions"/> a second time if <see cref="AddKeycloakDemoIdentities"/>
    /// is also called (both do, in `Ago.Chat.Api`'s own `Program.cs`, if demo minting is enabled there
    /// too) - harmless: <c>AddOptions&lt;T&gt;().Bind(...)</c> is idempotent against the same
    /// configuration section, and the resulting <c>services.AddSingleton(...)</c> line simply resolves
    /// the same already-validated value a second time rather than producing two.</para>
    /// </summary>
    public static IServiceCollection AddKeycloakOperatorInviteEmails(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<KeycloakAdminOptions>()
            .Bind(configuration.GetSection(KeycloakAdminOptions.SectionName))
            .ValidateDataAnnotations()
            // `25-73`: unlike the demo path's feature-flagged validation, operator-invite email is not
            // behind a flag - every deployment that lets an admin create an invite needs a real
            // BaseUrl/ClientSecret, so this one is unconditional rather than a delegate keyed on a
            // second option.
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.BaseUrl) && !string.IsNullOrWhiteSpace(options.ClientSecret),
                $"{KeycloakAdminOptions.SectionName}:BaseUrl and :ClientSecret are required for operator-invite email (25-73).")
            .ValidateOnStart();

        services.AddHttpClient<IOperatorInviteEmailProvisioner, OperatorInviteEmailProvisioner>(client =>
            {
                // Three admin calls in sequence per invite (create-or-find, optionally sync-locale,
                // send) - a longer budget than the demo provisioner's single call gets, for the
                // identical reason a hung Keycloak must not hold an admin's "send invite" click open
                // indefinitely, just with more round trips to allow for.
                client.Timeout = TimeSpan.FromSeconds(20);
            });

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value);

        return services;
    }
}
