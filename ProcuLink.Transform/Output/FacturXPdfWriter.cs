using ProcuLink.Core.Entities;
using QuestPDF.Fluent;

namespace ProcuLink.Transform.Output;

/// <summary>The Factur-X / ZUGFeRD 2.x profile a PDF declares in its XMP metadata.</summary>
public enum FacturXProfile { En16931, XRechnung }

/// <summary>
/// A ZUGFeRD 2.x / Factur-X invoice: our own PDF/A-3B page (<see cref="DocumentPdfOutput.WriteInvoice"/>) carrying exactly the given CII
/// file as its one attachment (relationship Alternative), with the Factur-X XMP metadata. QuestPDF's DocumentOperation works on paths only,
/// so every call gets its own temp directory, deleted even on failure: two sends at once never share a file.
/// </summary>
public static class FacturXPdfWriter
{
    public static (string ConformanceLevel, string FileName) Describe(FacturXProfile profile) =>
        profile == FacturXProfile.XRechnung ? ("XRECHNUNG", "xrechnung.xml") : ("EN 16931", "factur-x.xml");

    public static byte[] Write(InvoiceEntity invoice, IReadOnlyList<InvoiceLineEntity> lines, PeppolPartyOptions parties, bool creditNote, FacturXProfile profile, byte[] ciiXml)
    {
        var (level, fileName) = Describe(profile);
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"facturx-{Guid.NewGuid():N}")).FullName;
        try
        {
            string pdfPath = Path.Combine(dir, "page.pdf"), xmlPath = Path.Combine(dir, fileName), outPath = Path.Combine(dir, "out.pdf");
            File.WriteAllBytes(pdfPath, DocumentPdfOutput.WriteInvoice(invoice, lines, parties, creditNote));
            File.WriteAllBytes(xmlPath, ciiXml);
            var now = DateTime.UtcNow;
            try
            {
                DocumentOperation.LoadFile(pdfPath)
                    .AddAttachment(new DocumentOperation.DocumentAttachment
                    {
                        Key = fileName, FilePath = xmlPath, AttachmentName = fileName, MimeType = "text/xml",
                        Description = profile == FacturXProfile.XRechnung ? "XRechnung invoice" : "Factur-X invoice",
                        Relationship = DocumentOperation.DocumentAttachmentRelationship.Alternative, CreationDate = now, ModificationDate = now,
                    })
                    .ExtendMetadata(Xmp.Replace("{level}", level).Replace("{fileName}", fileName))
                    .Save(outPath);
            }
            // A qpdf failure surfaces as whatever QuestPDF throws: made the refusal every caller already handles (output_invalid).
            catch (Exception ex) when (ex is not (InvalidOperationException or OperationCanceledException))
            {
                throw new InvalidOperationException("The Factur-X PDF could not be assembled.", ex);
            }
            return File.ReadAllBytes(outPath);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort temp cleanup: a finished PDF is never thrown away for it */ }
        }
    }

    // The shape of QuestPDF's own conformance sample (validated by veraPDF and Mustang): the fx values, and the PDF/A extension schema declaring them.
    private const string Xmp = """
        <rdf:Description xmlns:fx="urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#" rdf:about="">
            <fx:ConformanceLevel>{level}</fx:ConformanceLevel>
            <fx:DocumentType>INVOICE</fx:DocumentType>
            <fx:DocumentFileName>{fileName}</fx:DocumentFileName>
            <fx:Version>1.0</fx:Version>
        </rdf:Description>
        <rdf:Description xmlns:pdfaExtension="http://www.aiim.org/pdfa/ns/extension/" xmlns:pdfaProperty="http://www.aiim.org/pdfa/ns/property#" xmlns:pdfaSchema="http://www.aiim.org/pdfa/ns/schema#" rdf:about="">
            <pdfaExtension:schemas>
                <rdf:Bag>
                    <rdf:li rdf:parseType="Resource">
                        <pdfaSchema:schema>Factur-X PDFA Extension Schema</pdfaSchema:schema>
                        <pdfaSchema:namespaceURI>urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#</pdfaSchema:namespaceURI>
                        <pdfaSchema:prefix>fx</pdfaSchema:prefix>
                        <pdfaSchema:property>
                            <rdf:Seq>
                                <rdf:li rdf:parseType="Resource">
                                    <pdfaProperty:name>DocumentFileName</pdfaProperty:name>
                                    <pdfaProperty:valueType>Text</pdfaProperty:valueType>
                                    <pdfaProperty:category>external</pdfaProperty:category>
                                    <pdfaProperty:description>The name of the embedded XML invoice file</pdfaProperty:description>
                                </rdf:li>
                                <rdf:li rdf:parseType="Resource">
                                    <pdfaProperty:name>DocumentType</pdfaProperty:name>
                                    <pdfaProperty:valueType>Text</pdfaProperty:valueType>
                                    <pdfaProperty:category>external</pdfaProperty:category>
                                    <pdfaProperty:description>INVOICE</pdfaProperty:description>
                                </rdf:li>
                                <rdf:li rdf:parseType="Resource">
                                    <pdfaProperty:name>Version</pdfaProperty:name>
                                    <pdfaProperty:valueType>Text</pdfaProperty:valueType>
                                    <pdfaProperty:category>external</pdfaProperty:category>
                                    <pdfaProperty:description>The version of the Factur-X XML schema</pdfaProperty:description>
                                </rdf:li>
                                <rdf:li rdf:parseType="Resource">
                                    <pdfaProperty:name>ConformanceLevel</pdfaProperty:name>
                                    <pdfaProperty:valueType>Text</pdfaProperty:valueType>
                                    <pdfaProperty:category>external</pdfaProperty:category>
                                    <pdfaProperty:description>The conformance level of the embedded XML invoice</pdfaProperty:description>
                                </rdf:li>
                            </rdf:Seq>
                        </pdfaSchema:property>
                    </rdf:li>
                </rdf:Bag>
            </pdfaExtension:schemas>
        </rdf:Description>
        """;
}
