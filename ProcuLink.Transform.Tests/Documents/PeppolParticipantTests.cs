using FluentAssertions;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Transform.Tests.Documents;

public class PeppolParticipantTests
{
    [Theory]
    [InlineData("0088:123abc", "Y7DZFXAF3D4CJZ4KCGRXTEC6TWVCGA4KY7ZWA5BOIF6MSWD4TDRQ")]
    [InlineData("0010:5798000000001", "XUKHFQABQZIKI3YKVR2FHR4SNFA3PF5VPQ6K4TONV3LMVSY5ARVQ")]
    [InlineData("0088:123ABC", "Y7DZFXAF3D4CJZ4KCGRXTEC6TWVCGA4KY7ZWA5BOIF6MSWD4TDRQ")] // lower-cased before hashing
    public void SmlHost_matches_the_policy_vectors(string id, string hash) =>
        PeppolParticipant.Parse(id)!.SmlHost("participant.sml.prod.tech.peppol.org")
            .Should().Be($"{hash}.iso6523-actorid-upis.participant.sml.prod.tech.peppol.org");

    [Theory]
    [InlineData(" 0208 : 0000000097 ", "0208:0000000097")]
    [InlineData("iso6523-actorid-upis::9915:test", "9915:test")]
    [InlineData("0208:0000 000 097", "0208:0000000097")]
    [InlineData("208:0000000097", null)] // three-digit scheme
    [InlineData("0208", null)]           // no value
    [InlineData("0208:", null)]
    [InlineData("0208:000\t097", null)]  // whitespace other than spaces
    public void Parse_normalises_or_refuses(string text, string? expected)
    {
        if (expected is null) PeppolParticipant.Parse(text).Should().BeNull();
        else PeppolParticipant.Parse(text)!.ToString().Should().Be(expected);

        PeppolParticipant.Parse("0088:" + new string('1', 51)).Should().BeNull();
        PeppolParticipant.Parse("0088:" + new string('1', 50)).Should().NotBeNull();
        PeppolParticipant.FromBelgianVat("BE 0000.000.097")!.ToString().Should().Be("0208:0000000097");
        PeppolParticipant.FromBelgianVat("BE000000097")!.ToString().Should().Be("0208:0000000097");
        PeppolParticipant.FromBelgianVat("FR00000000000").Should().BeNull();
        PeppolParticipant.FromBelgianVat("BE0123.456.749")!.ToString().Should().Be("0208:0123456749"); // synthetic, mod-97 valid
        PeppolParticipant.FromBelgianVat("BE0123456748").Should().BeNull();  // check digits wrong: a misread is never looked up
        PeppolParticipant.FromBelgianVat("BE0000000098").Should().BeNull();
        PeppolParticipant.FromBelgianVat(null).Should().BeNull();
    }
}
