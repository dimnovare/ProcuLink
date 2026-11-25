using FluentAssertions;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public class DocumentSenderByHandTests
{
    private static FlowDocument Doc(bool sample = false, bool unsure = false, DocumentCheck[]? checks = null) =>
        new() { IsSample = sample, SortUnconfirmed = unsure, ChecksJson = DocumentJson.Serialize((checks ?? []).ToList()) };

    private static DocumentCheck Check(string code, bool passed) => new(code, passed, null, "synthetic");

    [Fact]
    public void HeldForPerson_NamesEachHold()
    {
        DocumentSender.HeldForPerson(Doc(sample: true, unsure: true)).Should().Be("sample");
        DocumentSender.HeldForPerson(Doc(unsure: true, checks: [Check(DocumentChecks.Duplicate, false)])).Should().Be("check_sort");
        DocumentSender.HeldForPerson(Doc(checks: [Check(DocumentChecks.NewSender, false), Check(DocumentChecks.Duplicate, false)])).Should().Be("duplicate");
        DocumentSender.HeldForPerson(Doc(checks: [Check(DocumentChecks.NewSender, false)])).Should().Be("new_sender");
        DocumentSender.HeldForPerson(Doc(checks: [Check(DocumentChecks.Duplicate, true), Check("required", false)])).Should().BeNull();
    }

    [Fact]
    public void HeldForPerson_AgreesWithTheAutomaticSender()
    {
        var auto = FlowCreation.New(Guid.NewGuid(), "Synthetic flow", FlowFieldList.Empty, confirmed: true, FlowAutomation.Auto);
        foreach (var doc in new[] { Doc(), Doc(sample: true), Doc(unsure: true), Doc(checks: [Check(DocumentChecks.Duplicate, false)]),
                     Doc(checks: [Check(DocumentChecks.NewSender, false)]), Doc(checks: [Check("required", false)]) })
        {
            doc.Status = DocumentStatus.NeedsReview;
            ProcuLink.Api.Jobs.SendDocumentJob.ShouldSend(doc, auto).Should().Be(DocumentSender.HeldForPerson(doc) is null, doc.ChecksJson);
        }
    }
}
