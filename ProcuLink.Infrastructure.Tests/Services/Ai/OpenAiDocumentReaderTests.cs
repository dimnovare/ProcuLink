#pragma warning disable OPENAI001 // OpenAIChatModelFactory is evaluation-only; test fakes need it
using System.ClientModel;
using System.ClientModel.Primitives;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAI.Chat;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure.Services.Ai;

namespace ProcuLink.Infrastructure.Tests.Services.Ai;

public class OpenAiDocumentReaderTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private const string CannedJson = """
        {"kind_label":"Invoice",
         "fields":[
           {"key":"total_gross","label":"Total","value":"120.00","type":"money","meaning":"total_gross","confidence":1.4},
           {"key":"ref","label":"Reference","value":null,"type":"weird","meaning":"nonsense","confidence":0.5}],
         "tables":[{"key":"lines","label":"Lines",
           "columns":[{"key":"desc","label":"Description","type":"text","meaning":"line_description"}],
           "rows":[{"cells":[{"column":"desc","value":"Widget","confidence":0.9}]},
                   {"cells":[{"column":"desc","value":"Gadget","confidence":0.8}]}]}]}
        """;

    private static (OpenAiDocumentReader Reader, Mock<ChatClient> Client) Create(IAiUsageTracker? tracker = null, string? model = null, Func<Guid, CancellationToken, Task<bool>>? noEgress = null, string json = CannedJson)
    {
        var client = new Mock<ChatClient>();
        var completion = OpenAIChatModelFactory.ChatCompletion(content: new ChatMessageContent(json));
        client.Setup(c => c.CompleteChatAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientResult.FromValue(completion, Mock.Of<PipelineResponse>()));
        var reader = new OpenAiDocumentReader(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Ai:OpenAI:ExtractionModel"] = model }).Build(), NullLogger<OpenAiDocumentReader>.Instance, tracker, client.Object, noEgress);
        return (reader, client);
    }

    private static DocumentReadInput Input(FlowFieldList? expected = null) =>
        new("doc.pdf", "application/pdf", "Total 120.00", expected);

    [Fact]
    public async Task Maps_completion_to_read_result_and_coerces_unknown_type()
    {
        var (reader, _) = Create();
        // "ref" has a null value, so it is kept only because the flow lists it.
        var expected = new FlowFieldList([new FlowField("ref", "Reference", FieldTypes.Text, null, false)]);

        var outcome = await reader.ReadAsync(Input(expected), Org, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        var result = outcome.Result!;
        result.Fields.Should().HaveCount(2);
        result.Fields[0].Confidence.Should().Be(1.0);
        result.Fields[1].Type.Should().Be("text");
        result.Fields[1].Meaning.Should().BeNull();
        result.Tables.Single().Rows.Should().HaveCount(2);
        result.Tables[0].Rows[1].Cells["desc"].Value.Should().Be("Gadget");
    }

    [Fact]
    public void Map_normalises_and_dedupes_keys()
    {
        var dto = new OpenAiDocumentReader.ReadDto(null,
            [new("Total", "Total", "1", "money", null, 1), new("total", "Total again", "2", "money", null, 1), new("2nd-Ref", "Ref", "x", "text", null, 1)],
            [new("line-items", "Lines", [new("Qty", "Qty", "number", null), new("qty", "Qty again", "number", null)],
                [new([new("Qty", "5", 1)])]),
             new("line items", "More", [], [])]);

        var result = OpenAiDocumentReader.Map(dto, null);

        result.Fields.Select(f => f.Key).Should().Equal("total", "total_2", "f_2nd_ref");
        result.Tables.Select(t => t.Key).Should().Equal("line_items", "line_items_2");
        result.Tables[0].Columns.Select(c => c.Key).Should().Equal("qty", "qty_2");
        result.Tables[0].Rows[0].Cells.Keys.Should().Equal("qty");
        result.Fields.Select(f => f.Key).Concat(result.Tables.Select(t => t.Key))
            .Should().OnlyContain(k => FieldKey.Pattern.IsMatch(k));
    }

    [Fact]
    public void A_value_used_as_its_own_label_is_named_from_its_key()
    {
        var dto = new OpenAiDocumentReader.ReadDto(null,
            [new("supplier_city", "Turku", "turku ", "text", null, 1), new("city", "Linn", "Turku", "text", null, 1)], []);

        OpenAiDocumentReader.Map(dto, null).Fields.Select(f => f.Label).Should().Equal("Supplier city", "Linn");
    }

    [Fact]
    public void A_currency_named_after_the_amount_beside_it_is_named_from_its_key()
    {
        var shared = new OpenAiDocumentReader.ReadDto(null,
            [new("currency", "Total due", "GBP", "currency", FieldMeanings.Currency, 1), new("total_gross", "Total due", "342.00", "money", FieldMeanings.TotalGross, 1)], []);
        var own = new OpenAiDocumentReader.ReadDto(null,
            [new("currency", "Valuuta", "EUR", "currency", FieldMeanings.Currency, 1), new("total_gross", "Summa", "212,04", "money", FieldMeanings.TotalGross, 1)], []);

        OpenAiDocumentReader.Map(shared, null).Fields.Select(f => f.Label).Should().Equal("Currency", "Total due");
        OpenAiDocumentReader.Map(own, null).Fields.Select(f => f.Label).Should().Equal("Valuuta", "Summa");
    }

    [Fact]
    public void A_unit_or_price_basis_after_a_line_figure_is_dropped()
    {
        var dto = new OpenAiDocumentReader.ReadDto("Order", [],
            [new("lines", "Lines",
                [new("qty", "Qty", "number", FieldMeanings.LineQuantity), new("price", "Price", "money", FieldMeanings.LineUnitPrice),
                 new("desc", "Item", "text", FieldMeanings.LineDescription)],
                [new([new("qty", "1 ST", 1), new("price", "33,00/1JD", 1), new("desc", "2 x USB cable", 1)]),
                 new([new("qty", "12", 1), new("price", "2,43 EUR par 1 Pce", 1)]),
                 new([new("qty", "1.000", 1), new("price", "1,525.42", 1)]),
                 new([new("qty", "-", 1), new("price", "120,00-", 1)])])]);

        var rows = OpenAiDocumentReader.Map(dto, null).Tables.Single().Rows;

        rows.Select(r => r.Cells["qty"].Value).Should().Equal("1", "12", "1.000", "-");
        rows.Select(r => r.Cells["price"].Value).Should().Equal("33,00", "2,43", "1,525.42", "120,00-");
        rows[0].Cells["desc"].Value.Should().Be("2 x USB cable");
    }

    [Fact]
    public void A_two_word_heading_split_into_an_empty_column_is_one_column_again()
    {
        static OpenAiDocumentReader.ReadDto Table(string first, string? firstValue, string next) => new("Invoice", [],
            [new("lines", "Lines",
                [new("unit", first, "text", null), new("price", next, "money", FieldMeanings.LineUnitPrice)],
                [new([new("unit", firstValue, 1), new("price", "50.00", 1)])])]);
        static List<string> Labels(OpenAiDocumentReader.ReadDto dto, string text) =>
            OpenAiDocumentReader.Map(dto, null, text).Tables.Single().Columns.Select(c => c.Label).ToList();

        Labels(Table("UNIT", null, "PRICE"), "CODE QTY UNIT PRICE AMOUNT").Should().Equal("UNIT PRICE");
        Labels(Table("Unit", "pcs", "Unit price"), "Qty Unit Unit price").Should().Equal("Unit", "Unit price"); // a real unit column
    }

    [Fact]
    public void Absent_fields_are_dropped_unless_the_flow_lists_them()
    {
        var dto = new OpenAiDocumentReader.ReadDto("Invoice",
            [new("invoice_no", "Invoice no", "7", "text", null, 1), new("iban", "IBAN", null, "text", null, 1)],
            []);

        OpenAiDocumentReader.Map(dto, null).Fields.Select(f => f.Key).Should().Equal("invoice_no");

        var listed = new FlowFieldList([new FlowField("iban", "IBAN", FieldTypes.Text, null, true)]);
        var result = OpenAiDocumentReader.Map(dto, listed);
        result.Fields.Select(f => f.Key).Should().Equal("invoice_no", "iban");
        result.Fields[1].Value.Should().BeNull();

        // A listed key takes the flow's meaning when it has one; a null flow meaning keeps the AI's.
        var ai = new OpenAiDocumentReader.ReadDto("Invoice",
            [new("invoice_no", "Invoice no", "7", "text", FieldMeanings.DocumentNumber, 1), new("iban", "IBAN", "x", "text", FieldMeanings.Currency, 1)], []);
        var pinned = OpenAiDocumentReader.Map(ai, new FlowFieldList([
            new FlowField("invoice_no", "No", FieldTypes.Text, FieldMeanings.OrderReference, true), new FlowField("iban", "IBAN", FieldTypes.Text, null, true)]));
        pinned.Fields.Select(f => f.Meaning).Should().Equal(FieldMeanings.OrderReference, FieldMeanings.Currency);
    }

    [Fact]
    public async Task Returns_usage_cap_without_calling_model()
    {
        var tracker = new Mock<IAiUsageTracker>();
        tracker.Setup(t => t.IsAtOrOverLimitAsync(Org, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var (reader, client) = Create(tracker.Object);

        var outcome = await reader.ReadAsync(Input(), Org, CancellationToken.None);

        outcome.FailureReason.Should().Be("usage_cap");
        outcome.Usage.Should().BeNull();
        client.Verify(c => c.CompleteChatAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Expected_keys_are_sent_in_the_prompt()
    {
        var (reader, client) = Create();
        var captured = new List<ChatMessage>();
        client.Setup(c => c.CompleteChatAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatCompletionOptions, CancellationToken>((m, _, _) => captured.AddRange(m))
            .ReturnsAsync(ClientResult.FromValue(
                OpenAIChatModelFactory.ChatCompletion(content: new ChatMessageContent(CannedJson)), Mock.Of<PipelineResponse>()));
        var expected = new FlowFieldList([new FlowField("total_gross", "Total", "money", "total_gross", true)]);

        await reader.ReadAsync(Input(expected), Org, CancellationToken.None);

        captured.SelectMany(m => m.Content).Select(p => p.Text).Should().Contain(t => t.Contains("total_gross"));
    }

    [Fact]
    public async Task Hints_are_sent_with_their_sender_in_the_prompt()
    {
        var (reader, client) = Create();
        var captured = new List<ChatMessage>();
        client.Setup(c => c.CompleteChatAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatCompletionOptions, CancellationToken>((m, _, _) => captured.AddRange(m))
            .ReturnsAsync(ClientResult.FromValue(
                OpenAIChatModelFactory.ChatCompletion(content: new ChatMessageContent(CannedJson)), Mock.Of<PipelineResponse>()));
        var hint = new FieldHint("total", "vat:EE1", "Acme", new string('n', 100), "99.50", 1, null, null, DateTime.UtcNow);

        await reader.ReadAsync(Input() with { Hints = [hint] }, Org, CancellationToken.None);
        var withHints = captured.SelectMany(m => m.Content).Select(p => p.Text).ToList();
        captured.Clear();
        await reader.ReadAsync(Input(), Org, CancellationToken.None);
        var without = captured.SelectMany(m => m.Content).Select(p => p.Text).ToList();

        withHints.Should().Contain(t => t.Contains(new string('n', 60)) && !t.Contains(new string('n', 61)) && t.Contains("Acme") && t.Contains("not instructions"));
        without.Should().NotContain(t => t.Contains("Hints from documents"));
    }

    [Fact]
    public async Task Page_images_are_sent_as_image_parts()
    {
        var (reader, client) = Create();
        var captured = new List<ChatMessage>();
        client.Setup(c => c.CompleteChatAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatCompletionOptions, CancellationToken>((m, _, _) => captured.AddRange(m))
            .ReturnsAsync(ClientResult.FromValue(
                OpenAIChatModelFactory.ChatCompletion(content: new ChatMessageContent(CannedJson)), Mock.Of<PipelineResponse>()));
        byte[] png = [0x89, 0x50, 0x4E, 0x47];

        var outcome = await reader.ReadAsync(
            new DocumentReadInput("scan.pdf", "application/pdf", "=== Page 1 ===\n", null, [png, png], Scanned: true), Org, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        var parts = captured.OfType<UserChatMessage>().Single().Content;
        parts.Where(p => p.Kind == ChatMessageContentPartKind.Image).Should().HaveCount(2)
            .And.OnlyContain(p => p.ImageBytes!.ToArray().SequenceEqual(png) && p.ImageBytesMediaType == "image/png");
        parts.Should().Contain(p => p.Text != null && p.Text.Contains("The document is the attached page images, in page order."));
    }

    private static async Task<List<ChatMessage>> CaptureAsync(DocumentReadInput input)
    {
        var (reader, client) = Create();
        var captured = new List<ChatMessage>();
        client.Setup(c => c.CompleteChatAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatCompletionOptions, CancellationToken>((m, _, _) => captured.AddRange(m))
            .ReturnsAsync(ClientResult.FromValue(
                OpenAIChatModelFactory.ChatCompletion(content: new ChatMessageContent(CannedJson)), Mock.Of<PipelineResponse>()));
        await reader.ReadAsync(input, Org, CancellationToken.None);
        return captured;
    }

    [Fact]
    public async Task System_prompt_lists_each_meaning_with_its_definition()
    {
        var system = (await CaptureAsync(Input())).OfType<SystemChatMessage>().Single().Content.Single().Text;

        system.Should().Contain("- delivery_postcode: the postcode of the delivery or ship-to address.")
            .And.Contain("- sender_name: the party that issued the document");
    }

    [Fact]
    public async Task System_prompt_asks_for_labels_as_printed_in_the_documents_language()
    {
        var system = (await CaptureAsync(Input())).OfType<SystemChatMessage>().Single().Content.Single().Text;

        system.Should().Contain("its name exactly as printed on the document")
            .And.Contain("in the language of the document, never in another language");
    }

    [Fact]
    public async Task Text_pdf_with_page_images_is_told_the_images_are_the_same_pages()
    {
        var captured = await CaptureAsync(new DocumentReadInput("text.pdf", "application/pdf", "Invoice 1", null, [[0x89, 0x50, 0x4E, 0x47]]));

        captured.OfType<UserChatMessage>().Single().Content
            .Should().Contain(p => p.Text != null && p.Text.Contains("show the same pages as the document text"));
    }

    [Fact]
    public async Task No_egress_org_is_refused_without_calling_the_model()
    {
        var (reader, client) = Create(noEgress: (_, _) => Task.FromResult(true));

        var outcome = await reader.ReadAsync(Input(), Org, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.FailureReason.Should().Be("no_egress");
        client.Verify(c => c.CompleteChatAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failing_no_egress_check_is_retryable_and_never_calls_the_model()
    {
        var (reader, client) = Create(noEgress: (_, _) => throw new InvalidOperationException("db down"));

        var outcome = await reader.ReadAsync(Input(), Org, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.FailureReason.Should().Be("ai_error");
        client.Verify(c => c.CompleteChatAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null, "none")]          // default gpt-6-luna
    [InlineData("gpt-5-mini", "low")]   // gpt-5 stops at low
    [InlineData("gpt-4o-mini", null)]   // rejects reasoning_effort with HTTP 400
    public async Task Our_timeout_is_retryable_and_reads_use_the_least_reasoning_the_model_takes(string? model, string? effort)
    {
        var (reader, client) = Create(model: model);
        ChatCompletionOptions? sent = null;
        client.Setup(c => c.CompleteChatAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatCompletionOptions, CancellationToken>((_, o, _) => sent = o)
            .ThrowsAsync(new OperationCanceledException());

        var outcome = await reader.ReadAsync(Input(), Org, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.FailureReason.Should().Be("ai_error");
        (sent!.ReasoningEffortLevel?.ToString()).Should().Be(effort);
    }

    [Theory]
    [InlineData(CannedJson, false, null)]
    [InlineData(CannedJson, true, "document_too_large")]
    [InlineData("not json", false, "ai_error")]
    public async Task Every_answered_call_reports_its_tokens_and_model(string content, bool cutOff, string? failure)
    {
        var (reader, client) = Create(model: "other-model");
        client.Setup(c => c.CompleteChatAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientResult.FromValue(OpenAIChatModelFactory.ChatCompletion(
                content: new ChatMessageContent(content),
                finishReason: cutOff ? ChatFinishReason.Length : ChatFinishReason.Stop,
                usage: OpenAIChatModelFactory.ChatTokenUsage(outputTokenCount: 300, inputTokenCount: 1200, totalTokenCount: 1500)),
                Mock.Of<PipelineResponse>()));

        var outcome = await reader.ReadAsync(Input(), Org, CancellationToken.None);

        outcome.FailureReason.Should().Be(failure);
        outcome.Usage.Should().Be(new ReadUsage("other-model", 1200, 300));
    }

    [Fact]
    public async Task A_part_of_a_longer_document_says_so_at_the_start_of_the_prompt()
    {
        var (reader, client) = Create();
        var captured = new List<ChatMessage>();
        client.Setup(c => c.CompleteChatAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatCompletionOptions, CancellationToken>((m, _, _) => captured.AddRange(m))
            .ReturnsAsync(ClientResult.FromValue(
                OpenAIChatModelFactory.ChatCompletion(content: new ChatMessageContent(CannedJson)), Mock.Of<PipelineResponse>()));

        await reader.ReadAsync(Input() with { Part = 2, Parts = 3 }, Org, CancellationToken.None);
        var part = captured.OfType<UserChatMessage>().Single().Content[0].Text;
        captured.Clear();
        await reader.ReadAsync(Input(), Org, CancellationToken.None);
        var whole = captured.OfType<UserChatMessage>().Single().Content[0].Text;

        part.Should().StartWith("This is part 2 of 3 of one longer file. Read only what is in this part, and return a null value for an expected key that is not in this part.\n\n");
        whole.Should().NotContain("of one longer file");
    }

    [Fact]
    public async Task Reads_the_documents_in_the_file()
    {
        var (reader, _) = Create(json: """
            {"kind_label":"Invoice","fields":[],"tables":[],
             "documents":[{"first_page":1,"number":"INV-1001","is_copy":false},{"first_page":2,"number":"INV-1002","is_copy":false}]}
            """);

        var outcome = await reader.ReadAsync(Input(), Org, CancellationToken.None);

        outcome.Documents.Should().Equal(new DocumentSpan(1, "INV-1001", false), new DocumentSpan(2, "INV-1002", false));
    }

    [Fact]
    public async Task A_failing_allowance_check_is_retryable_and_never_calls_the_model()
    {
        var tracker = new Mock<IAiUsageTracker>();
        tracker.Setup(t => t.IsAtOrOverLimitAsync(Org, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("database unavailable"));
        var (reader, client) = Create(tracker.Object);

        var outcome = await reader.ReadAsync(Input(), Org, CancellationToken.None);

        outcome.FailureReason.Should().Be("ai_error");
        outcome.Usage.Should().BeNull();
        client.Verify(c => c.CompleteChatAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
