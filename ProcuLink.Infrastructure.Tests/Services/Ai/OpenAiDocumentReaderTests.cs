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

    private static (OpenAiDocumentReader Reader, Mock<ChatClient> Client) Create(IAiUsageTracker? tracker = null, string? model = null, Func<Guid, CancellationToken, Task<bool>>? noEgress = null)
    {
        var client = new Mock<ChatClient>();
        var completion = OpenAIChatModelFactory.ChatCompletion(content: new ChatMessageContent(CannedJson));
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
    public void A_key_at_the_pattern_limit_survives_normalising_and_stays_unique()
    {
        var key = new string('a', 64);
        FieldKey.Normalise(key).Should().Be(key);
        var unique = FieldKey.Unique(new HashSet<string> { key }, key);
        unique.Should().MatchRegex(FieldKey.Pattern.ToString()).And.EndWith("_2");
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
    }

    [Fact]
    public async Task Returns_usage_cap_without_calling_model()
    {
        var tracker = new Mock<IAiUsageTracker>();
        tracker.Setup(t => t.IsAtOrOverLimitAsync(Org, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var (reader, client) = Create(tracker.Object);

        var outcome = await reader.ReadAsync(Input(), Org, CancellationToken.None);

        outcome.FailureReason.Should().Be("usage_cap");
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
            new DocumentReadInput("scan.pdf", "application/pdf", "", null, [png, png]), Org, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        var parts = captured.OfType<UserChatMessage>().Single().Content;
        parts.Where(p => p.Kind == ChatMessageContentPartKind.Image).Should().HaveCount(2)
            .And.OnlyContain(p => p.ImageBytes!.ToArray().SequenceEqual(png) && p.ImageBytesMediaType == "image/png");
        parts.Should().Contain(p => p.Text != null && p.Text.Contains("attached page images"));
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
}
