using FluentAssertions;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public sealed class SenderIdentityTests
{
    private static ReadResult Read(params (string Meaning, string Value)[] f) =>
        new(null, f.Select(x => new ReadField(x.Meaning, x.Meaning, x.Value, FieldTypes.Text, x.Meaning, 0.9, null, false, false, false)).ToList());

    [Fact]
    public void Matches_only_the_documents_own_sender()
    {
        SenderIdentity.Matches("", "x", "upload", null).Should().BeTrue();
        SenderIdentity.Matches("vat:EE100912345", "VAT: ee 1009-12345", "upload", null).Should().BeTrue();
        SenderIdentity.Matches("vat:EE100912345", "VAT: EE999", "upload", null).Should().BeFalse();
        SenderIdentity.Matches("name:acme", "Invoice from ACME, Ltd", "upload", null).Should().BeTrue();
        SenderIdentity.Matches("name:acme", "Invoice from Acmes", "upload", null).Should().BeFalse();
        SenderIdentity.Matches("email:shop.example", "", "email", "a@Shop.Example").Should().BeTrue();
        SenderIdentity.Matches("email:shop.example", "", "upload", "a@shop.example").Should().BeFalse();
    }

    [Fact]
    public void Vat_wins_and_is_normalised()
    {
        var id = SenderIdentity.Of(Read((FieldMeanings.SenderVatId, " ee 1009-1234 5 "), (FieldMeanings.SenderName, "Acme GmbH")), "email", "x@y.example");
        id.Should().Be(new SenderIdentity("vat:EE100912345", "Acme GmbH"));
    }

    [Fact]
    public void Name_then_email_domain()
    {
        SenderIdentity.Of(Read((FieldMeanings.SenderName, "ACME GmbH")), "upload", null)!.Key
            .Should().Be(SenderIdentity.Of(Read((FieldMeanings.SenderName, "Acme")), "upload", null)!.Key);
        SenderIdentity.Of(Read(), "imap", "Orders@Mail.Shop.EXAMPLE")
            .Should().Be(new SenderIdentity("email:mail.shop.example", "mail.shop.example"));
    }

    [Fact]
    public void Upload_with_no_sender_fields_is_unknown()
    {
        SenderIdentity.Of(Read(), "upload", null).Should().BeNull();
        SenderIdentity.Of(Read(), "sftp", "a@b/in.pdf").Should().BeNull();
    }
}
