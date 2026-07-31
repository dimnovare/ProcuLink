using System.Text;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// WP-15 · S7 — typed JSON leaves.
///
/// <para>Every leaf emitted as a string, always. A receiver whose schema says <c>quantity</c> is a
/// number rejected a document that was otherwise correct, and there was no way to author around it:
/// the tree could produce <c>"3"</c> and never <c>3</c>. Likewise <c>emptyValue</c> — a receiver that
/// distinguishes "absent" from "empty string" could not be served, because nothing about a VALUE can
/// express the absence of its property.</para>
///
/// <para>The whole slice rests on one property: <b>absent means byte-identical</b>. Both fields are
/// nullable, an unrecognised value means "as before", and the parity cases below are what stop a
/// well-meant default from re-typing every existing supplier's JSON.</para>
/// </summary>
public class JsonTypedLeafTests
{
    private static PurchaseOrderEntity Order(string? buyer = null, decimal qty = 3m)
    {
        var id = Guid.NewGuid();
        return new PurchaseOrderEntity
        {
            Id = id, OrgId = Guid.NewGuid(), SupplierId = Guid.NewGuid(),
            PoNumber = "PO-1", Currency = "EUR", OrderDate = new DateOnly(2026, 6, 15),
            BuyerName = buyer,
            Lines =
            {
                new PurchaseOrderLineEntity { Id = Guid.NewGuid(), OrderId = id, LineNumber = 1,
                    SupplierItemCode = "S-1", Quantity = qty, UnitPrice = 10m, NeedsReview = false },
            },
        };
    }

    /// <summary>A one-object tree with a single leaf, so each case is about that leaf and nothing else.</summary>
    private static OutputNodeTemplate Tree(string canonicalField, string? valueType = null, string? emptyValue = null) => new()
    {
        Format = OutputFormat.Json,
        Root = OutputNode.Obj("order", new OutputNode
        {
            Name = "value",
            NodeType = OutputNodeType.Field,
            ValueType = valueType,
            EmptyValue = emptyValue,
            Rule = new OutputFieldRule { OutputPath = "value", CanonicalField = canonicalField },
        }),
    };

    private static string Emit(OutputNodeTemplate t, PurchaseOrderEntity? order = null)
    {
        var r = new OutputTemplateEmitter().Emit(t, order ?? Order(), new OrderMappingOverride());
        r.Content.Position = 0;
        using var sr = new StreamReader(r.Content, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    // ── The parity guarantee ─────────────────────────────────────────────────

    /// <summary>
    /// No declaration → the string every leaf has always been. This case has to hold for the rest of
    /// the slice to be safe to ship: a default of "number" anywhere, or a coercion that looked
    /// helpful, would silently re-type every existing supplier's JSON.
    /// </summary>
    [Fact]
    public void NoValueType_EmitsAString_ExactlyAsBefore()
    {
        Assert.Contains("\"value\": \"PO-1\"", Emit(Tree("PoNumber")));
    }

    /// <summary>An unrecognised type is a string too — never a hard failure and never a guess.</summary>
    [Fact]
    public void UnknownValueType_FallsBackToAString()
    {
        Assert.Contains("\"value\": \"PO-1\"", Emit(Tree("PoNumber", valueType: "decimal128")));
    }

    // ── number ───────────────────────────────────────────────────────────────

    [Fact]
    public void NumberValueType_EmitsAnUnquotedNumber()
    {
        // Bound to a HEADER field: the leaf sits directly under the root object, so a line-scope
        // canonical name would resolve to empty here and the test would be about scope, not typing.
        var json = Emit(Tree("BuyerName", valueType: "number"), Order(buyer: "3"));
        Assert.Contains("\"value\": 3", json);
        Assert.DoesNotContain("\"value\": \"3\"", json);
    }

    [Fact]
    public void NumberValueType_KeepsDecimals()
    {
        Assert.Contains("\"value\": 2.5", Emit(Tree("BuyerName", valueType: "number"), Order(buyer: "2.5")));
    }

    [Fact]
    public void NumberValueType_EmitsANegative()
    {
        Assert.Contains("\"value\": -4", Emit(Tree("BuyerName", valueType: "number"), Order(buyer: "-4")));
    }

    /// <summary>
    /// An EMPTY numeric field is a different authoring mistake from a non-numeric one — a field that
    /// is simply absent on this order rather than the wrong shape — so it gets its own sentence and
    /// names the two real fixes. "'' is not a number" reads like a bug in us.
    /// </summary>
    [Fact]
    public void NumberValueType_OnAnEmptyValue_SaysWhatToDoAboutIt()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Emit(Tree("BuyerName", valueType: "number"), Order(buyer: null)));

        Assert.Contains("has no value", ex.Message);
        Assert.Contains("left out when empty", ex.Message);
    }

    /// <summary>
    /// …and `emptyValue: omit` is the escape hatch that message points at: an optional numeric field
    /// with nothing to say drops out instead of failing the order.
    /// </summary>
    [Fact]
    public void NumberValueType_WithOmit_DropsTheFieldInsteadOfFailingTheOrder()
    {
        var json = Emit(Tree("BuyerName", valueType: "number", emptyValue: "omit"), Order(buyer: null));
        Assert.DoesNotContain("\"value\"", json);
    }

