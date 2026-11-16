using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Services.Email;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public class InboxApiTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid OrgA = DocumentEngineTestFactory.OrgA;
    private static readonly Guid OrgB = DocumentEngineTestFactory.OrgB;

    private static byte[] Pdf(int pages, string marker)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(c =>
        {
            for (var i = 1; i <= pages; i++)
                c.Page(p => p.Content().Text($"Synthetic document {marker} page {i}"));
        }).GeneratePdf();
    }

    private async Task<InboxDto> InboxOf(Guid org)
    {
        var res = await factory.ClientFor(org).GetAsync("/api/inbox");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<InboxDto>(DocumentJson.Options))!;
    }

    /// <summary>The address's token, in either composed form ("token@domain" or "orders@token.host").</summary>
    private static string TokenOf(string address)
    {
        var (local, host) = (address.Split('@')[0], address.Split('@')[1]);
        return (local == "orders" ? host.Split('.')[0] : local).ToLowerInvariant();
    }

    private async Task<InboundAddressLookup> ResolveAsync(string address)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IInboundAddressService>().ResolveAsync(TokenOf(address), CancellationToken.None);
    }

    [Fact]
    public async Task Upload_without_a_flow_lands_in_the_inbox()
    {
        var http = factory.ClientFor(OrgA);
        var before = factory.Enqueued<ReadDocumentJob>();

        var res = await http.PostAsync("/api/documents",
            new MultipartFormDataContent { { new ByteArrayContent(Pdf(1, "inbox")), "file", "inbox.pdf" } });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var dto = (await res.Content.ReadFromJsonAsync<DocumentSummaryDto>(DocumentJson.Options))!;
        Assert.Equal("New documents", dto.FlowName);
        Assert.Equal((await InboxOf(OrgA)).FlowId, dto.FlowId);
        Assert.Equal(before + 1, factory.Enqueued<ReadDocumentJob>());
        var flows = (await http.GetFromJsonAsync<List<FlowDto>>("/api/flows", DocumentJson.Options))!;
        Assert.DoesNotContain(flows, f => f.Id == dto.FlowId);
    }

    [Fact]
    public async Task Each_workspace_has_its_own_address()
    {
        var (a, b) = (await InboxOf(OrgA), await InboxOf(OrgB));

        Assert.NotEqual(a.FlowId, b.FlowId);
        Assert.NotNull(a.Address);
        Assert.NotNull(b.Address);
        Assert.NotEqual(a.Address, b.Address);
        var resolved = await ResolveAsync(b.Address!);
        Assert.Equal(InboundAddressLookupStatus.Resolved, resolved.Status);
        Assert.Equal(b.FlowId, resolved.FlowId);
        Assert.Equal(OrgB, resolved.OrgId);
        Assert.Equal(a.Address, (await InboxOf(OrgA)).Address);

        // The inbox reads the same file kinds the organisation's flows report.
        var http = factory.ClientFor(OrgA);
        (await http.PostAsJsonAsync("/api/flows", new CreateFlowRequest("Supplier invoices"))).EnsureSuccessStatusCode();
        var flow = (await http.GetFromJsonAsync<List<FlowDto>>("/api/flows", DocumentJson.Options))!.First();
        Assert.Equal(flow.ScansOn, a.ScansOn);
        Assert.Equal(flow.FormatsOn, a.FormatsOn);
    }

    [Fact]
    public async Task New_address_replaces_the_old_one()
    {
        var old = await InboxOf(OrgA);

        var res = await factory.ClientFor(OrgA).PostAsync("/api/inbox/new-address", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var rotated = (await res.Content.ReadFromJsonAsync<InboxDto>(DocumentJson.Options))!;
        Assert.NotNull(rotated.Address);
        Assert.NotEqual(old.Address, rotated.Address);
        Assert.Equal(old.FlowId, rotated.FlowId);
        Assert.Equal(InboundAddressLookupStatus.NotFound, (await ResolveAsync(old.Address!)).Status);
    }
}
