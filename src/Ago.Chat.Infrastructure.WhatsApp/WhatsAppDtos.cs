using System.Text.Json.Serialization;

namespace Ago.Chat.Infrastructure.WhatsApp;

// `14-10`: WhatsApp's own wire shapes, entirely below the Infrastructure boundary
// (ChannelPortTests.NoProviderVocabulary_AppearsAboveInfrastructure) - nothing here is referenced by
// Ago.Chat.Domain, Ago.Chat.Application or Ago.Chat.Contracts, and WhatsAppChannelAdapter/
// WhatsAppInboundMessageParser/WhatsAppWebhookEndpoints are the only translators between this
// vocabulary and the channel-neutral one IInboundChannelAdapter defines.
//
// **Honesty note, the same discipline MaxDtos.cs/VkDtos.cs each state for themselves.** Unlike either
// precedent, Meta's own documentation (developers.facebook.com) was directly reachable from this
// environment, so every field name below is confirmed from Meta's own current Cloud API reference
// pages - the webhook payload shape (entry/changes/value/messages/metadata), the outbound send
// request/response shape, and the numeric error-code taxonomy (WhatsAppApiClient's own remarks) - not
// reconstructed from a third party or an SDK's source the way MAX's/VK's own DTOs had to be. What is
// NOT confirmed against a real delivery is the exact JSON error envelope's own field names
// (`message`/`type`/`code`/`error_subcode`/`fbtrace_id`) - Meta's fetched documentation described the
// numeric error codes in prose but did not render a literal example error response body during this
// item's own research; the envelope shape used below is the well-established, broadly-documented
// convention shared across every Graph API product (Meta's platform-wide error object), not a guess
// specific to WhatsApp - but it is worth naming as the one shape here taken from general Graph API
// knowledge rather than a page this item could point to directly. The first thing to fix once a real
// WhatsApp Business number and a real failing send exist.

/// <summary>
/// One webhook delivery's outer envelope - confirmed field names from Meta's own webhook payload
/// documentation. <see cref="Object"/> is always the literal string <c>"whatsapp_business_account"</c>
/// for this channel; kept as a plain string rather than a closed enum because nothing here branches on
/// it beyond a sanity check, the same "do not model what nothing reads" restraint <c>VkCallbackEvent.Type</c>'s
/// own remarks apply to VK's event kinds this item has no use for.
/// </summary>
public sealed record WhatsAppWebhookEnvelope(
    [property: JsonPropertyName("object")] string? Object,
    [property: JsonPropertyName("entry")] IReadOnlyList<WhatsAppEntry>? Entry);

/// <summary>
/// One WhatsApp Business Account's own batch of changes - an array by Meta's own design, because one
/// Meta App's single webhook URL can in principle receive a batched delivery covering more than one
/// onboarded tenant's account in one HTTP call. <see cref="WhatsAppInboundMessageParser"/> is written to
/// walk every entry and every change, unlike <c>VkInboundMessageParser</c>/<c>MaxInboundMessageParser</c>,
/// each of which only ever had to parse a single event per call - VK's and MAX's own wire shapes are
/// natively single-event; WhatsApp's is natively a batch container, and treating it as anything else
/// risks silently dropping a real, delivered message the first time Meta actually does batch two
/// together.
/// </summary>
public sealed record WhatsAppEntry(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("changes")] IReadOnlyList<WhatsAppChange>? Changes);

public sealed record WhatsAppChange(
    [property: JsonPropertyName("field")] string? Field,
    [property: JsonPropertyName("value")] WhatsAppChangeValue? Value);

/// <summary>
/// <see cref="Messages"/> and <see cref="Statuses"/> are mutually exclusive in practice - confirmed from
/// Meta's own webhook payload-examples documentation: an inbound visitor message carries
/// <see cref="Messages"/>, and Meta's own delivery-status callbacks (sent/delivered/read/failed for an
/// operator's own outbound reply) carry <see cref="Statuses"/> instead, both under the identical
/// <c>changes[].field == "messages"</c> discriminator - there is no separate field value to switch on the
/// way <c>VkCallbackEvent.Type</c> lets <c>VkInboundMessageParser</c> distinguish
/// <c>"message_new"</c> from everything else up front. This is WhatsApp's own version of the problem
/// <c>VkMessage.Out</c> solves for VK (a webhook that also delivers this system's own outbound activity
/// back to it) - shaped differently (a whole separate array, not a flag on the same one) but the same
/// underlying hazard: <see cref="WhatsAppInboundMessageParser"/> only ever looks at <see cref="Messages"/>,
/// so a status-only delivery is skipped by construction rather than by an explicit filter, the same
/// outcome VK's <c>out == 1</c> check reaches by a different route.
/// </summary>
public sealed record WhatsAppChangeValue(
    [property: JsonPropertyName("messaging_product")] string? MessagingProduct,
    [property: JsonPropertyName("metadata")] WhatsAppMetadata? Metadata,
    [property: JsonPropertyName("messages")] IReadOnlyList<WhatsAppMessage>? Messages,
    [property: JsonPropertyName("statuses")] System.Text.Json.JsonElement? Statuses);

