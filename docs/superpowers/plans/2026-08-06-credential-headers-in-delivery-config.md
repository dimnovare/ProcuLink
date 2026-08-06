# Credential-bearing headers in `config_json` — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refuse, on the write paths only, a credential typed into a supplier delivery config's extra-headers map — which `config_json` stores in cleartext — while every pre-existing config keeps delivering and keeps being editable.

**Architecture:** One primitive in `ProcuLink.Core/Services/Delivery/DeliveryConfigTransport.cs` (the file PR #157 established) classifies header names and extracts them from a config blob. Both write paths call it, both read paths surface it through the existing `InsecureTransportWarning` field, and the HTTP dispatcher logs it on every attempt. Grandfathering an identical `(name, value)` pair already stored applies to every UPDATE of an existing row — the live delivery-config upsert and the revision draft update, which the mapper echoes the whole bundle through on every autosave. Only revision CREATE refuses flat, because a create has no stored predecessor.

**Tech Stack:** .NET 8, C# 12, EF Core (InMemory in tests), xUnit, FluentAssertions, Moq, `System.Text.Json`.

**Spec:** `docs/superpowers/specs/2026-08-06-credential-headers-in-delivery-config-design.md`

**Worktree:** `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\.claude\worktrees\cred-headers`
**Branch:** `security/refuse-credential-headers-in-delivery-config`, based on `security/validate-revision-delivery-config` (PR #157, **open**) — not `main`.

## Global Constraints

- **Never `git checkout <file>` to undo a mutation check.** It has destroyed uncommitted work in this repo. Restore by editing until `git diff HEAD` is empty.
- **Never echo a header value** — not in an exception message, not in a log line, not in an API response. Header *names* only.
- **Assert the refusal before asserting the message hides the secret.** A `NotContain(token)` assertion passes vacuously when the guard is disabled.
- **Assert the allowance alongside every refusal.** A rule that refused every header would pass a refusal-only suite.
- **Any walk over a runtime collection carries an anti-vacuity floor**, so an emptied collection cannot make the walk assert nothing.
- **No second copy of the rule.** Every consumer reaches `DeliveryConfigTransport.FindCredentialHeaders`. A hand-rolled name check anywhere else is the defect #157 exists to prevent.
- Error code, verbatim: `credential_header_in_delivery_config`
- Refusal message, verbatim (single header): `Delivery config header 'Authorization' holds a credential. This config is stored in cleartext, so credentials belong in this supplier's delivery credentials — set the auth type there to bearer, basic, apikey or oauth2_client_credentials — where they are encrypted. Remove the header and save the token as a credential instead.`
- Plural form replaces the first sentence with: `Delivery config headers 'A', 'B' hold credentials.`
- Run tests from the worktree root. Project paths: `ProcuLink.Infrastructure.Tests`, `ProcuLink.Api.Tests`.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `ProcuLink.Core/Services/Delivery/DeliveryConfigTransport.cs` | **modify** — the classifier, the extraction, the messages, the composer, the exception | 1, 2, 5 |
| `ProcuLink.Infrastructure/Services/DeliveryConfigService.cs` | **modify** — live write guard (grandfathered) + read surface | 3, 5 |
| `ProcuLink.Api/Controllers/SuppliersController.cs` | **modify** — 400 shape for the live path | 3 |
| `ProcuLink.Api/Services/SupplierConnectionService.cs` | **modify** — revision write guard (update grandfathered, create flat) | 4 |
| `ProcuLink.Api/Controllers/ConnectionsController.cs` | **modify** — 400 shape + revision read surface | 4, 5 |
| `ProcuLink.Api/Contracts/ConnectionDto.cs` | **modify** — widen the field's doc comment | 5 |
| `ProcuLink.Infrastructure/Services/Dispatchers/HttpDeliveryDispatcher.cs` | **modify** — dispatch-time log | 6 |
| `ProcuLink.Core/Entities/SupplierDeliveryConfig.cs` | **modify** — the invariant comment becomes true | 7 |
| `ProcuLink.Infrastructure.Tests/Security/CredentialHeaderNamesTests.cs` | **create** — classifier + extraction + grandfathering | 1, 2 |
| `ProcuLink.Infrastructure.Tests/Services/DeliveryConfigCredentialHeaderTests.cs` | **create** — live write path + read surface | 3, 5 |
| `ProcuLink.Api.Tests/Controllers/SuppliersControllerDeliveryConfigCredentialHeaderTests.cs` | **create** — live path 400 shape | 3 |
| `ProcuLink.Api.Tests/Services/ConnectionRevisionCredentialHeaderTests.cs` | **create** — revision write path | 4 |
| `ProcuLink.Api.Tests/Controllers/ConnectionRevisionCredentialHeaderControllerTests.cs` | **create** — revision 400 shape + DTO warning | 4, 5 |
| `ProcuLink.Infrastructure.Tests/Services/Dispatchers/HttpDeliveryDispatcherTests.cs` | **modify** — dispatch log case | 6 |

The companion **frontend** PR (spec §5.1) is a separate branch in the separate `project-proculink` repo and is **not** part of this plan.

---

### Task 1: The classifier

**Files:**
- Modify: `ProcuLink.Core/Services/Delivery/DeliveryConfigTransport.cs` (append after `DescribeInsecureTransport`, before the closing brace)
- Test: `ProcuLink.Infrastructure.Tests/Security/CredentialHeaderNamesTests.cs` (create)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `public static bool DeliveryConfigTransport.IsCredentialHeaderName(string? name)`
  - `public static IReadOnlyCollection<string> DeliveryConfigTransport.KnownCredentialHeaderNames { get; }`

- [ ] **Step 1: Write the failing test**

Create `ProcuLink.Infrastructure.Tests/Security/CredentialHeaderNamesTests.cs`:

```csharp
using FluentAssertions;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Tests.Security;

/// <summary>
/// Which delivery-config header names are treated as credential-bearing.
///
/// <para>The whole guard turns on this predicate, and it can fail in two directions that cost
/// different things. A false NEGATIVE leaves a secret in cleartext under a name someone chose
/// obscurely. A false POSITIVE hard-blocks a legitimate save — and the delivery editor has no
/// headers field, so there is no UI workaround. Both directions are therefore asserted, and the
/// rule is deliberately precise rather than aggressive: never bare <c>auth</c>, never bare
/// <c>key</c>.</para>
/// </summary>
public class CredentialHeaderNamesTests
{
    // ── Refused ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("AUTHORIZATION")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    [InlineData("X-Api-Key")]
    [InlineData("x-api-key")]
    [InlineData("ApiKey")]
    [InlineData("X-Auth-Token")]
    [InlineData("X-Access-Token")]
    [InlineData("Ocp-Apim-Subscription-Key")]
    [InlineData("Private-Token")]
    public void KnownCredentialNames_AreRefused(string name) =>
        DeliveryConfigTransport.IsCredentialHeaderName(name).Should().BeTrue();

    /// <summary>The segment rule, which is what catches a bespoke supplier-specific name.</summary>
    [Theory]
    [InlineData("X-Supplier-Token")]
    [InlineData("X-Acme-Secret")]
    [InlineData("X-Client-Password")]
    [InlineData("X-Legacy-Passwd")]
    [InlineData("X-Old-Pwd")]
    [InlineData("X-Supplier-Credentials")]
    [InlineData("X-Foo-Api-Key")]
    [InlineData("X-Aws-Access-Key")]
    [InlineData("X-Signing-Key")]
    [InlineData("X_Supplier_Token")]
    public void CredentialShapedNames_AreRefused(string name) =>
        DeliveryConfigTransport.IsCredentialHeaderName(name).Should().BeTrue();

    // ── Allowed ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The line the segment rule is drawn on. Every one of these is a header a real tenant sends,
    /// and refusing any of them would block a save with nowhere to go. <c>X-Idempotency-Key</c> and
    /// <c>X-Auth-Email</c> are the two that a sloppier rule (bare <c>key</c>, bare <c>auth</c>)
    /// would take out.
    /// </summary>
    [Theory]
    [InlineData("Content-Type")]
    [InlineData("Accept")]
    [InlineData("X-Correlation-Id")]
    [InlineData("X-Request-Id")]
    [InlineData("X-Supplier-Account")]
    [InlineData("X-Idempotency-Key")]
    [InlineData("X-Partition-Key")]
    [InlineData("X-Sort-Key")]
    [InlineData("X-Auth-Email")]
    [InlineData("X-Message-Id")]
    public void OrdinaryHeaders_AreAllowed(string name) =>
        DeliveryConfigTransport.IsCredentialHeaderName(name).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankNames_AreNotCredentials(string? name) =>
        DeliveryConfigTransport.IsCredentialHeaderName(name).Should().BeFalse();

    [Fact]
    public void SurroundingWhitespace_DoesNotEvadeTheRule() =>
        DeliveryConfigTransport.IsCredentialHeaderName("  Authorization  ").Should().BeTrue();

    /// <summary>
    /// Walks the published list itself, so an entry added to it can never be added without being
    /// covered. The count floor is the anti-vacuity guard: an emptied list would otherwise make
    /// this test assert nothing at all and still pass.
    /// </summary>
    [Fact]
    public void EveryKnownCredentialHeaderName_IsRefusedByThePredicate()
    {
        var names = DeliveryConfigTransport.KnownCredentialHeaderNames;

        names.Should().HaveCountGreaterThan(10,
            "an emptied or gutted list would make this walk assert nothing");

        foreach (var name in names)
            DeliveryConfigTransport.IsCredentialHeaderName(name)
                .Should().BeTrue($"'{name}' is on the published known-credential list");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~CredentialHeaderNamesTests"
```

Expected: **build failure** — `'DeliveryConfigTransport' does not contain a definition for 'IsCredentialHeaderName'`.

- [ ] **Step 3: Write minimal implementation**

In `ProcuLink.Core/Services/Delivery/DeliveryConfigTransport.cs`, append inside the class:

```csharp
    // ── Credential-bearing headers ───────────────────────────────────────────

    /// <summary>
    /// Header names that conventionally carry a credential, matched case-insensitively on the
    /// trimmed name.
    ///
    /// <para>Published rather than private so a test can walk it — an entry added here must not be
    /// addable without being covered — and so a UI could warn inline before a save is attempted.</para>
    /// </summary>
    public static IReadOnlyCollection<string> KnownCredentialHeaderNames => CredentialHeaderNames;

    private static readonly HashSet<string> CredentialHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "authentication",
        "cookie", "set-cookie",
        "x-api-key", "api-key", "apikey", "x-apikey",
        "x-auth-token", "x-authorization", "x-access-token", "x-auth-key",
        "x-amz-security-token", "x-goog-api-key", "x-functions-key",
        "ocp-apim-subscription-key", "private-token", "x-shopify-access-token",
    };

    /// <summary>
    /// Words that make a header name credential-bearing on their own, matched per hyphen/underscore
    /// segment.
    ///
    /// <para>Deliberately excludes bare <c>auth</c> and bare <c>key</c>. Including either would
    /// refuse <c>X-Auth-Email</c> and <c>X-Idempotency-Key</c> — headers real tenants send — and the
    /// delivery editor has no headers field, so a false refusal is a save an operator cannot make
    /// and cannot work around.</para>
    /// </summary>
    private static readonly HashSet<string> CredentialSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "secret", "password", "passwd", "pwd", "credential", "credentials", "apikey",
    };

    /// <summary>
    /// Adjacent segment pairs that are credential-bearing together though neither word is on its
    /// own — the reason bare <c>key</c> does not need to be.
    /// </summary>
    private static readonly HashSet<string> CredentialSegmentPairs = new(StringComparer.OrdinalIgnoreCase)
    {
        "api-key", "access-key", "secret-key", "private-key", "signing-key", "session-key",
    };

    private static readonly char[] HeaderNameSeparators = ['-', '_'];

    /// <summary>
    /// True when this header name conventionally carries a credential, by exact match or by
    /// segment. See <see cref="CredentialSegments"/> for why the segment rule is narrow.
    /// </summary>
    public static bool IsCredentialHeaderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var trimmed = name.Trim();
        if (CredentialHeaderNames.Contains(trimmed)) return true;

        var segments = trimmed.Split(HeaderNameSeparators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (CredentialSegments.Contains(segments[i])) return true;
            if (i + 1 < segments.Length
                && CredentialSegmentPairs.Contains($"{segments[i]}-{segments[i + 1]}"))
                return true;
        }

        return false;
    }
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~CredentialHeaderNamesTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Core/Services/Delivery/DeliveryConfigTransport.cs ProcuLink.Infrastructure.Tests/Security/CredentialHeaderNamesTests.cs
git commit -m "security: classify credential-bearing delivery header names"
```

---

### Task 2: Extraction and grandfathering

**Files:**
- Modify: `ProcuLink.Core/Services/Delivery/DeliveryConfigTransport.cs`
- Test: `ProcuLink.Infrastructure.Tests/Security/CredentialHeaderNamesTests.cs` (extend)

**Interfaces:**
- Consumes: `IsCredentialHeaderName` from Task 1.
- Produces:
  - `public static IReadOnlyList<string> FindCredentialHeaders(string? configJson, string? storedConfigJson = null)`
  - `public static string? DescribeCredentialHeaders(string? configJson)`
  - `internal static string BuildCredentialHeaderMessage(IReadOnlyList<string> names)`
  - `public sealed class CredentialHeaderInConfigException : ArgumentException` with `const string Code`, `IReadOnlyList<string> HeaderNames`, `string PolicyMessage`

- [ ] **Step 1: Write the failing test**

Append to `ProcuLink.Infrastructure.Tests/Security/CredentialHeaderNamesTests.cs`, inside the class. Add `using System.Text.Json;` to the top of the file.

```csharp
    // ── Extraction ───────────────────────────────────────────────────────────

    [Fact]
    public void AHeaderMapWithACredential_IsFound() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"url":"https://s.example/o","headers":{"Authorization":"Bearer t0ps3cret"}}""")
            .Should().ContainSingle().Which.Should().Be("Authorization");

    [Fact]
    public void AHeaderMapWithoutOne_IsClean() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"url":"https://s.example/o","headers":{"Content-Type":"application/xml","X-Correlation-Id":"abc"}}""")
            .Should().BeEmpty();

    [Theory]
    [InlineData("""{"url":"https://s.example/o"}""")]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not json at all")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{"headers":"a string, not a map"}""")]
    public void BlobsWithNoHeaderMap_AreClean(string? configJson) =>
        DeliveryConfigTransport.FindCredentialHeaders(configJson).Should().BeEmpty();

    /// <summary>
    /// The dispatchers deserialize with <c>PropertyNameCaseInsensitive = true</c>, so
    /// <c>{"HEADERS":{"AUTHORIZATION":…}}</c> binds and is sent. A lookup that matched only the
    /// exact lowercase key would be bypassed by changing one character.
    /// </summary>
    [Theory]
    [InlineData("HEADERS")]
    [InlineData("Headers")]
    [InlineData("hEaDeRs")]
    public void TheHeadersKeyInAnyCasing_IsStillInspected(string key) =>
        DeliveryConfigTransport.FindCredentialHeaders(
                $$$"""{"{{{key}}}":{"Authorization":"Bearer t0ps3cret"}}""")
            .Should().ContainSingle();

    [Fact]
    public void TwoCredentialHeaders_AreBothNamed() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"headers":{"Authorization":"Bearer a","X-Api-Key":"b","Content-Type":"application/xml"}}""")
            .Should().Equal("Authorization", "X-Api-Key");

    /// <summary>
    /// The #157 trap, applied to the headers key. A JSON object may repeat a key and
    /// System.Text.Json keeps both: <see cref="JsonDocument"/> enumerates them in document order
    /// while <c>JsonSerializer.Deserialize</c> — what the dispatcher uses — binds the LAST.
    /// Inspecting only the first would validate the clean map and deliver the credential-bearing
    /// one.
    ///
    /// <para>The bypass is confirmed against the REAL serializer first, not reasoned about, so this
    /// test still means something if System.Text.Json ever changes which duplicate wins.</para>
    /// </summary>
    [Fact]
    public void ARepeatedHeadersKey_CannotHideACredential()
    {
        const string blob = """
            {"headers":{"Content-Type":"application/xml"},"headers":{"Authorization":"Bearer t0ps3cret"}}
            """;

        // What the dispatcher will actually send.
        var bound = JsonSerializer.Deserialize<HeaderProbe>(
            blob, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        bound!.Headers.Should().ContainKey("Authorization",
            "the deserializer binds the LAST duplicate — that is the map that reaches the wire");

        DeliveryConfigTransport.FindCredentialHeaders(blob)
            .Should().ContainSingle().Which.Should().Be("Authorization");
    }

    private sealed record HeaderProbe(Dictionary<string, string> Headers);

    // ── Grandfathering ───────────────────────────────────────────────────────

    private const string StoredWithToken =
        """{"url":"https://s.example/o","headers":{"Authorization":"Bearer t0ps3cret"}}""";

    /// <summary>
    /// The case the whole design turns on. The delivery editor has no headers field, so it carries
    /// the stored map through every save untouched. Refusing that identical echo would lock an
    /// operator out of changing a timeout, and there would be no UI anywhere to remove the header.
    /// </summary>
    [Fact]
    public void AnUnchangedRoundTripOfAStoredHeader_IsAllowed() =>
        DeliveryConfigTransport.FindCredentialHeaders(StoredWithToken, StoredWithToken)
            .Should().BeEmpty();

    [Fact]
    public void AnUnchangedHeaderAlongsideAnUnrelatedEdit_IsAllowed() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"url":"https://s.example/o","timeoutSeconds":60,"headers":{"Authorization":"Bearer t0ps3cret"}}""",
                StoredWithToken)
            .Should().BeEmpty();

    [Fact]
    public void AddingACredentialHeader_IsRefusedEvenWhenSomethingElseWasStored() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"headers":{"Content-Type":"application/xml","X-Api-Key":"new"}}""",
                """{"headers":{"Content-Type":"application/xml"}}""")
            .Should().ContainSingle().Which.Should().Be("X-Api-Key");

    /// <summary>Rotation is a WRITE of a secret, which is exactly what this refuses.</summary>
    [Fact]
    public void ChangingTheValueOfAStoredCredentialHeader_IsRefused() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"url":"https://s.example/o","headers":{"Authorization":"Bearer rotated"}}""",
                StoredWithToken)
            .Should().ContainSingle().Which.Should().Be("Authorization");

    [Fact]
    public void RemovingAStoredCredentialHeader_IsAllowed() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"url":"https://s.example/o","headers":{"Content-Type":"application/xml"}}""",
                StoredWithToken)
            .Should().BeEmpty();

    [Fact]
    public void WithNoStoredBlob_NothingIsGrandfathered() =>
        DeliveryConfigTransport.FindCredentialHeaders(StoredWithToken, storedConfigJson: null)
            .Should().ContainSingle();

    /// <summary>
    /// A client that re-serialises the blob may change only the escaping of a value. That is not a
    /// rotated secret and must not be treated as one, or an unchanged round-trip would start being
    /// refused for a reason no operator could see.
    /// </summary>
    [Fact]
    public void AReEscapedButIdenticalValue_IsStillGrandfathered() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"headers":{"Authorization":"Bearer A1"}}""",
                """{"headers":{"Authorization":"Bearer \u00411"}}""")
            .Should().BeEmpty();

    [Fact]
    public void TheStoredHeaderNameMatchesCaseInsensitively() =>
        DeliveryConfigTransport.FindCredentialHeaders(
                """{"headers":{"authorization":"Bearer t0ps3cret"}}""",
                StoredWithToken)
            .Should().BeEmpty();

    // ── The operator-facing message ──────────────────────────────────────────

    /// <summary>
    /// The refusal is asserted FIRST. Asserting only that the message hides the token would pass
    /// vacuously the moment the guard stopped producing a message at all.
    /// </summary>
    [Fact]
    public void TheMessageNamesTheHeaderAndNeverItsValue()
    {
        DeliveryConfigTransport.FindCredentialHeaders(StoredWithToken).Should().NotBeEmpty();

        var message = DeliveryConfigTransport.DescribeCredentialHeaders(StoredWithToken);

        message.Should().NotBeNullOrWhiteSpace();
        message.Should().Contain("'Authorization'");
        message.Should().Contain("Remove the header and save the token as a credential instead.");
        message.Should().NotContain("t0ps3cret");
        message.Should().NotContain("Bearer t0ps3cret");
    }

    [Fact]
    public void TwoOffendingHeaders_ReadAsAPlural()
    {
        var message = DeliveryConfigTransport.DescribeCredentialHeaders(
            """{"headers":{"Authorization":"Bearer a","X-Api-Key":"b"}}""");

        message.Should().StartWith("Delivery config headers 'Authorization', 'X-Api-Key' hold credentials.");
    }

    [Fact]
    public void ACleanConfig_HasNoMessage() =>
        DeliveryConfigTransport.DescribeCredentialHeaders(
                """{"url":"https://s.example/o","headers":{"Content-Type":"application/xml"}}""")
            .Should().BeNull();
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~CredentialHeaderNamesTests"
```

Expected: **build failure** — `'DeliveryConfigTransport' does not contain a definition for 'FindCredentialHeaders'`.

- [ ] **Step 3: Write minimal implementation**

In `ProcuLink.Core/Services/Delivery/DeliveryConfigTransport.cs`, append inside the class after `IsCredentialHeaderName`:

```csharp
    /// <summary>
    /// Every credential-bearing header name in the blob, in document order, deduped
    /// case-insensitively. Empty means there is nothing to refuse.
    ///
    /// <para><strong>Every <c>headers</c>-keyed object is inspected, not the first.</strong> Same
    /// trap as <see cref="ExtractUrls"/>: a JSON object may repeat a key and System.Text.Json keeps
    /// both — <see cref="JsonDocument"/> enumerates them in document order while
    /// <c>JsonSerializer.Deserialize</c>, what the dispatcher uses, binds the LAST. Inspecting one
    /// of them would validate the clean map and deliver the credential-bearing one.</para>
    ///
    /// <para><strong>Not protocol-scoped.</strong> Only the http connector declares a headers map
    /// today, but a guard scoped to a protocol list goes stale in one direction — a protocol that
    /// later grows one inherits no protection and nothing fails. Inspecting the key wherever it
    /// appears costs nothing and cannot produce a false refusal.</para>
    ///
    /// <para><paramref name="storedConfigJson"/> grandfathers a header whose name AND value are
    /// already persisted, so an unchanged round-trip is not treated as a write of a secret. The
    /// delivery editor has no headers field and carries the stored map through every save untouched;
    /// refusing that echo would lock an operator out of every unrelated edit with no way to remove
    /// the header. Adding one, or rotating its value, is still refused. Pass null — the default —
    /// to grandfather nothing.</para>
    /// </summary>
    public static IReadOnlyList<string> FindCredentialHeaders(
        string? configJson, string? storedConfigJson = null)
    {
        var incoming = ReadHeaderEntries(configJson);
        if (incoming.Count == 0) return Array.Empty<string>();

        var stored = ReadHeaderEntries(storedConfigJson);

        List<string>? offending = null;
        HashSet<string>? seen = null;

        foreach (var (name, value) in incoming)
        {
            if (!IsCredentialHeaderName(name)) continue;
            if (IsAlreadyStored(stored, name, value)) continue;

            seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!seen.Add(name.Trim())) continue;

            (offending ??= new List<string>()).Add(name.Trim());
        }

        return (IReadOnlyList<string>?)offending ?? Array.Empty<string>();
    }

    /// <summary>
    /// An operator-facing message naming the credential-bearing headers a stored config carries, or
    /// null when it carries none. Names only — echoing the value is the defect itself.
    /// </summary>
    public static string? DescribeCredentialHeaders(string? configJson)
    {
        var names = FindCredentialHeaders(configJson);
        return names.Count == 0 ? null : BuildCredentialHeaderMessage(names);
    }

    /// <summary>
    /// The one wording, shared by the refusal and the read-path warning so they cannot drift. It
    /// names the destination concretely — the connector manifest really does carry
    /// <c>type</c> + <c>header</c> + <c>value</c> under credentials — so an operator following it
    /// lands on a field that exists.
    /// </summary>
    internal static string BuildCredentialHeaderMessage(IReadOnlyList<string> names)
    {
        var quoted = string.Join(", ", names.Select(n => $"'{n}'"));
        var subject = names.Count == 1
            ? $"Delivery config header {quoted} holds a credential."
            : $"Delivery config headers {quoted} hold credentials.";

        return subject
            + " This config is stored in cleartext, so credentials belong in this supplier's delivery"
            + " credentials — set the auth type there to bearer, basic, apikey or oauth2_client_credentials — where they"
            + " are encrypted. Remove the header and save the token as a credential instead.";
    }

    /// <summary>
    /// Every (name, comparable value) pair under EVERY <c>headers</c>-keyed object in the blob.
    /// An unparseable blob yields nothing: <c>ValidateConfigJson</c> already refuses those on the
    /// save path, and failing here as well would turn a parse error into a security refusal.
    /// </summary>
    private static List<(string Name, string Value)> ReadHeaderEntries(string? configJson)
    {
        var entries = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(configJson)) return entries;

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return entries;

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "headers", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.Object) continue;

                foreach (var header in property.Value.EnumerateObject())
                    entries.Add((header.Name, ComparableValue(header.Value)));
            }
        }
        catch (JsonException)
        {
            return entries;
        }

        return entries;
    }

    /// <summary>
    /// The token two blobs are compared by. A JSON string is compared DECODED, so a client that
    /// re-serialises <c>"\u00411"</c> as <c>"A1"</c> has not rotated the secret and is not refused;
    /// anything else is compared by raw text, which is exact for every other value kind.
    /// </summary>
    private static string ComparableValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();

    private static bool IsAlreadyStored(
        List<(string Name, string Value)> stored, string name, string value)
    {
        foreach (var (storedName, storedValue) in stored)
            if (string.Equals(storedName.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(storedValue, value, StringComparison.Ordinal))
                return true;

        return false;
    }
```

Then append, **after** the closing brace of the `DeliveryConfigTransport` class (same file):

```csharp
/// <summary>
/// Thrown by the delivery-config write paths when a caller puts a credential into the extra-headers
/// map of <c>config_json</c>, which is stored in cleartext by design.
///
/// <para>Derives from <see cref="ArgumentException"/> so a handler that is not updated still answers
/// 400 rather than 500, exactly as <see cref="OutboundUrlPolicyException"/> does;
/// <see cref="Code"/> and <see cref="PolicyMessage"/> let one that is updated return the same
/// machine-readable shape the transport refusal already uses. No <c>paramName</c> is passed, because
/// ArgumentException's <c>(Parameter '…')</c> suffix is right for a log and wrong for a body an
/// operator reads.</para>
/// </summary>
public sealed class CredentialHeaderInConfigException : ArgumentException
{
    public const string Code = "credential_header_in_delivery_config";

    /// <summary>The offending header NAMES. Never their values.</summary>
    public IReadOnlyList<string> HeaderNames { get; }

    public string PolicyMessage { get; }

    public CredentialHeaderInConfigException(IReadOnlyList<string> headerNames)
        : this(headerNames, DeliveryConfigTransport.BuildCredentialHeaderMessage(headerNames))
    {
    }

    private CredentialHeaderInConfigException(IReadOnlyList<string> headerNames, string message)
        : base(message)
    {
        HeaderNames = headerNames;
        PolicyMessage = message;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~CredentialHeaderNamesTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Core/Services/Delivery/DeliveryConfigTransport.cs ProcuLink.Infrastructure.Tests/Security/CredentialHeaderNamesTests.cs
git commit -m "security: find credential headers in a delivery config, grandfathering what is stored"
```

---

### Task 3: The live delivery-config write path

**Files:**
- Modify: `ProcuLink.Infrastructure/Services/DeliveryConfigService.cs` (`UpsertAsync`, plus a new private validator beside `ValidateTransportSecurity`)
- Modify: `ProcuLink.Api/Controllers/SuppliersController.cs:794` (add a catch **before** the existing `catch (ArgumentException ex)`)
- Test: `ProcuLink.Infrastructure.Tests/Services/DeliveryConfigCredentialHeaderTests.cs` (create)
- Test: `ProcuLink.Api.Tests/Controllers/SuppliersControllerDeliveryConfigCredentialHeaderTests.cs` (create)

**Interfaces:**
- Consumes: `FindCredentialHeaders`, `CredentialHeaderInConfigException` from Task 2.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing test**

Create `ProcuLink.Infrastructure.Tests/Services/DeliveryConfigCredentialHeaderTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// The cleartext invariant on <c>SupplierDeliveryConfig.ConfigJson</c>, enforced at the live
/// delivery-config write path.
///
/// <para><b>The defect.</b> The column is cleartext by design and its doc comment says no credential
/// may ever be written into it — every secret belongs AES-GCM encrypted in
/// <c>EncryptedCredentials</c>. The HTTP channel's extra-headers map broke that in prose only: an
/// operator typing <c>Authorization: Bearer …</c> had the token stored in clear, returned by GET,
/// and copied into every connection-revision snapshot.</para>
///
/// <para><b>Why the grandfather.</b> The delivery editor has no headers field — <c>headers</c> is an
/// unmanaged key carried through every save untouched — so a flat refusal would block an operator
/// from changing a timeout with no UI anywhere to remove the header. An identical round-trip is
/// therefore not a write; adding or rotating one is.</para>
/// </summary>
public class DeliveryConfigCredentialHeaderTests
{
    private const string Token = "t0ps3cret";
    private static readonly string WithToken =
        $$"""{"url":"https://supplier.example/orders","headers":{"Authorization":"Bearer {{Token}}"}}""";
    private const string Clean =
        """{"url":"https://supplier.example/orders","headers":{"Content-Type":"application/xml"}}""";

    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeliveryConfigServiceTests.DeliveryConfigTestDbContext(options);
    }

    private static DeliveryConfigService CreateService(ProcuLinkDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        return new DeliveryConfigService(db, new DeliveryEncryptionService(config));
    }

    private static Task<DeliveryConfigResponse> SaveAsync(
        DeliveryConfigService service, Guid orgId, Guid supplierId, string configJson) =>
        service.UpsertAsync(
            orgId, supplierId,
            new UpsertDeliveryConfigRequest(DeliveryProtocolConstants.Http, false, configJson, null),
            default);

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertAsync_WithAnAuthorizationHeader_IsRefusedAndSavesNothing()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var act = () => SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(), WithToken);

        var thrown = await act.Should().ThrowAsync<CredentialHeaderInConfigException>();
        thrown.And.Code.Should().Be("credential_header_in_delivery_config");
        thrown.And.HeaderNames.Should().Equal("Authorization");

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// The refusal is asserted BEFORE the message is checked for the token. Asserting only that the
    /// message hides it would pass vacuously the moment the guard stopped refusing at all.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_RefusalMessage_NamesTheHeaderAndNeverTheToken()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var act = () => SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(), WithToken);

        var thrown = await act.Should().ThrowAsync<CredentialHeaderInConfigException>();

        thrown.And.PolicyMessage.Should().Contain("'Authorization'");
        thrown.And.PolicyMessage.Should().NotContain(Token);
        thrown.And.Message.Should().NotContain(Token);
    }

    [Fact]
    public async Task UpsertAsync_AddingACredentialHeaderToAnExistingConfig_IsRefused()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await SaveAsync(service, orgId, supplierId, Clean);

        var act = () => SaveAsync(service, orgId, supplierId, WithToken);

        (await act.Should().ThrowAsync<CredentialHeaderInConfigException>())
            .And.HeaderNames.Should().Equal("Authorization");
    }

    // ── Allowances (a rule that refused everything would pass a refusal-only suite) ──

    [Fact]
    public async Task UpsertAsync_WithOrdinaryHeaders_Saves()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var saved = await SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(),
            """{"url":"https://supplier.example/orders","headers":{"Content-Type":"application/xml","X-Correlation-Id":"abc","X-Supplier-Account":"ACME-4417"}}""");

        saved.ConfigJson.Should().Contain("X-Supplier-Account");
        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UpsertAsync_WithNoHeadersAtAll_Saves()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(),
            """{"url":"https://supplier.example/orders","timeoutSeconds":30}""");

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(1);
    }

    // ── Grandfathering at the service level ──────────────────────────────────

    /// <summary>
    /// Writes the row straight to the database, bypassing the service — the only way a config the
    /// rule now refuses can exist, i.e. one saved before enforcement did.
    /// </summary>
    private static async Task SeedLegacyConfigAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, string configJson)
    {
        db.SupplierDeliveryConfigs.Add(new ProcuLink.Core.Entities.SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = DeliveryProtocolConstants.Http,
            ConfigJson = configJson,
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            UpdatedAt = DateTime.UtcNow.AddDays(-30),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task UpsertAsync_UnchangedRoundTripOfALegacyHeader_StillSaves()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedLegacyConfigAsync(db, orgId, supplierId, WithToken);

        var saved = await SaveAsync(service, orgId, supplierId, WithToken);

        saved.Should().NotBeNull();
    }

    /// <summary>
    /// The realistic migration case: an operator changes the timeout on a supplier whose config
    /// predates enforcement. That must not be blocked — there is no UI to remove the header.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_AnUnrelatedEditBesideALegacyHeader_StillSaves()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedLegacyConfigAsync(db, orgId, supplierId, WithToken);

        var saved = await SaveAsync(service, orgId, supplierId,
            $$"""{"url":"https://supplier.example/orders","timeoutSeconds":90,"headers":{"Authorization":"Bearer {{Token}}"}}""");

        saved.ConfigJson.Should().Contain("90");
    }

    [Fact]
    public async Task UpsertAsync_RotatingALegacyHeaderValue_IsRefused()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedLegacyConfigAsync(db, orgId, supplierId, WithToken);

        var act = () => SaveAsync(service, orgId, supplierId,
            """{"url":"https://supplier.example/orders","headers":{"Authorization":"Bearer rotated-value"}}""");

        (await act.Should().ThrowAsync<CredentialHeaderInConfigException>())
            .And.HeaderNames.Should().Equal("Authorization");
    }

    [Fact]
    public async Task UpsertAsync_RemovingALegacyHeader_Saves()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedLegacyConfigAsync(db, orgId, supplierId, WithToken);

        var saved = await SaveAsync(service, orgId, supplierId, Clean);

        saved.ConfigJson.Should().NotContain("Authorization");
    }

    // ── The invariant, verified against the database ─────────────────────────

    /// <summary>
    /// The documented invariant is that the token is not in the column. Read it back and check,
    /// rather than trusting the refusal — with a positive control in the same test so this cannot
    /// pass by refusing everything.
    /// </summary>
    [Fact]
    public async Task ARefusedCredentialHeader_IsNowhereInThePersistedConfigJson()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var act = () => SaveAsync(service, orgId, supplierId, WithToken);
        await act.Should().ThrowAsync<CredentialHeaderInConfigException>();

        (await db.SupplierDeliveryConfigs.AsNoTracking().ToListAsync())
            .Should().OnlyContain(c => !c.ConfigJson.Contains(Token));

        // Positive control: the same endpoint really does persist an ordinary header, so the
        // assertion above is not passing because nothing can be saved at all.
        var saved = await SaveAsync(service, orgId, supplierId, Clean);
        saved.ConfigJson.Should().Contain("Content-Type");
    }
}
```

Then create `ProcuLink.Api.Tests/Controllers/SuppliersControllerDeliveryConfigCredentialHeaderTests.cs`. **Before writing it**, open `ProcuLink.Api.Tests/Controllers/ConnectionRevisionTransportSecurityControllerTests.cs` and copy its `Build()` harness shape; then read `SuppliersController`'s constructor and the `PUT` delivery-config action signature (around `SuppliersController.cs:749-798`) and construct the controller with the same mocked dependencies (`ICurrentTenantService` returning a fixed org id, `IBillingService.HasFeatureAsync` returning `true` for every feature so a billing 403 cannot answer before the rule under test runs). The single assertion that matters:

```csharp
    /// <summary>
    /// The refusal must reach the caller as a 400 with the machine-readable code — not the 500 an
    /// unhandled exception would give, and not the bare `{ error: "<message> (Parameter …)" }` the
    /// generic ArgumentException catch produces.
    /// </summary>
    [Fact]
    public async Task UpsertDeliveryConfig_WithACredentialHeader_Returns400WithTheCode_AndNeverEchoesTheToken()
    {
        var h = Build();

        var result = await h.Controller.UpsertDeliveryConfig(
            h.SupplierId,
            new UpsertDeliveryConfigRequest(
                DeliveryProtocolConstants.Http, false,
                $$"""{"url":"https://supplier.example/orders","headers":{"Authorization":"Bearer {{Token}}"}}""",
                null),
            CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var value = (dynamic)bad.Value!;
        ((string)value.error).Should().Be("credential_header_in_delivery_config");
        ((string)value.message).Should().Contain("'Authorization'");
        ((string)value.message).Should().NotContain(Token);
    }

    /// <summary>An ordinary header still saves through the same action — otherwise the test above
    /// would pass for a rule that refused everything.</summary>
    [Fact]
    public async Task UpsertDeliveryConfig_WithAnOrdinaryHeader_Succeeds()
    {
        var h = Build();

        var result = await h.Controller.UpsertDeliveryConfig(
            h.SupplierId,
            new UpsertDeliveryConfigRequest(
                DeliveryProtocolConstants.Http, false,
                """{"url":"https://supplier.example/orders","headers":{"X-Correlation-Id":"abc"}}""",
                null),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~DeliveryConfigCredentialHeaderTests"
```

Expected: refusal tests FAIL — no exception is thrown, the config saves. Allowance tests PASS already (nothing refuses yet), which is correct: they are the control.

- [ ] **Step 3: Write minimal implementation**

In `ProcuLink.Infrastructure/Services/DeliveryConfigService.cs`, inside `UpsertAsync`, immediately after the `existing` fetch and **before** the `if (existing is null)` block:

```csharp
        var existing = await _db.SupplierDeliveryConfigs
            .Where(x => x.OrgId == orgId && x.SupplierId == supplierId)
            .FirstOrDefaultAsync(ct);

        // Before ANY mutation. config_json is cleartext by design, so a credential typed into the
        // extra-headers map is refused here rather than at dispatch — refusing mid-flight would
        // strand orders. Grandfathered against the STORED blob deliberately: the delivery editor has
        // no headers field and carries the stored map through every save untouched, so refusing that
        // identical echo would lock an operator out of changing a timeout with no UI anywhere to
        // remove the header. Adding one, or rotating its value, is still refused.
        ValidateCredentialHeaders(request.ConfigJson, existing?.ConfigJson);
```

And beside `ValidateTransportSecurity` at the bottom of the class:

```csharp
    /// <summary>
    /// Refuses a credential written into the delivery config's extra-headers map. Every entry of
    /// that map is applied to the outbound request by <c>HttpDeliveryDispatcher</c>, so
    /// <c>Authorization: Bearer …</c> typed there is a real credential stored in a cleartext column,
    /// returned by GET, and copied into every connection-revision snapshot.
    ///
    /// <para>Deliberately the SAME primitive the revision write path runs
    /// (<c>SupplierConnectionService.ValidateCredentialHeaders</c>): both reach
    /// <see cref="DeliveryConfigTransport.FindCredentialHeaders"/>. Two copies of a security rule is
    /// how the transport gap existed, and #157 exists to stop it happening twice.</para>
    /// </summary>
    private static void ValidateCredentialHeaders(string configJson, string? storedConfigJson)
    {
        var offending = DeliveryConfigTransport.FindCredentialHeaders(configJson, storedConfigJson);
        if (offending.Count > 0)
            throw new CredentialHeaderInConfigException(offending);
    }
```

In `ProcuLink.Api/Controllers/SuppliersController.cs`, insert **before** the existing `catch (ArgumentException ex)` at `:794`:

```csharp
        catch (CredentialHeaderInConfigException ex)
        {
            // A credential typed into config_json's extra-headers map is the caller's mistake, so it
            // is a 400 — and it carries the machine-readable code rather than falling through to the
            // generic ArgumentException catch, whose body is the message plus ArgumentException's
            // "(Parameter '…')" suffix. The message names the header and never its value.
            return BadRequest(new { error = CredentialHeaderInConfigException.Code, message = ex.PolicyMessage });
        }
```

Add `using ProcuLink.Core.Services.Delivery;` to the file if it is not already present.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~DeliveryConfigCredentialHeaderTests"
dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~SuppliersControllerDeliveryConfigCredentialHeaderTests"
```

Expected: both PASS.

- [ ] **Step 5: Mutation-check the guard**

Delete the `ValidateCredentialHeaders(request.ConfigJson, existing?.ConfigJson);` line from `UpsertAsync`. Re-run:

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~DeliveryConfigCredentialHeaderTests"
```

Expected: **RED**. Record the failure count in the commit body.

Restore the line **by typing it back** — never `git checkout`. Confirm with:

```bash
git diff HEAD --stat
```

Expected: only the files you intend to commit appear, with the guard line present.

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Infrastructure/Services/DeliveryConfigService.cs ProcuLink.Api/Controllers/SuppliersController.cs ProcuLink.Infrastructure.Tests/Services/DeliveryConfigCredentialHeaderTests.cs ProcuLink.Api.Tests/Controllers/SuppliersControllerDeliveryConfigCredentialHeaderTests.cs
git commit -m "security: refuse a credential header on the live delivery-config save"
```

---

### Task 4: The connection-revision write path

**Files:**
- Modify: `ProcuLink.Api/Services/SupplierConnectionService.cs` (`ApplyScalars` at `:753`, plus a validator beside `ValidateTransportSecurity` at `:725`)
- Modify: `ProcuLink.Api/Controllers/ConnectionsController.cs` (catches in `CreateDraft` at `:175` and `UpdateDraft` at `:200`, plus a helper beside `RejectedCredentialsRef` at `:231`)
- Test: `ProcuLink.Api.Tests/Services/ConnectionRevisionCredentialHeaderTests.cs` (create)
- Test: `ProcuLink.Api.Tests/Controllers/ConnectionRevisionCredentialHeaderControllerTests.cs` (create)

**Interfaces:**
- Consumes: `FindCredentialHeaders`, `CredentialHeaderInConfigException` from Task 2.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing test**

Create `ProcuLink.Api.Tests/Services/ConnectionRevisionCredentialHeaderTests.cs`. Copy the `MakeDb`, `MakeSvc`, `Bundle`, `SeedAsync` and `SeedLegacyRevisionAsync` helpers verbatim from `ProcuLink.Api.Tests/Services/ConnectionRevisionTransportSecurityTests.cs:49-119` — they are private to that class, so they are duplicated rather than shared. Then:

```csharp
/// <summary>
/// The cleartext invariant at the connection-revision write path — the second way a delivery
/// endpoint's configuration is chosen, and the one a pinned order actually delivers through.
///
/// <para><b>Flat, with no grandfathering, unlike the live delivery-config path.</b> This input is
/// caller-supplied, exactly as the transport rule's is, and the paths that carry an ALREADY-LIVE
/// bundle — clone-from-active, rollback, republish-from-live, publish, the V1 backfill — never reach
/// <c>ApplyScalars</c>. Republish-from-live is the one the delivery-config editor triggers, so the
/// ordinary operator flow keeps working after a grandfathered live save. Nothing pre-existing is
/// stranded by refusing here, and those paths are pinned below.</para>
/// </summary>
public class ConnectionRevisionCredentialHeaderTests
{
    private const string Token = "t0ps3cret";
    private static readonly string WithToken =
        $$"""{"url":"https://supplier.example/orders","headers":{"Authorization":"Bearer {{Token}}"}}""";
    private const string Clean =
        """{"url":"https://supplier.example/orders","headers":{"X-Correlation-Id":"abc"}}""";

    // … MakeDb / MakeSvc / Bundle / SeedAsync / SeedLegacyRevisionAsync copied as described above …

    [Fact]
    public async Task CreateDraft_WithACredentialHeader_IsRefusedAndPersistsNothing()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var act = () => svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(WithToken), cloneFromActive: false, "user", CancellationToken.None);

        (await act.Should().ThrowAsync<CredentialHeaderInConfigException>())
            .And.HeaderNames.Should().Equal("Authorization");
        db.SupplierConnectionRevisions.Should().BeEmpty("a refused draft must not leave a row behind");
    }

    /// <summary>Refusal asserted before the message is checked, so the NotContain cannot pass vacuously.</summary>
    [Fact]
    public async Task CreateDraft_RefusalMessage_NeverCarriesTheToken()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var act = () => svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(WithToken), cloneFromActive: false, "user", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<CredentialHeaderInConfigException>();
        thrown.And.PolicyMessage.Should().Contain("'Authorization'");
        thrown.And.PolicyMessage.Should().NotContain(Token);
    }

    [Fact]
    public async Task UpdateDraft_WithACredentialHeader_IsRefusedAndLeavesTheStoredConfigUntouched()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var rev = await SeedLegacyRevisionAsync(db, conn, "draft", Clean);

        var act = () => svc.UpdateDraftAsync(
            orgId, conn.Id, rev.Id, Bundle(WithToken), CancellationToken.None);

        await act.Should().ThrowAsync<CredentialHeaderInConfigException>();

        var reread = await db.SupplierConnectionRevisions.AsNoTracking().SingleAsync(r => r.Id == rev.Id);
        reread.DeliveryConfigJson.Should().Be(Clean);
        reread.DeliveryConfigJson.Should().NotContain(Token);
    }

    // ── Allowances ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDraft_WithOrdinaryHeaders_Succeeds()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var draft = await svc.CreateDraftAsync(
            orgId, conn.Id, Bundle(Clean), cloneFromActive: false, "user", CancellationToken.None);

        draft.Should().NotBeNull();
        draft!.DeliveryConfigJson.Should().Contain("X-Correlation-Id");
    }

    [Fact]
    public async Task CreateDraft_WithNoHeadersAtAll_Succeeds()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);

        var draft = await svc.CreateDraftAsync(
            orgId, conn.Id, Bundle("""{"url":"https://supplier.example/orders"}"""),
            cloneFromActive: false, "user", CancellationToken.None);

        draft.Should().NotBeNull();
    }

    /// <summary>
    /// A revision that predates enforcement stays publishable. Publish flips a status on a stored
    /// bundle rather than writing an endpoint, and refusing it would block every future revision —
    /// including a mapping-only fix — for a supplier whose config predates the rule. Same call #157
    /// made for cleartext endpoints, and the reason the warning and the dispatch log exist.
    /// </summary>
    [Fact]
    public async Task Publish_OfAPreExistingRevisionCarryingACredentialHeader_StillWorks()
    {
        await using var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, _, conn) = await SeedAsync(db, svc);
        var rev = await SeedLegacyRevisionAsync(db, conn, "draft", WithToken);

        var published = await svc.PublishAsync(orgId, conn.Id, rev.Id, "user", CancellationToken.None);

        published.Should().NotBeNull();
    }
}
```

> If `PublishAsync`'s signature differs, read it from `SupplierConnectionService` and match — the point of the test is that publish is not refused, not the exact argument list. `ConnectionRevisionTransportSecurityTests` has an equivalent publish test to copy the call shape from.

Create `ProcuLink.Api.Tests/Controllers/ConnectionRevisionCredentialHeaderControllerTests.cs`, copying the `Harness`, `Build()`, `Bundle()`, `SeedLegacyDraft()` and `Assert400()` helpers from `ConnectionRevisionTransportSecurityControllerTests.cs:37-117`:

```csharp
    [Fact]
    public async Task CreateDraft_WithACredentialHeader_Returns400WithTheCode()
    {
        var h = Build();

        var result = await h.Controller.CreateDraft(
            h.Connection.Id,
            new CreateConnectionRevisionRequest(CloneFromActive: false, Bundle(WithToken)),
            CancellationToken.None);

        var (error, message) = Assert400(result);
        error.Should().Be("credential_header_in_delivery_config");
        message.Should().Contain("'Authorization'");
        h.Db.SupplierConnectionRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateDraft_WithACredentialHeader_Returns400_AndTheBodyNeverCarriesTheToken()
    {
        var h = Build();
        var revisionId = SeedLegacyDraft(h, Clean);

        var result = await h.Controller.UpdateDraft(
            h.Connection.Id, revisionId,
            new UpdateConnectionRevisionRequest(Bundle(WithToken)),
            CancellationToken.None);

        var (error, message) = Assert400(result);
        error.Should().Be("credential_header_in_delivery_config");
        message.Should().NotContain(Token);
    }

    [Fact]
    public async Task CreateDraft_WithOrdinaryHeaders_Succeeds()
    {
        var h = Build();

        var result = await h.Controller.CreateDraft(
            h.Connection.Id,
            new CreateConnectionRevisionRequest(CloneFromActive: false, Bundle(Clean)),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~CredentialHeader"
```

Expected: the refusal tests FAIL (no exception, revision saves with the token); the allowance and publish tests PASS as controls.

- [ ] **Step 3: Write minimal implementation**

In `ProcuLink.Api/Services/SupplierConnectionService.cs`, in `ApplyScalars`, add the second line:

```csharp
        ValidateTransportSecurity(input.DeliveryProtocol, input.DeliveryConfigJson);
        ValidateCredentialHeaders(input.DeliveryConfigJson);
        ValidateNoClientSuppliedCredentials(input.CredentialsRef);
```

And beside `ValidateTransportSecurity`:

```csharp
    /// <summary>
    /// Refuses a credential written into a caller-supplied revision's extra-headers map. Every entry
    /// of that map is applied to the outbound request by <c>HttpDeliveryDispatcher</c>, and
    /// <c>config_json</c> is a cleartext column, so a token typed there is stored in the clear and
    /// copied into the snapshot a pinned order delivers through.
    ///
    /// <para>Deliberately the SAME primitive the live delivery-config save path runs
    /// (<c>DeliveryConfigService.ValidateCredentialHeaders</c>): both reach
    /// <see cref="DeliveryConfigTransport.FindCredentialHeaders"/>. A second hand-rolled name check
    /// here would be a second security rule free to drift from the first.</para>
    ///
    /// <para><b>Flat, with no grandfathering</b> — unlike the live path, which grandfathers an
    /// identical stored pair because the delivery editor has no headers field to remove one with.
    /// This is caller-supplied input, and the clone-from-active, rollback, republish-from-live and
    /// publish paths never reach <c>ApplyScalars</c>, so nothing already live is stranded.
    /// Republish-from-live is what the delivery-config editor triggers, so the ordinary operator
    /// flow keeps working.</para>
    /// </summary>
    private static void ValidateCredentialHeaders(string? configJson)
    {
        var offending = DeliveryConfigTransport.FindCredentialHeaders(configJson);
        if (offending.Count > 0)
            throw new CredentialHeaderInConfigException(offending);
    }
```

In `ProcuLink.Api/Controllers/ConnectionsController.cs`, add to **both** `CreateDraft` and `UpdateDraft`, after the existing `catch (ClientSuppliedCredentialsRefException ex)`:

```csharp
        catch (CredentialHeaderInConfigException ex)
        {
            return RejectedCredentialHeader(ex);
        }
```

And beside `RejectedCredentialsRef`:

```csharp
    /// <summary>
    /// A credential written into <c>config_json</c>'s extra-headers map is the caller's mistake, so
    /// it is a 400 rather than the 500 an unhandled exception would produce. Same body shape as
    /// <see cref="InsecureEndpoint"/>; the message names the header and never its value.
    /// </summary>
    private BadRequestObjectResult RejectedCredentialHeader(CredentialHeaderInConfigException ex) =>
        BadRequest(new { error = CredentialHeaderInConfigException.Code, message = ex.PolicyMessage });
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~CredentialHeader"
```

Expected: PASS.

- [ ] **Step 5: Mutation-check the guard**

Delete the `ValidateCredentialHeaders(input.DeliveryConfigJson);` line from `ApplyScalars`. Re-run the command above. Expected: **RED**; record the count.

Restore it **by typing it back** — never `git checkout`. Verify with `git diff HEAD --stat`.

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Api/Services/SupplierConnectionService.cs ProcuLink.Api/Controllers/ConnectionsController.cs ProcuLink.Api.Tests/Services/ConnectionRevisionCredentialHeaderTests.cs ProcuLink.Api.Tests/Controllers/ConnectionRevisionCredentialHeaderControllerTests.cs
git commit -m "security: refuse a credential header on a connection revision"
```

---

### Task 5: The read surfaces

**Files:**
- Modify: `ProcuLink.Core/Services/Delivery/DeliveryConfigTransport.cs` (add `DescribeConfigWarnings`)
- Modify: `ProcuLink.Infrastructure/Services/DeliveryConfigService.cs:166-167` (`ToResponse`)
- Modify: `ProcuLink.Api/Controllers/ConnectionsController.cs:384` (`ToRevisionDto`)
- Modify: `ProcuLink.Core/Services/Delivery/DeliveryConfigContracts.cs:77-82` (widen the doc comment)
- Modify: `ProcuLink.Api/Contracts/ConnectionDto.cs:33-40` (widen the doc comment)
- Test: `ProcuLink.Infrastructure.Tests/Services/DeliveryConfigCredentialHeaderTests.cs` (extend)
- Test: `ProcuLink.Api.Tests/Controllers/ConnectionRevisionCredentialHeaderControllerTests.cs` (extend)

**Interfaces:**
- Consumes: `DescribeCredentialHeaders` from Task 2, `DescribeInsecureTransport` (pre-existing).
- Produces: `public static string? DeliveryConfigTransport.DescribeConfigWarnings(string? protocol, string? configJson)`

- [ ] **Step 1: Write the failing test**

Append to `ProcuLink.Infrastructure.Tests/Services/DeliveryConfigCredentialHeaderTests.cs`:

```csharp
    // ── The read surface ─────────────────────────────────────────────────────

    /// <summary>
    /// How an operator whose config predates enforcement finds out. The frontend already renders
    /// this field, so reusing it rather than adding a sibling is what puts the instruction in front
    /// of them at all.
    /// </summary>
    [Fact]
    public async Task GetAsync_ALegacyCredentialHeader_IsReportedWithoutTheToken()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedLegacyConfigAsync(db, orgId, supplierId, WithToken);

        var fetched = await service.GetAsync(orgId, supplierId, default);

        fetched!.InsecureTransportWarning.Should().NotBeNullOrWhiteSpace();
        fetched.InsecureTransportWarning.Should().Contain("'Authorization'");
        fetched.InsecureTransportWarning.Should().NotContain(Token);
    }

    [Fact]
    public async Task GetAsync_ACleanConfig_HasNoWarning()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SaveAsync(service, orgId, supplierId, Clean);

        (await service.GetAsync(orgId, supplierId, default))!
            .InsecureTransportWarning.Should().BeNull();
    }

    /// <summary>
    /// A config that is BOTH cleartext and credential-bearing reports both faults, because fixing
    /// one does not fix the other and an operator told only about the URL would leave the token in
    /// place.
    /// </summary>
    [Fact]
    public async Task GetAsync_BothFaults_AreBothReported()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedLegacyConfigAsync(db, orgId, supplierId,
            $$"""{"url":"http://supplier.example/orders","headers":{"Authorization":"Bearer {{Token}}"}}""");

        var warning = (await service.GetAsync(orgId, supplierId, default))!.InsecureTransportWarning;

        warning.Should().Contain("https://", "the transport fault must still be reported");
        warning.Should().Contain("'Authorization'", "the credential-header fault must be reported too");
        warning.Should().NotContain(Token);
    }
```

Append to `ProcuLink.Api.Tests/Controllers/ConnectionRevisionCredentialHeaderControllerTests.cs`:

```csharp
    /// <summary>
    /// A pre-existing revision keeps delivering, so the revision editor has to be able to say why.
    /// Mirrors DeliveryConfigResponse.InsecureTransportWarning deliberately, so both editors report
    /// the same blob the same way.
    /// </summary>
    [Fact]
    public async Task GetRevision_ALegacyCredentialHeader_IsReportedWithoutTheToken()
    {
        var h = Build();
        var revisionId = SeedLegacyDraft(h, WithToken);

        var result = await h.Controller.GetRevision(h.Connection.Id, revisionId, CancellationToken.None);

        var dto = (ConnectionRevisionDto)((OkObjectResult)result).Value!;
        dto.InsecureTransportWarning.Should().NotBeNullOrWhiteSpace();
        dto.InsecureTransportWarning.Should().Contain("'Authorization'");
        dto.InsecureTransportWarning.Should().NotContain(Token);
    }

    [Fact]
    public async Task GetRevision_ACleanRevision_HasNoWarning()
    {
        var h = Build();
        var revisionId = SeedLegacyDraft(h, Clean);

        var result = await h.Controller.GetRevision(h.Connection.Id, revisionId, CancellationToken.None);

        ((ConnectionRevisionDto)((OkObjectResult)result).Value!)
            .InsecureTransportWarning.Should().BeNull();
    }
```

> `ConnectionRevisionTransportSecurityControllerTests.cs:160-240` has equivalent read-path tests — copy the exact way it invokes the revision read action and unwraps the DTO, since the action name and result shape must match the real controller.

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~DeliveryConfigCredentialHeaderTests"
dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~ConnectionRevisionCredentialHeaderControllerTests"
```

Expected: the new read-path tests FAIL — `InsecureTransportWarning` is null for a credential-header-only config, because only the transport fault is reported today.

- [ ] **Step 3: Write minimal implementation**

In `DeliveryConfigTransport.cs`, add:

```csharp
    /// <summary>
    /// Everything wrong with a stored delivery config that an operator needs to know and cannot see:
    /// a transport the policy now refuses, a credential sitting in the extra-headers map, or both.
    ///
    /// <para>One composer, so the delivery-config editor and the revision editor cannot report the
    /// same blob differently. Both faults travel because fixing one does not fix the other — an
    /// operator told only about the URL would leave the token in place. Never quotes a URL or a
    /// header value: those are precisely the strings that would copy the secret onto the screen.</para>
    /// </summary>
    public static string? DescribeConfigWarnings(string? protocol, string? configJson)
    {
        var transport = DescribeInsecureTransport(protocol, configJson);
        var headers = DescribeCredentialHeaders(configJson);

        if (transport is null) return headers;
        if (headers is null) return transport;

        return $"{transport} {headers}";
    }
```

In `DeliveryConfigService.ToResponse`, replace the `InsecureTransportWarning:` argument:

```csharp
            InsecureTransportWarning: DeliveryConfigTransport.DescribeConfigWarnings(
                config.Protocol, config.ConfigJson));
```

In `ConnectionsController.ToRevisionDto`, replace the last argument:

```csharp
        // A revision written before enforcement reached this path keeps delivering, so the editor
        // has to be able to show BOTH faults it can now carry: an endpoint the transport policy
        // refuses, and a credential sitting in the extra-headers map. Same composer as the
        // delivery-config editor, so the two cannot report the same blob differently.
        DeliveryConfigTransport.DescribeConfigWarnings(r.DeliveryProtocol, r.DeliveryConfigJson));
```

In `DeliveryConfigContracts.cs`, replace the comment above `string? InsecureTransportWarning = null);`:

```csharp
    // Set when the SAVED config carries a fault the write path now refuses: an endpoint the
    // transport policy rejects (written before TLS enforcement existed), a credential sitting in the
    // extra-headers map, or both. Delivery continues — refusing mid-flight would turn a security
    // weakness into an outage — so this is how the operator finds out. Null when fine. Never
    // contains the URL or a header value: those are precisely the strings that would copy the secret
    // into the editor.
