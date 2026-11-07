using System.Text.Json;
using System.Text.Json.Nodes;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Api.Services.Documents.Intake;

/// <summary>SFTP pickup; the server's host key is trusted on first use, then pinned in the intake's config.</summary>
public sealed class SftpPickupSource(ISftpClientFactory factory, OutboundRequestGuard guard) : IPickupSource
{
    public string Kind => IntakeKind.Sftp;

    public async Task<IPickupSession> OpenAsync(FlowIntake intake, JsonElement credentials, CancellationToken ct)
    {
        var config = JsonNode.Parse(intake.ConfigJson)!.AsObject();
        var host = config["host"]!.GetValue<string>();
        var port = config["port"]!.GetValue<int>();
        var guarded = await guard.ValidateHostAsync(host, port, ct);
        if (!guarded.Allowed) throw new InvalidOperationException(guarded.Reason);

        var pinned = (config["hostKeyFingerprints"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? [];
        var verifier = new SshHostKeyVerifier("SFTP pickup", pinned);
        var password = credentials.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
        var session = await Task.Run(
            () => factory.Connect(host, port, config["username"]!.GetValue<string>(), password, verifier), ct);

        if (verifier.LearnedFingerprint is { } learned)
        {
            config["hostKeyFingerprints"] = new JsonArray(pinned.Append(learned).Select(f => (JsonNode)f).ToArray());
            intake.ConfigJson = config.ToJsonString();
        }

        return new Session(session, config["directory"]!.GetValue<string>());
    }

    private sealed class Session(ISftpSession sftp, string directory) : IPickupSession
    {
        public Task<IReadOnlyList<PickupFile>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PickupFile>>(sftp.ListFiles(directory).Select(PickupFile.At).ToList());

        public async Task<MemoryStream> DownloadAsync(PickupFile file, long maxBytes, CancellationToken ct)
        {
            await using var remote = sftp.OpenRead(file.SourceRef!);
            return await BoundedRead.CopyAsync(remote, maxBytes, ct);
        }

        public Task MarkDoneAsync(PickupFile file, CancellationToken ct) => Task.CompletedTask;

        public void Dispose() => sftp.Dispose();
    }
}