/// <summary><see cref="PhoneNumberId"/> is the value this item resolves an inbound delivery's tenant
/// by - <c>ChannelCredential.ProviderAccountId</c>, looked up via
/// <c>IChannelCredentialRepository.GetActiveByProviderAccountIdAsync</c> - because, unlike every
/// precedent, WhatsApp's own webhook URL carries no per-tenant path segment at all
/// (<see cref="WhatsAppBotApiOptions"/>' own remarks on why the webhook is App-wide, not
/// per-credential).</summary>
public sealed record WhatsAppMetadata(
    [property: JsonPropertyName("display_phone_number")] string? DisplayPhoneNumber,
    [property: JsonPropertyName("phone_number_id")] string? PhoneNumberId);

/// <summary>One inbound message. <see cref="Type"/> is checked and only <c>"text"</c> is accepted -
/// WhatsApp's Cloud API delivers a dozen other message types (image, audio, location, an interactive
/// button reply) this item has no use case for, the identical "recognise the one shape this item
/// handles, skip the rest" restraint <c>VkInboundMessageParser</c>'s own remarks state for VK's own
/// event-type breadth. `14-06`'s structured-content item is the one that would give a non-text type
/// anywhere real to go; nothing here invents a text-only stand-in for a photo.</summary>
public sealed record WhatsAppMessage(
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("timestamp")] string? Timestamp,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] WhatsAppMessageText? Text);

public sealed record WhatsAppMessageText([property: JsonPropertyName("body")] string? Body);

// --- Outbound: POST /{version}/{phone-number-id}/messages ---
//
// Confirmed from Meta's own Cloud API messages reference, 2026-08-30: `messaging_product` is always
// the literal string "whatsapp" (Meta's own convention for every Cloud API call, not specific to this
// endpoint), `recipient_type` is "individual" for this item's own scope (a WhatsApp *group* recipient
// is a real, separate concept this item does not build toward), `to` is the recipient's own phone
// number as a plain string, `type` is "text" for a free-form reply.

public sealed record WhatsAppSendMessageRequest(
    [property: JsonPropertyName("messaging_product")] string MessagingProduct,
    [property: JsonPropertyName("recipient_type")] string RecipientType,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] WhatsAppMessageText Text);

/// <summary>A successful send's own response shape - confirmed from Meta's own reference documentation.
/// Unlike VK's bare-integer <c>response</c> or MAX's own message object, WhatsApp's success response
/// carries an array of both <c>contacts</c> and <c>messages</c> (Meta's own batching-shaped convention,
/// mirroring the inbound envelope) even though this item's own calls only ever send to one
/// recipient.</summary>
public sealed record WhatsAppSendMessageResponse(
    [property: JsonPropertyName("messaging_product")] string? MessagingProduct,
    [property: JsonPropertyName("messages")] IReadOnlyList<WhatsAppSentMessage>? Messages);

public sealed record WhatsAppSentMessage([property: JsonPropertyName("id")] string? Id);

/// <summary>Meta's own platform-wide Graph API error envelope - see this file's own honesty note for
/// why this specific shape is general Graph API knowledge rather than a page this item's own research
/// could point to directly.</summary>
public sealed record WhatsAppErrorEnvelope([property: JsonPropertyName("error")] WhatsAppApiError? Error);

public sealed record WhatsAppApiError(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("error_subcode")] int? ErrorSubcode,
    [property: JsonPropertyName("fbtrace_id")] string? FbtraceId);

/// <summary><c>GET /{version}/{phone-number-id}</c>'s own success shape - confirmed from Meta's own
/// phone-numbers reference. <see cref="WhatsAppApiClient.GetPhoneNumberAsync"/>'s own remarks explain
/// why this item calls it at connect time.</summary>
public sealed record WhatsAppPhoneNumberInfo(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("display_phone_number")] string? DisplayPhoneNumber,
    [property: JsonPropertyName("verified_name")] string? VerifiedName);
