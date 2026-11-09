using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Documents;

namespace ProcuLink.Transform.Tests.Documents;

public class OutputSampleTests
{
    private static byte[] Workbook(params string[] headings)
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Sheet1");
            for (var i = 0; i < headings.Length; i++) ws.Cell(1, i + 1).Value = headings[i];
            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    [Fact]
    public void Xlsx_sample_becomes_a_flat_draft_bound_by_name()
    {
        var fields = new FlowFieldList(
            [new FlowField("po_number", "PO number", "text", null, false)],
            [new FlowTable("lines", "Lines", [new FlowField("qty", "Qty", "number", null, false)])]);

        var t = OutputSample.Infer("sample.xlsx", Workbook("PO number", "Qty"))!;
        t.Root.Name.Should().Be("document");

        var slots = DraftBinder.Slots(t, "xlsx", fields);
        var draft = DraftBinder.Apply("Out", "xlsx", t, DraftBinder.ByName(slots, fields).Select(m => (m, "name")).ToList(), fields);

        draft.TableKey.Should().Be("lines");
        draft.Leaves.Should().Contain(l => l.Path == "PO number" && l.FieldKey == "po_number" && l.By == "name");
        draft.Leaves.Should().Contain(l => l.Path == "lines/row/Qty" && l.FieldKey == "qty");
    }

    [Fact]
    public void Leaf_paths_follow_the_shared_vector()
    {
        OutputNode F(string n) => OutputNode.FieldOf(n, new OutputFieldRule { OutputPath = n });
        var t = new OutputNodeTemplate
        {
            Root = OutputNode.Obj("document", F("a"), OutputNode.Obj("b", F("c")), OutputNode.Arr("lines", OutputNode.Obj("row", F("q")))),
        };

        var leaves = OutputLeafPaths.Of(t);

        leaves.Select(l => l.Path).Should().Equal("a", "b/c", "lines/row/q");
        leaves.Where(l => l.InTable).Select(l => l.Path).Should().Equal("lines/row/q");
    }

    [Fact]
    public void Unreadable_or_odd_samples_never_throw()
    {
        var semi = OutputSample.Infer("a.csv", Encoding.UTF8.GetBytes("a;b\n"));
        semi.Should().NotBeNull();
        semi!.CsvDialect!.Delimiter.Should().Be(";");

        // A root array is represented (as an array root), so it infers rather than returning null.
        OutputSample.Infer("a.json", Encoding.UTF8.GetBytes("[{\"x\":1}]")).Should().NotBeNull();
        OutputSample.Infer("a.xml", Encoding.UTF8.GetBytes("<o id=\"1\"><l q=\"2\"/></o>")).Should().NotBeNull();
        OutputSample.Infer("a.json", Encoding.UTF8.GetBytes("{not json")).Should().BeNull();
        OutputSample.Infer("a.xlsx", Workbook()).Should().BeNull();
        // No leaves, or repeated leaf paths, can't make a draft; nor can a DTD.
        OutputSample.Infer("a.csv", Encoding.UTF8.GetBytes("a,a\n")).Should().BeNull();
        OutputSample.Infer("a.json", Encoding.UTF8.GetBytes("5")).Should().BeNull();
        OutputSample.Infer("a.xml", Encoding.UTF8.GetBytes("<!DOCTYPE o [<!ENTITY x \"y\">]><o>&x;</o>")).Should().BeNull();
    }
}