```

In `ConnectionDto.cs`, replace the comment above `string? InsecureTransportWarning = null);` with the same text, keeping its final sentence: `Mirrors DeliveryConfigResponse.InsecureTransportWarning so both editors report the same blob the same way.`

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~DeliveryConfig"
dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~ConnectionRevision"
```

Expected: PASS — including the pre-existing `DeliveryConfigTransportSecurityTests` and `ConnectionRevisionTransportSecurityControllerTests`, which must still pass unchanged. If a transport-only warning assertion now fails, the composer is appending when it should return the transport message alone.

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Core/Services/Delivery/DeliveryConfigTransport.cs ProcuLink.Core/Services/Delivery/DeliveryConfigContracts.cs ProcuLink.Infrastructure/Services/DeliveryConfigService.cs ProcuLink.Api/Controllers/ConnectionsController.cs ProcuLink.Api/Contracts/ConnectionDto.cs ProcuLink.Infrastructure.Tests/Services/DeliveryConfigCredentialHeaderTests.cs ProcuLink.Api.Tests/Controllers/ConnectionRevisionCredentialHeaderControllerTests.cs
git commit -m "security: report a stored credential header on both delivery read paths"
```

---

### Task 6: The dispatch-time log

**Files:**
- Modify: `ProcuLink.Infrastructure/Services/Dispatchers/HttpDeliveryDispatcher.cs:95` and a new method beside `WarnIfInsecureTransport` at `:211`
- Test: `ProcuLink.Infrastructure.Tests/Services/Dispatchers/HttpDeliveryDispatcherTests.cs` (extend; also extend the `file sealed class TestableHttpDeliveryDispatcher` at `:314` to accept a logger)

**Interfaces:**
- Consumes: `FindCredentialHeaders` from Task 2.
- Produces: nothing.

- [ ] **Step 1: Write the failing test**

In `HttpDeliveryDispatcherTests.cs`, change `TestableHttpDeliveryDispatcher` to take an optional logger:

```csharp
file sealed class TestableHttpDeliveryDispatcher : HttpDeliveryDispatcher
{
    private readonly HttpClient _sendClient;

    public TestableHttpDeliveryDispatcher(
        IHttpClientFactory factory, OutboundRequestGuard guard, HttpClient sendClient,
        ILogger<HttpDeliveryDispatcher>? logger = null)
        : base(factory, guard, logger ?? NullLogger<HttpDeliveryDispatcher>.Instance)
    {
        _sendClient = sendClient;
    }

    internal override HttpClient CreateSendClient() => _sendClient;
}
```

Add a capturing logger and the test (add `using Microsoft.Extensions.Logging;` to the file):

```csharp
    /// <summary>
    /// A config that predates enforcement keeps delivering, so the only thing that can surface it
    /// for a supplier nobody opens in the editor is a log line on every attempt.
    ///
    /// <para>It must name the HEADER and never its value. Logging the value would copy the
    /// credential out of one cleartext store into another, which is the defect this guard exists to
    /// stop.</para>
    /// </summary>
    [Fact]
    public async Task Dispatch_WithACredentialHeader_LogsTheHeaderNameAndNeverItsValue()
    {
        const string token = "t0ps3cret";
        var logger = new CapturingLogger();
        var client = new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, "OK"));
        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("delivery"))
               .Returns(() => new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, "OK")));
        var dispatcher = new TestableHttpDeliveryDispatcher(
            factory.Object, MakePermissiveGuard(), client, logger);

        var config = MakeConfig("https://example.com/orders");
        config.ConfigJson = JsonSerializer.Serialize(new
        {
            url = "https://example.com/orders",
            method = "POST",
            headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        });

        var result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config,
            JsonSerializer.Serialize(new { type = "none" }), default);

        result.Success.Should().BeTrue("delivery must continue — refusing mid-flight would strand orders");

        var warnings = string.Join("\n", logger.Warnings);
        warnings.Should().Contain("Authorization");
        warnings.Should().NotContain(token);
    }

    [Fact]
    public async Task Dispatch_WithOrdinaryHeaders_LogsNoCredentialWarning()
    {
        var logger = new CapturingLogger();
        var client = new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, "OK"));
        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("delivery"))
               .Returns(() => new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, "OK")));
        var dispatcher = new TestableHttpDeliveryDispatcher(
            factory.Object, MakePermissiveGuard(), client, logger);

        var config = MakeConfig("https://example.com/orders");
        config.ConfigJson = JsonSerializer.Serialize(new
        {
            url = "https://example.com/orders",
            method = "POST",
            headers = new Dictionary<string, string> { ["X-Correlation-Id"] = "abc" },
        });

        await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config,
            JsonSerializer.Serialize(new { type = "none" }), default);

        string.Join("\n", logger.Warnings).Should().NotContain("credential-bearing");
    }