    /// <summary>
    /// The decision this slice rests on. A non-numeric value THROWS rather than falling back to a
    /// string: the fallback produces well-formed JSON the receiver's schema rejects, and it does so
    /// after we have already reported the order delivered. Failing here, where the order is still on
    /// an operator's screen, is the kinder failure — and the message names the field and the value.
    /// </summary>
    [Fact]
    public void NumberValueType_OnANonNumber_Throws_RatherThanQuietlyEmittingAString()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Emit(Tree("BuyerName", valueType: "number"), Order(buyer: "10 pcs")));

        Assert.Contains("value", ex.Message);
        Assert.Contains("10 pcs", ex.Message);
    }

    /// <summary>
    /// Parsed invariantly. The value has already been through the manipulator chain, so "1.234,50"
    /// here means an author asked for a localised STRING and then declared it a number — a mistake
    /// worth reporting rather than silently reinterpreting as 1.234.
    /// </summary>
    [Fact]
    public void NumberValueType_DoesNotReinterpretALocalisedNumber()
    {
        Assert.Throws<InvalidOperationException>(
            () => Emit(Tree("BuyerName", valueType: "number"), Order(buyer: "1.234,50")));
    }

    // ── boolean ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("Y")]
    [InlineData("1")]
    public void BooleanValueType_AcceptsTheSpellingsAFeedReallyCarries_True(string raw)
    {
        Assert.Contains("\"value\": true", Emit(Tree("BuyerName", valueType: "boolean"), Order(buyer: raw)));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("N")]
    [InlineData("0")]
    public void BooleanValueType_AcceptsTheSpellingsAFeedReallyCarries_False(string raw)
    {
        Assert.Contains("\"value\": false", Emit(Tree("BuyerName", valueType: "boolean"), Order(buyer: raw)));
    }

    [Fact]
    public void BooleanValueType_OnSomethingElse_Throws_AndSaysWhatItAccepts()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Emit(Tree("BuyerName", valueType: "boolean"), Order(buyer: "maybe")));

        Assert.Contains("maybe", ex.Message);
        Assert.Contains("yes/no", ex.Message);
    }

    // ── null ─────────────────────────────────────────────────────────────────

    [Fact]
    public void NullValueType_EmitsJsonNull_NotTheStringNull()
    {
        var json = Emit(Tree("PoNumber", valueType: "null"));
        Assert.Contains("\"value\": null", json);
        Assert.DoesNotContain("\"null\"", json);
    }

    // ── emptyValue: omit ─────────────────────────────────────────────────────

    /// <summary>
    /// The PROPERTY disappears, not its value. "Absent" and "empty string" are different to a
    /// receiver, and nothing about a value can express the first.
    /// </summary>
    [Fact]
    public void OmitWhenEmpty_DropsThePropertyEntirely()
    {
        var json = Emit(Tree("BuyerName", emptyValue: "omit"), Order(buyer: null));

        Assert.DoesNotContain("\"value\"", json);
        Assert.Contains("{", json);
    }

    [Fact]
    public void OmitWhenEmpty_KeepsThePropertyWhenThereIsAValue()
    {
        Assert.Contains("\"value\": \"Acme\"", Emit(Tree("BuyerName", emptyValue: "omit"), Order(buyer: "Acme")));
    }

    /// <summary>
    /// Whitespace counts as empty. A value that is only spaces is not information, and a receiver
    /// told to distinguish absent from empty did not mean "absent, or two spaces".
    /// </summary>
    [Fact]
    public void OmitWhenEmpty_TreatsWhitespaceAsEmpty()
    {
        Assert.DoesNotContain("\"value\"", Emit(Tree("BuyerName", emptyValue: "omit"), Order(buyer: "   ")));
    }

    [Fact]
    public void WithoutOmit_AnEmptyValueStillWritesTheProperty()
    {
        Assert.Contains("\"value\": \"\"", Emit(Tree("BuyerName"), Order(buyer: null)));
    }

    /// <summary>An unrecognised emptyValue behaves as absent, like every other unknown here.</summary>
    [Fact]
    public void UnknownEmptyValue_WritesThePropertyAsBefore()
    {
        Assert.Contains("\"value\": \"\"", Emit(Tree("BuyerName", emptyValue: "blank"), Order(buyer: null)));
    }

    // ── format isolation ─────────────────────────────────────────────────────

    /// <summary>
    /// CSV and XML have no types on the wire — everything there is text — so a declared type must be
    /// IGNORED rather than half-honoured. A bare 3 in a CSV cell is indistinguishable from the string,
    /// and throwing on "10 pcs" in a CSV would break a document that was always fine.
    /// </summary>
    [Fact]
    public void CsvIgnoresValueType_IncludingAValueThatWouldThrowInJson()
    {
        var csv = new OutputNodeTemplate
        {
            Format = OutputFormat.Csv,
            Root = OutputNode.Obj("root", new OutputNode
            {
                Name = "value", NodeType = OutputNodeType.Field, ValueType = "number", EmptyValue = "omit",
                Rule = new OutputFieldRule { OutputPath = "value", CanonicalField = "BuyerName" },
            }),
        };

        Assert.Contains("10 pcs", Emit(csv, Order(buyer: "10 pcs")));
    }
}
