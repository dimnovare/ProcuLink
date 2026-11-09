using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Documents;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProcuLink.Transform.Tests.Documents;

public class ProposalDraftTests
{
    private static readonly FlowFieldList Fields = new(
        [new FlowField("po_number", "PO number", "text", null, false)],
        [new FlowTable("lines", "Lines", [new FlowField("qty", "Qty", "number", null, false)])]);

    private static ProposedNode Leaf(string name, string? key, string quote = "", string kind = "field", bool required = false) =>
        new(name, kind, key, required, null, quote, []);

    private static ProposedOutput Proposal(string format, ProposedDestination? destination, params ProposedNode[] leaves) =>
        new("Out", format, "", null, new ProposedNode("root", "object", null, false, null, "", leaves), destination);

    [Fact]
    public void Citations_are_located_by_the_backend_not_the_model()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var pdf = Document.Create(c =>
        {
            c.Page(p => { p.Size(PageSizes.A4); p.Content().Text("Intro to the import file"); });
            c.Page(p => { p.Size(PageSizes.A4); p.Content().Text("OrderNumber is required"); });
        }).GeneratePdf();
        var docs = DocumentationText.Read("spec.pdf", pdf)!;

        var (draft, _, _) = ProposalDraft.From(
            Proposal("json", null, Leaf("OrderNumber", "po_number", "OrderNumber is  REQUIRED"), Leaf("Other", null, "not in the text at all")), Fields, docs);

        draft.Leaves.Single(l => l.Path == "OrderNumber").Cite!.Where.Should().Be("page 2");
        draft.Leaves.Single(l => l.Path == "Other").Cite.Should().BeNull();
        draft.Leaves.Single(l => l.Path == "OrderNumber").By.Should().Be("docs");

        var html = DocumentationText.Read("spec.html", System.Text.Encoding.UTF8.GetBytes("<p>intro text here</p><p>OrderNumber is required</p>"))!;
        html.Locate("OrderNumber is required").Should().Be("line 2");
    }

    [Fact]
    public void Proposal_answers_outside_what_is_allowed_are_dropped()
    {
        var docs = new DocumentationText("anything", false);
        var csv = Proposal("csv", new ProposedDestination("https://x.test/in", "POST", "magic", ""),
            Leaf("PO", "po_number"), Leaf("Bad", "nope"), Leaf("Id", null, kind: "attribute"), Leaf("Qty", "lines.qty", required: true));

        var (draft, _, destination) = ProposalDraft.From(csv, Fields, docs);

        draft.Notes.Should().Contain("left_out");
        draft.Leaves.Single(l => l.Path == "Bad").FieldKey.Should().BeNull();
        draft.Leaves.Select(l => l.Path).Should().NotContain("Id");
        // Qty is a root leaf that Apply moves into the table; it keeps its Required flag.
        draft.Leaves.Single(l => l.Path == "lines/row/Qty").Required.Should().BeTrue();
        destination!.AuthType.Should().Be("none");

        var (_, _, js) = ProposalDraft.From(Proposal("json", new ProposedDestination("javascript:alert(1)", "POST", "none", ""), Leaf("PO", "po_number")), Fields, docs);
        js.Should().BeNull();

        var xml = new ProposedOutput("Out", "xml", "", null, new ProposedNode("root", "object", null, false, null, "", [Leaf("my field", "po_number"), Leaf("ok", "po_number")]), null);
        var (xmlDraft, _, _) = ProposalDraft.From(xml, Fields, docs);
        xmlDraft.Leaves.Select(l => l.Path).Should().Equal("ok");
        xmlDraft.Notes.Should().Contain("left_out");

        var quoted = ProposalDraft.From(new ProposedOutput("Out", "csv", "", "\"", new ProposedNode("root", "object", null, false, null, "", [Leaf("PO", "po_number")]), null), Fields, docs);
        quoted.Draft.Template.CsvDialect.Should().BeNull();
    }
}