```

And at the bottom of the file, beside `TestableHttpDeliveryDispatcher`:

```csharp
file sealed class CapturingLogger : ILogger<HttpDeliveryDispatcher>
{
    public List<string> Warnings { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~HttpDeliveryDispatcherTests"
```

Expected: `Dispatch_WithACredentialHeader_LogsTheHeaderNameAndNeverItsValue` FAILS — no warning is logged. The ordinary-headers control PASSES.

- [ ] **Step 3: Write minimal implementation**

In `HttpDeliveryDispatcher.cs`, after `WarnIfInsecureTransport(config, endpoint);` at `:95`:

```csharp
            WarnIfInsecureTransport(config, endpoint);
            WarnIfCredentialHeaders(config);
```

And beside `WarnIfInsecureTransport`:

```csharp
    /// <summary>
    /// Logs — once per delivery attempt — that this supplier's saved config carries a credential in
    /// its extra-headers map, which <c>config_json</c> stores in cleartext.
    ///
    /// <para>Only the header NAME is logged. Logging the value would copy the credential out of one
    /// cleartext store and into another, which is the defect this exists to surface. Delivery
    /// continues: the rule lands on the write paths, and refusing here would strand orders for every
    /// customer whose config predates it.</para>
    /// </summary>
    private void WarnIfCredentialHeaders(SupplierDeliveryConfig config)
    {
        var names = DeliveryConfigTransport.FindCredentialHeaders(config.ConfigJson);
        if (names.Count == 0) return;

        _logger.LogWarning(
            "Supplier {SupplierId} has credential-bearing delivery header(s) {HeaderNames} stored in "
            + "cleartext config_json. Delivery continues so orders are not lost; move the value into "
            + "the encrypted delivery credentials. The value itself is never logged.",
            config.SupplierId, string.Join(", ", names));
    }
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~HttpDeliveryDispatcherTests"
```

Expected: PASS, and every pre-existing dispatcher test still green.

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Infrastructure/Services/Dispatchers/HttpDeliveryDispatcher.cs ProcuLink.Infrastructure.Tests/Services/Dispatchers/HttpDeliveryDispatcherTests.cs
git commit -m "security: log a credential-bearing delivery header on every attempt, by name only"
```

---

### Task 7: Make the entity's invariant comment true, and verify the whole suite

**Files:**
- Modify: `ProcuLink.Core/Entities/SupplierDeliveryConfig.cs` (the `ConfigJson` doc comment, lines 40-51)

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

- [ ] **Step 1: Update the invariant comment**

Replace the `ConfigJson` doc comment with:

```csharp
    /// <summary>
    /// Non-secret JSONB: endpoint URL, host, remote path, extra headers, timeout, etc.
    ///
    /// SCALE-GATED / SECURITY NOTE: this column is stored in CLEARTEXT (no encryption).
    /// That is BY DESIGN and NOT a P2 secret-at-rest issue: every SECRET (passwords, API
    /// keys, bearer tokens, basic-auth, OAuth2 client secrets, SFTP/FTP credentials) is
    /// kept out of here and stored AES-GCM encrypted in <see cref="EncryptedCredentials"/>.
    /// ConfigJson holds only non-secret connection metadata. INVARIANT to preserve: never
    /// write a credential/secret into ConfigJson — if a new delivery option needs a secret,
    /// add it to the encrypted credential payload instead. See
    /// docs/audit/2026-06-12-scale-gated-constraints.md.
    ///
    /// <para><b>Enforced, not merely documented, for the extra-headers map.</b> Every entry of
    /// <c>headers</c> is applied to the outbound request by <c>HttpDeliveryDispatcher</c>, so an
    /// operator typing <c>Authorization: Bearer …</c> there was writing a live credential into this
    /// cleartext column. Both write paths — the live delivery-config upsert and the connection
    /// revision draft input — now refuse one, through the single classifier
    /// <c>DeliveryConfigTransport.FindCredentialHeaders</c>.</para>
    ///
    /// <para>Two deliberate limits. Enforcement is on WRITE only: a config saved before it existed
    /// keeps delivering, because refusing at dispatch would strand orders. And the live path
    /// grandfathers a header whose name and value are already stored, because the delivery editor
    /// has no headers field and round-trips the stored map on every save — refusing that echo would
    /// lock an operator out of every unrelated edit with no way to remove the header. Adding one, or
    /// rotating its value, is refused. Both cases are surfaced by
    /// <c>DeliveryConfigResponse.InsecureTransportWarning</c> and by a dispatch-time log that names
    /// the header and never its value.</para>
    /// </summary>
```

- [ ] **Step 2: Run the full backend suite**

```bash
dotnet test ProcuLink.Infrastructure.Tests
```

```bash
dotnet test ProcuLink.Api.Tests
```

```bash
dotnet test ProcuLink.Transform.Tests
```

Expected: zero failures in all three. Record the passing counts — they go in the PR body. If the Docker-gated Postgres integration tests skip on their own probe, say so rather than claiming they ran.

- [ ] **Step 3: Confirm no second copy of the rule exists**

```bash
git grep -n "Authorization\|IsCredentialHeaderName\|FindCredentialHeaders" -- 'ProcuLink.Api/**/*.cs' 'ProcuLink.Infrastructure/**/*.cs' 'ProcuLink.Core/**/*.cs'
```

Expected: every production hit is either a call to the shared primitive or the primitive itself. A hand-rolled header-name comparison anywhere else is the defect #157 exists to prevent — fold it into `DeliveryConfigTransport` instead.

- [ ] **Step 4: Commit**

```bash
git add ProcuLink.Core/Entities/SupplierDeliveryConfig.cs
git commit -m "docs: the ConfigJson cleartext invariant is now enforced for headers"
```

- [ ] **Step 5: Push and open the PR**

```bash
git push -u origin security/refuse-credential-headers-in-delivery-config
```

Open the PR **against `security/validate-revision-delivery-config`**, not `main` — #157 is the prerequisite and is still open. The PR body must state, at minimum: the defect and where it was found (#157's deferred list), the two design decisions and their reasons (grandfathering because the editor has no headers field; reusing `InsecureTransportWarning` for reach), the write-path table from spec §4, the per-guard mutation-check counts recorded in Tasks 3 and 5, the exact test counts from Step 2, and the companion frontend PR that spec §5.1 requires.

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| §3.1 one primitive, three consumers | 1, 2, 5 |
| §3.2 duplicate key / casing / not protocol-scoped | 2 |
| §3.3 classifier | 1 |
| §3.4 grandfathering | 2, 3 |
| §4 write paths + refusal shape | 3, 4 |
| §5 read + dispatch surfaces | 5, 6 |
| §5.1 companion frontend PR | out of plan by design; named in Task 7 Step 5 |
| §6 migration | 5 (warning), 6 (log), 7 (invariant doc) |
| §7 tests | every task |
| §8 out of scope | not implemented, correctly |

**Placeholder scan:** two steps say "copy the harness from `<named file>:<lines>`" (Task 3's controller test, Task 4's service test) rather than reproducing ~70 lines of harness verbatim. Those are exact file+line pointers to code that must be matched exactly, not descriptions of work to be invented. Task 4's `PublishAsync` note and Task 5's revision-read note flag the two signatures to read rather than guess. Everything else carries the actual code.

**Type consistency:** `FindCredentialHeaders(string?, string? = null) → IReadOnlyList<string>` is used identically in Tasks 2, 3, 4, 5 and 6. `CredentialHeaderInConfigException.Code` is the const in Tasks 2, 3 and 4, and the literal `"credential_header_in_delivery_config"` appears in test assertions only — deliberately, so a renamed const cannot silently change the wire contract. `BuildCredentialHeaderMessage` is `internal` and is called only from within `ProcuLink.Core`.
