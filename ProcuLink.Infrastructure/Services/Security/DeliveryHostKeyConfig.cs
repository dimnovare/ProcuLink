using System.Text.Json;
using System.Text.Json.Nodes;
using ProcuLink.Core.Services.Security;

namespace ProcuLink.Infrastructure.Services.Security;

/// <summary>
/// Where a supplier's trusted SSH host-key fingerprints live on a delivery config, and the only two
/// operations anything performs on them.
///
/// <para>
/// They live in <c>SupplierDeliveryConfig.ConfigJson</c> — the CLEARTEXT column — and that is
/// correct rather than a violation of its invariant: a fingerprint is the digest of a PUBLIC key.
/// The invariant on that column is that no SECRET goes in it, and this is the opposite of a secret;
/// an operator has to be able to read the value back and compare it against what their supplier
/// tells them, which is impossible for anything in <c>EncryptedCredentials</c>.
/// </para>
///
/// <para>
/// One type rather than a reader on the dispatcher and a writer on the service, because the key name
/// is the join between a value written after one delivery and a decision taken before the next: two
/// spellings of it would not fail any test, they would silently un-pin every supplier.
/// </para>
/// </summary>
public static class DeliveryHostKeyConfig
{
    /// <summary>The <c>ConfigJson</c> property holding this supplier's trusted host-key fingerprints.</summary>
    public const string Key = "hostKeyFingerprints";

    /// <summary>
    /// The fingerprints this connection already trusts. Empty ⇒ trust-on-first-use.
    ///
    /// <para>
    /// Tolerant of both spellings the property can hold — a JSON array (what <see cref="Write"/>
    /// produces) and a single JSON string (the most natural thing for an operator pasting one
    /// fingerprint by hand) — because refusing a working supplier's delivery over JSON punctuation
    /// would be its own defect. Anything else, including malformed config, reads as "nothing
    /// pinned": fabricating a pin nobody configured would block a supplier over an unrelated typo.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Read(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return [];

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!doc.RootElement.TryGetProperty(Key, out var value)) return [];

            return value.ValueKind switch
            {
                JsonValueKind.String => SshHostKeyPolicy.Parse(value.GetString()),
                JsonValueKind.Array => SshHostKeyPolicy.Parse(
                    string.Join('\n', value.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()))),
                _ => [],
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether the property is present at all — which is a different question from whether it reads
    /// as empty, and the distinction the save path turns on. An absent property is a client that has
    /// never heard of host keys; a present, empty one is an operator deliberately clearing the pin.
    /// </summary>
    public static bool IsPresent(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(Key, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The config to SAVE, given what a client sent and what is already stored.
    ///
    /// <para>
    /// This exists because the delivery-config save is a whole-object replace
    /// (<c>existing.ConfigJson = request.ConfigJson</c>), and no client sends a property it does not
    /// know about. Without this, an operator changing the timeout would silently un-pin the supplier,
    /// and the next connection would trust-on-first-use all over again — a verification feature that
    /// quietly disarms itself the first time anyone edits anything.
    /// </para>
    ///
    /// <para>
    /// Absent from the incoming config ⇒ keep what was recorded. Present ⇒ the operator is speaking,
    /// and what they sent wins, including an empty array or an empty string, which is the deliberate
    /// re-trust after a supplier genuinely rebuilds their server. Untouched when there is nothing to
    /// preserve, so an ordinary save is byte-for-byte what the client sent.
    /// </para>
    /// </summary>
    public static string PreserveRecordedFingerprints(string incomingConfigJson, string? storedConfigJson)
    {
        if (IsPresent(incomingConfigJson)) return incomingConfigJson;

        var recorded = Read(storedConfigJson);
        return recorded.Count == 0 ? incomingConfigJson : Write(incomingConfigJson, recorded);
    }

    /// <summary>
    /// The same config with the fingerprints replaced, and <b>every other property preserved</b>.
    /// That is the whole contract: this runs on a live delivery config carrying the host, the remote
    /// path, the timeout and the operator's <c>overwriteExisting</c> setting, and a write that
    /// rebuilt the object from the fields it knows about would silently reset the ones it does not.
    ///
    /// <para>
    /// An empty set removes the property, so "never pinned" stays distinguishable from "pinned to
    /// nothing" — and so clearing it is a real re-trust rather than a permanent refusal.
    /// </para>
    /// </summary>
    public static string Write(string? configJson, IEnumerable<string> fingerprints)
    {
        JsonObject root;
        try
        {
            root = (string.IsNullOrWhiteSpace(configJson) ? null : JsonNode.Parse(configJson)) as JsonObject
                   ?? new JsonObject();
        }
        catch (JsonException)
        {
            // Unparseable config is already broken for delivery — its own parse reports that. Do not
            // compound it by discarding whatever text is in there: refuse to write instead, and let
            // the connection stay unpinned rather than replacing the operator's config with ours.
            return configJson ?? "{}";
        }

        var normalised = SshHostKeyPolicy.Parse(SshHostKeyPolicy.Serialise(fingerprints));

        if (normalised.Count == 0)
        {
            root.Remove(Key);
        }
        else
        {
            var array = new JsonArray();
            foreach (var fingerprint in normalised) array.Add(fingerprint);
            root[Key] = array;
        }

        return root.ToJsonString();
    }
}
