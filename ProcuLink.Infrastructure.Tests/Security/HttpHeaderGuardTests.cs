using System.Collections.Generic;
using System.Net.Http;
using FluentAssertions;
using ProcuLink.Core.Security;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Security;

/// <summary>
/// Adversarial gate for <see cref="HttpHeaderGuard"/> — the shared validator that stops
/// tenant-supplied outbound HTTP header names/values from carrying CR/LF/NUL/control characters
/// (header-injection / response-splitting). Pins that injection payloads are REJECTED (never
/// added to the request) while legitimate headers pass through unchanged.
///
/// <para>Control characters (SOH, BEL, DEL, etc.) are built in code from their numeric code
/// points so a literal control byte never has to survive in a source/string literal.</para>
/// </summary>
public class HttpHeaderGuardTests
{
    private const char Soh = (char)0x01;  // start of heading — a generic C0 control char
    private const char Bel = (char)0x07;  // bell
    private const char Del = (char)0x7F;  // delete
    private const char Cr  = (char)0x0D;  // carriage return
    private const char Lf  = (char)0x0A;  // line feed
    private const char Nul = (char)0x00;  // null

    // ── IsValidName ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Api-Key")]
    [InlineData("X-ProcuLink-Event")]
    [InlineData("X-Custom-Header-123")]
    [InlineData("Content-Type")]
    public void IsValidName_ValidTokens_ReturnTrue(string name)
    {
        HttpHeaderGuard.IsValidName(name).Should().BeTrue();
    }

    public static IEnumerable<object?[]> InvalidNames() => new[]
    {
        new object?[] { null },
        new object?[] { "" },
        new object?[] { "X-Evil" + Cr + Lf + "Host: attacker" }, // embedded CRLF + second header
        new object?[] { "X-Evil" + Cr + "Host" },                // bare CR
        new object?[] { "X-Evil" + Lf + "Host" },                // bare LF
        new object?[] { "X-Evil" + Nul },                        // NUL
        new object?[] { "Bad Name" },                            // space is a separator
        new object?[] { "Bad:Name" },                            // colon is a separator
        new object?[] { "Bad,Name" },                            // comma is a separator
        new object?[] { "Bad;Name" },                            // semicolon is a separator
        new object?[] { "Bad/Name" },                            // slash is a separator
        new object?[] { "Bad@Name" },                            // @ is a separator
        new object?[] { "X-Ctrl" + Soh + "Name" },               // control char (SOH)
        new object?[] { "X-Tab\tName" },                         // HTAB not valid in a NAME
        new object?[] { "X-Unicode-naive" + (char)0x00E9 },      // non-ASCII (e-acute)
    };

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void IsValidName_InjectionOrIllegal_ReturnFalse(string? name)
    {
        HttpHeaderGuard.IsValidName(name).Should().BeFalse();
    }

    // ── IsValidValue ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]                            // null serializes to empty — allowed
    [InlineData("")]
    [InlineData("Bearer abc.def.ghi")]
    [InlineData("a value with spaces")]
    [InlineData("value\twith\ttabs")]             // HTAB is the one permitted control
    [InlineData("application/json; charset=utf-8")]
    public void IsValidValue_CleanValues_ReturnTrue(string? value)
    {
        HttpHeaderGuard.IsValidValue(value).Should().BeTrue();
    }

    public static IEnumerable<object?[]> InvalidValues() => new[]
    {
        new object?[] { "1" + Cr + Lf + "Host: attacker.example" }, // classic response-split
        new object?[] { "value" + Cr + Lf + "X-Injected: 1" },
        new object?[] { "value" + Cr + "carriage" },               // bare CR
        new object?[] { "value" + Lf + "linefeed" },               // bare LF
        new object?[] { "value" + Nul + "nul" },                   // NUL
        new object?[] { "value" + Bel + "bell" },                  // control char (BEL)
        new object?[] { "value" + Del + "del" },                   // DEL
    };

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void IsValidValue_InjectionOrControl_ReturnFalse(string value)
    {
        HttpHeaderGuard.IsValidValue(value).Should().BeFalse();
    }

    // ── TryAdd ───────────────────────────────────────────────────────────────

    [Fact]
    public void TryAdd_ValidHeader_AddsAndReturnsTrue()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com");

        var added = HttpHeaderGuard.TryAdd(request.Headers, "X-Api-Key", "secret-token-value");

        added.Should().BeTrue();
        request.Headers.TryGetValues("X-Api-Key", out var values).Should().BeTrue();
        values!.Should().ContainSingle().Which.Should().Be("secret-token-value");
    }

    [Fact]
    public void TryAdd_CrlfInValue_NotAddedAndReturnsFalse()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com");

        var added = HttpHeaderGuard.TryAdd(
            request.Headers, "X-Forwarded-For", "1.2.3.4" + Cr + Lf + "Host: attacker.example");

        added.Should().BeFalse();
        request.Headers.TryGetValues("X-Forwarded-For", out _).Should().BeFalse();
        request.Headers.TryGetValues("Host", out _).Should().BeFalse();
    }

    [Fact]
    public void TryAdd_CrlfInName_NotAddedAndReturnsFalse()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com");

        var added = HttpHeaderGuard.TryAdd(
            request.Headers, "X-Evil" + Cr + Lf + "Host", "attacker.example");

        added.Should().BeFalse();
        request.Headers.Should().BeEmpty();
    }

    [Fact]
    public void TryAdd_EmbeddedSecondHeader_DoesNotLeakAsSeparateHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com");

        // Attacker tries to terminate the value and inject Host + X-Forwarded-For.
        var added = HttpHeaderGuard.TryAdd(
            request.Headers,
            "X-Tenant-Header",
            "ok" + Cr + Lf + "Host: attacker.example" + Cr + Lf + "X-Forwarded-For: 6.6.6.6");

        added.Should().BeFalse();
        // None of the injected headers exist, and the original wasn't added either.
        request.Headers.TryGetValues("X-Tenant-Header", out _).Should().BeFalse();
        request.Headers.TryGetValues("Host", out _).Should().BeFalse();
        request.Headers.TryGetValues("X-Forwarded-For", out _).Should().BeFalse();
    }

    [Fact]
    public void TryAdd_NulInValue_NotAddedAndReturnsFalse()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com");

        var added = HttpHeaderGuard.TryAdd(request.Headers, "X-Token", "abc" + Nul + "def");

        added.Should().BeFalse();
        request.Headers.TryGetValues("X-Token", out _).Should().BeFalse();
    }

    [Fact]
    public void TryAdd_NullValue_AddsEmptyHeaderAndReturnsTrue()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com");

        // A null value is treated as valid (serializes to an empty header value), matching
        // TryAddWithoutValidation behaviour — the guard only blocks CR/LF/NUL/control chars.
        var added = HttpHeaderGuard.TryAdd(request.Headers, "X-Optional", null);

        added.Should().BeTrue();
        request.Headers.TryGetValues("X-Optional", out _).Should().BeTrue();
    }

    [Fact]
    public void TryAdd_NullHeaders_Throws()
    {
        var act = () => HttpHeaderGuard.TryAdd(null!, "X-Api-Key", "value");
        act.Should().Throw<ArgumentNullException>();
    }
}
