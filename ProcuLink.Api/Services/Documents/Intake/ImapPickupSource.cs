using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Api.Services.Documents.Intake;

/// <summary>
/// Mailbox pickup. Unseen messages that match the filters give one file per readable attachment; a message that does not
/// match is left untouched, and a matching one is marked seen once all its files are done.
/// </summary>
public sealed class ImapPickupSource(OutboundRequestGuard guard, DocumentEngineGate gate) : IPickupSource
{
    public string Kind => IntakeKind.Imap;

    public async Task<IPickupSession> OpenAsync(FlowIntake intake, JsonElement credentials, CancellationToken ct)
    {
        var config = JsonNode.Parse(intake.ConfigJson)!.AsObject();
        var host = config["host"]!.GetValue<string>();
        var port = config["port"]!.GetValue<int>();
        var guarded = await guard.ValidateHostAsync(host, port, ct);
        if (!guarded.Allowed) throw new InvalidOperationException(guarded.Reason);

        var client = new ImapClient();
        Stream? stream = null;
        try
        {
            // The guard's pinned socket, so the host is not resolved a second time; the host name still drives TLS.
            stream = await guard.OpenGuardedStreamAsync(host, port, ct);
            await client.ConnectAsync(stream, host, port,
                // Never StartTlsWhenAvailable: a stripped STARTTLS would send the password in clear.
                config["useSsl"]!.GetValue<bool>() ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
            await client.AuthenticateAsync(config["username"]!.GetValue<string>(),
                credentials.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "", ct);
            var folder = await client.GetFolderAsync(config["folder"]!.GetValue<string>(), ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            return new Session(client, folder, folder.UidValidity, config["fromFilter"]?.GetValue<string>(), config["subjectFilter"]?.GetValue<string>(),
                gate.AcceptedFormats(intake.OrgId));
        }
        catch
        {
            client.Dispose();
            stream?.Dispose();
            throw;
        }
    }

    /// <summary>The attachment's extension is one the org accepts; both sides are compared without the dot.</summary>
    internal static bool Readable(string? fileName, IReadOnlyList<string> accepted) =>
        accepted.Contains(Path.GetExtension(fileName ?? "").TrimStart('.'), StringComparer.OrdinalIgnoreCase);

    /// <summary>Any sender address contains any <paramref name="fromFilter"/> term AND the subject contains any <paramref name="subjectFilter"/> term; a blank filter matches all.</summary>
    internal static bool Matches(string? fromFilter, string? subjectFilter, IEnumerable<string> senders, string? subject)
    {
        static string[] Terms(string? list) =>
            (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        static bool Any(string[] terms, IEnumerable<string> values) =>
            terms.Length == 0 || values.Any(v => terms.Any(t => v.Contains(t, StringComparison.OrdinalIgnoreCase)));

        return Any(Terms(fromFilter), senders) && Any(Terms(subjectFilter), [subject ?? ""]);
    }

    private sealed class Session(ImapClient client, IMailFolder folder, uint uidValidity, string? fromFilter, string? subjectFilter, IReadOnlyList<string> accepted) : IPickupSession
    {
        private readonly Dictionary<string, (UniqueId Uid, BodyPartBasic Part)> _parts = [];
        private readonly Dictionary<UniqueId, int> _open = [];
        private readonly List<(UniqueId Uid, string Sender)> _unreadable = [];
        private readonly Dictionary<string, string> _bodies = [];

        public async Task<IReadOnlyList<PickupFile>> ListAsync(CancellationToken ct)
        {
            var files = new List<PickupFile>();
            var uids = await folder.SearchAsync(SearchQuery.NotSeen, ct);
            if (uids.Count == 0) return files;

            foreach (var m in await folder.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure, ct))
            {
                var senders = m.Envelope.From.Mailboxes.Select(b => b.Address).ToList();
                if (!Matches(fromFilter, subjectFilter, senders, m.Envelope.Subject)) continue;

                var readable = m.Attachments.Where(a => Readable(a.FileName, accepted)).ToList();
                if (readable.Count == 0 && accepted.Contains("txt") && await BodyAsync(m, ct) is { } body)
                {
                    // Nothing readable was attached, so the body is the document.
                    var key = $"imap:{uidValidity}:{m.UniqueId.Id}#body";
                    _bodies[key] = body;
                    _parts[key] = (m.UniqueId, (m.TextBody ?? m.HtmlBody)!);
                    _open[m.UniqueId] = 1;
                    files.Add(new PickupFile(key, "email.txt", senders.FirstOrDefault()));
                    continue;
                }

                if (readable.Count == 0)
                {
                    _unreadable.Add((m.UniqueId, senders.FirstOrDefault() ?? "an unknown sender"));
                    continue;
                }

                _open[m.UniqueId] = readable.Count;
                foreach (var part in readable)
                {
                    // The server's ids, not the sender's Message-ID: a sender can reuse that to suppress or swap a file.
                    var key = $"imap:{uidValidity}:{m.UniqueId.Id}#{part.PartSpecifier}";
                    _parts[key] = (m.UniqueId, part);
                    files.Add(new PickupFile(key, part.FileName!, senders.FirstOrDefault()));
                }
            }

            return files;
        }

        /// <summary>A part's text, or null when it is absent or declares more than the file limit.</summary>
        private async Task<string?> TextOfAsync(UniqueId uid, BodyPartText? part, CancellationToken ct) =>
            part is null || part.Octets > IngressLimits.MaxFileBytes ? null
                : (await folder.GetBodyPartAsync(uid, part, ct) as TextPart)?.Text;

        private async Task<string?> BodyAsync(IMessageSummary m, CancellationToken ct) =>
            EmailBodyText.Resolve(await TextOfAsync(m.UniqueId, m.TextBody, ct), await TextOfAsync(m.UniqueId, m.HtmlBody, ct));

        public async Task<MemoryStream> DownloadAsync(PickupFile file, long maxBytes, CancellationToken ct)
        {
            if (_bodies.TryGetValue(file.Key, out var body))
                return Encoding.UTF8.GetByteCount(body) > maxBytes
                    ? throw new CatalogFileTooLargeException(maxBytes)
                    : new MemoryStream(Encoding.UTF8.GetBytes(body));
            var (uid, part) = _parts[file.Key];
            var entity = (MimePart)await folder.GetBodyPartAsync(uid, part, ct);
            await using var content = entity.Content.Open();
            return await BoundedRead.CopyAsync(content, maxBytes, ct);
        }

        public async Task MarkDoneAsync(PickupFile file, CancellationToken ct)
        {
            var uid = _parts[file.Key].Uid;
            if (--_open[uid] == 0) await folder.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, ct);
        }

        public async Task<IReadOnlyList<string>> SettleAsync(CancellationToken ct)
        {
            foreach (var (uid, _) in _unreadable) await folder.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, ct);
            // Only the latest, so one run's message stays one sentence however many such mails arrived.
            return _unreadable.TakeLast(1).Select(u => $"An email from {u.Sender} had no file we can read attached.").ToList();
        }

        public void Dispose() => client.Dispose();
    }
}
