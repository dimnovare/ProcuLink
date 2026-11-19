using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Documents;

public class StandardOutputsPartnerIdsTests
{
    private static async Task<string> Render(string standard, EnvelopeConfig envelope)
    {
        var output = new FlowOutput { Standard = standard, TableKey = "lines", TemplateJson = DocumentJson.Serialize(new OutputNodeTemplate { Envelope = envelope }) };
        var render = await StandardOutputs.RenderAsync(output, StrictOrderFixture.Read(), FlowFieldList.Empty, Guid.Empty, CancellationToken.None);
        render.Ok.Should().BeTrue(render.Detail);
        return await new StreamReader(render.Result!.Content).ReadToEndAsync();
    }

    [Fact]
    public async Task X12_writes_the_outputs_sender_and_receiver_ids()
    {
        var text = await Render("x12-850", new() { X12 = new() { IsaSenderQualifier = "01", IsaSenderId = "TESTBUYER", IsaReceiverId = "TESTSUPPLIER" } });
        var segs = text.Split('~');
        var isa = segs[0].Split('*');
        (isa[5], isa[6], isa[7], isa[8]).Should().Be(("01", "TESTBUYER      ", "ZZ", "TESTSUPPLIER   "));
        segs[1].Should().StartWith("GS*PO*TESTBUYER*TESTSUPPLIER*");
    }

    [Fact]
    public async Task EDIFACT_writes_the_ids_with_a_qualifier_only_when_given()
    {
        var text = await Render("edifact-orders", new() { Edifact = new() { SenderId = "TESTBUYER", SenderQualifier = "ZZZ", RecipientId = "TESTSUPPLIER" } });
        text.Should().Contain("UNB+UNOC:3+TESTBUYER:ZZZ+TESTSUPPLIER+");
    }

    [Fact]
    public async Task CXml_configured_ids_win_and_the_sender_follows_the_from()
    {
        var text = await Render("cxml-1-2", new() { Cxml = new() { FromIdentity = "TESTBUYER_EE", ToDomain = "DUNS", ToIdentity = "TESTSUPPLIER_EE" } });
        var header = XDocument.Parse(text).Root!.Element("Header")!;
        (string, string) Read(string name)
        {
            var c = header.Element(name)!.Element("Credential")!;
            return ((string)c.Attribute("domain")!, c.Element("Identity")!.Value);
        }
        Read("From").Should().Be(("NetworkId", "TESTBUYER_EE"));
        Read("To").Should().Be(("DUNS", "TESTSUPPLIER_EE"));
        Read("Sender").Should().Be(("NetworkId", "TESTBUYER_EE"));
        text.Should().Contain(CxmlTransformService.StandardDtdSystemId);
    }
}
