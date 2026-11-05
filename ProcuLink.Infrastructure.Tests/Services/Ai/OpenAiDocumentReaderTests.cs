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

    private static (OpenAiDocumentReader Reader, Mock<ChatClient> Client) Create(IAiUsageTracker? tracker = null)
    {
        var client = new Mock<ChatClient>();
        var completion = OpenAIChatModelFactory.ChatCompletion(content: new ChatMessageContent(CannedJson));
        client.Setup(c => c.CompleteChatAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatCompletionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientResult.FromValue(completion, Mock.Of<PipelineResponse>()));
        var reader = new OpenAiDocumentReader(
            new ConfigurationBuilder().Build(), NullLogger<OpenAiDocumentReader>.Instance, tracker, client.Object);
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
}
