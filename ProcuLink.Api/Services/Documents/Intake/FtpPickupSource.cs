using System.Text.Json;
using System.Text.Json.Nodes;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Api.Services.Documents.Intake;

/// <summary>FTP (<c>IntakeKind.Ftp</c>) or explicit-TLS FTPS (<c>IntakeKind.Ftps</c>) pickup; registered once per kind.</summary>
public sealed class FtpPickupSource(string kind, IFtpFetchClientFactory factory, OutboundRequestGuard guard) : IPickupSource
{
    public string Kind => kind;

    public async Task<IPickupSession> OpenAsync(FlowIntake intake, JsonElement credentials, CancellationToken ct)
    {
        var config = JsonNode.Parse(intake.ConfigJson)!.AsObject();
        var host = config["host"]!.GetValue<string>();
        var port = config["port"]!.GetValue<int>();
        var guarded = await guard.ValidateHostAsync(host, port, ct);
        if (!guarded.Allowed) throw new InvalidOperationException(guarded.Reason);

        var password = credentials.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
        var ftp = factory.Connect(host, port, config["username"]!.GetValue<string>(), password, explicitTls: kind == IntakeKind.Ftps);
        return new Session(ftp, config["directory"]!.GetValue<string>());
    }

    private sealed class Session(IFtpFetchSession ftp, string directory) : IPickupSession
    {
        public async Task<IReadOnlyList<PickupFile>> ListAsync(CancellationToken ct) =>
            (await ftp.ListFilesAsync(directory, ct)).Select(PickupFile.At).ToList();

        public Task<MemoryStream> DownloadAsync(PickupFile file, long maxBytes, CancellationToken ct) =>
            ftp.DownloadAsync(file.SourceRef!, maxBytes, ct);

        public Task MarkDoneAsync(PickupFile file, CancellationToken ct) => Task.CompletedTask;

        public void Dispose() => ftp.Dispose();
    }
}
