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
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Api.Services.Documents.Intake;

/// <summary>
/// Mailbox pickup. Unseen messages that match the filters give one file per readable attachment; a message that does not
/// match is left untouched, and a matching one is marked seen once all its files are done.
/// </summary>
public sealed class ImapPickupSource(OutboundRequestGuard guard) : IPickupSource
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
        try
        {
            await client.ConnectAsync(host, port,
                config["useSsl"]!.GetValue<bool>() ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, ct);
            await client.AuthenticateAsync(config["username"]!.GetValue<string>(),
                credentials.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "", ct);
            var folder = await client.GetFolderAsync(config["folder"]!.GetValue<string>(), ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            return new Session(client, folder, config["fromFilter"]?.GetValue<string>(), config["subjectFilter"]?.GetValue<string>());
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Any sender address contains any <paramref name="fromFilter"/> term AND the subject contains any <paramref name="subjectFilter"/> term; a blank filter matches all.</summary>
    internal static bool Matches(string? fromFilter, string? subjectFilter, IEnumerable<string> senders, string? subject)
    {
        static string[] Terms(string? list) =>
            (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        static bool Any(string[] terms, IEnumerable<string> values) =>
            terms.Length == 0 || values.Any(v => terms.Any(t => v.Contains(t, StringComparison.OrdinalIgnoreCase)));

        return Any(Terms(fromFilter), senders) && Any(Terms(subjectFilter), [subject ?? ""]);
    }

    private sealed class Session(ImapClient client, IMailFolder folder, string? fromFilter, string? subjectFilter) : IPickupSession
    {
        private readonly Dictionary<string, (UniqueId Uid, BodyPartBasic Part)> _parts = [];
        private readonly Dictionary<UniqueId, int> _open = [];
        private readonly List<(UniqueId Uid, string Sender)> _unreadable = [];

        public async Task<IReadOnlyList<PickupFile>> ListAsync(CancellationToken ct)
        {
            var files = new List<PickupFile>();
            var uids = await folder.SearchAsync(SearchQuery.NotSeen, ct);
            if (uids.Count == 0) return files;

            foreach (var m in await folder.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure, ct))
            {
                var senders = m.Envelope.From.Mailboxes.Select(b => b.Address).ToList();
                if (!Matches(fromFilter, subjectFilter, senders, m.Envelope.Subject)) continue;

                var readable = m.Attachments.Where(a => FlowPickupJob.Extensions.Contains(
                    Path.GetExtension(a.FileName ?? ""), StringComparer.OrdinalIgnoreCase)).ToList();
                if (readable.Count == 0)
                {
                    _unreadable.Add((m.UniqueId, senders.FirstOrDefault() ?? "an unknown sender"));
                    continue;
                }

                _open[m.UniqueId] = readable.Count;
                foreach (var part in readable)
                {
                    var key = $"imap:{m.Envelope.MessageId ?? m.UniqueId.ToString()}#{part.PartSpecifier}";
                    _parts[key] = (m.UniqueId, part);
                    files.Add(new PickupFile(key, part.FileName!, senders.FirstOrDefault()));
                }
            }

            return files;
        }

        public async Task<MemoryStream> DownloadAsync(PickupFile file, long maxBytes, CancellationToken ct)
        {
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
            return _unreadable.Select(u => $"An email from {u.Sender} had no PDF, CSV or Excel file attached.").ToList();
        }

        public void Dispose() => client.Dispose();
    }
}
