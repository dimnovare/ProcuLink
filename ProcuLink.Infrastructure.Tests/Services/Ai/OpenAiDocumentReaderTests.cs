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

    private static (OpenAiDocumentReader Reader, Mock<ChatClient> Client) Create(IAiUsageTracker? tracker = null, string? model = null)
    {
        var client = new Mock<ChatClient>();
        var completion = OpenAIChatModelFactory.ChatCompletion(content: new ChatMessageContent(CannedJson));
        client.Setup(c => c.CompleteChatAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientResult.FromValue(completion, Mock.Of<PipelineResponse>()));
        var reader = new OpenAiDocumentReader(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Ai:OpenAI:ExtractionModel"] = model }).Build(), NullLogger<OpenAiDocumentReader>.Instance, tracker, client.Object);
        return (reader, client);
    }

    private static DocumentReadInput Input(FlowFieldList? expected = null) =>
        new("doc.pdf", "application/pdf", "Total 120.00", expected);

    [Fact]
    public async Task Maps_completion_to_read_result_and_coerces_unknown_type()
    {
        var (reader, _) = Create();

        var outcome = await reader.ReadAsync(Input(), Org, CancellationToken.None);

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

    [Theory]
    [InlineData(null, true)]            // default gpt-5-mini
    [InlineData("gpt-6-luna", true)]
    [InlineData("gpt-4o-mini", false)]  // the Worker's production model rejects reasoning_effort (HTTP 400)
    public async Task Our_timeout_is_retryable_and_only_reasoning_models_get_low_reasoning(string? model, bool reasoning)
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
        (sent!.ReasoningEffortLevel == ChatReasoningEffortLevel.Low).Should().Be(reasoning);
        sent.ReasoningEffortLevel.HasValue.Should().Be(reasoning);
    }
}
