using FluentAssertions;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public sealed class SenderIdentityTests
{
    private static ReadResult Read(params (string Meaning, string Value)[] f) =>
        new(null, f.Select(x => new ReadField(x.Meaning, x.Meaning, x.Value, FieldTypes.Text, x.Meaning, 0.9, null, false, false, false)).ToList());

    [Fact]
    public void Vat_wins_and_is_normalised()
    {
        var id = SenderIdentity.Of(Read((FieldMeanings.SenderVatId, " ee 1009-1234 5 "), (FieldMeanings.SenderName, "Acme GmbH")), "email", "x@y.com");
        id.Should().Be(new SenderIdentity("vat:EE100912345", "Acme GmbH"));
    }

    [Fact]
    public void Name_then_email_domain()
    {
        SenderIdentity.Of(Read((FieldMeanings.SenderName, "ACME GmbH")), "upload", null)!.Key
            .Should().Be(SenderIdentity.Of(Read((FieldMeanings.SenderName, "Acme")), "upload", null)!.Key);
        SenderIdentity.Of(Read(), "imap", "Orders@Mail.Example.COM")
            .Should().Be(new SenderIdentity("email:mail.example.com", "mail.example.com"));
    }

    [Fact]
    public void Upload_with_no_sender_fields_is_unknown()
    {
        SenderIdentity.Of(Read(), "upload", null).Should().BeNull();
        SenderIdentity.Of(Read(), "sftp", "a@b/in.pdf").Should().BeNull();
    }
}
