using System.Text.Json;
using Ago.Chat.Api.WidgetConfig;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// <para>
/// `23-108`. <see cref="WidgetConfigEndpoints.UpdateWidgetConfigRequest.RequireContactConsent"/> is a
/// <b>gate</b>: while it is on, no contact detail is recorded until the visitor has accepted the
/// tenant's consent document. A missing <c>bool</c> binds to <c>false</c> silently, so an omission
/// turned enforcement off while looking like an ordinary save - and `ago-console` omitted it for as
/// long as the field existed, because its own DTO had no such property and it serialises that object
/// as the entire PUT body. Saving a colour would have cleared the gate.
/// </para>
/// <para>
/// It was harmless only because the console also had no way to switch the gate on, so the stored value
/// was never anything but <c>false</c>. Both halves are this item; this is the half that stops the
/// other half's fix from being undone by the next save.
/// </para>
/// <para>
/// <b>Why the options are built here rather than taken from the host.</b> These are
/// <see cref="JsonSerializerDefaults.Web"/>, which is what Minimal API binds a body with. Constructing
/// them makes the test independent of a running host - but it also means this asserts the contract of
/// the record rather than of the pipeline, and that limit is worth stating: if the host were ever
/// configured with different options, this test would keep passing while the endpoint changed
/// behaviour. `Program.cs` configures none today.
/// </para>
/// </summary>
public sealed class UpdateWidgetConfigRequestBindingTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ABodyWithoutRequireContactConsent_IsRefused_RatherThanBindingToFalse()
    {
        // Exactly what `ago-console` sent before this item: every other field, and no mention of the gate.
        const string body = """
            {
              "primaryColorHex": "#336699",
              "position": "BottomRight",
              "locale": "Ru",
              "noticeText": null,
              "noticeUrl": null
            }
            """;

        var thrown = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<WidgetConfigEndpoints.UpdateWidgetConfigRequest>(body, WebOptions));

        Assert.Contains("RequireContactConsent", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABodyThatStatesTheGate_BindsIt_InEitherDirection(bool required)
    {
        var body = $$"""
            {
              "primaryColorHex": null,
              "position": "BottomLeft",
              "locale": "En",
              "noticeText": null,
              "noticeUrl": null,
              "requireContactConsent": {{(required ? "true" : "false")}}
            }
            """;

        var request = JsonSerializer.Deserialize<WidgetConfigEndpoints.UpdateWidgetConfigRequest>(body, WebOptions);

        Assert.NotNull(request);
        Assert.Equal(required, request.RequireContactConsent);

        // Turning the gate *off* deliberately has to stay possible - this is a full-replacement PUT and
        // a tenant may legitimately decide they do not want it. What is refused is silence, not "false".
        Assert.Equal("BottomLeft", request.Position);
    }
}
