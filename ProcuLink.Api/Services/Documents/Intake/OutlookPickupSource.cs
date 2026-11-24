using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Google;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Core.Services.Google;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Microsoft;

namespace ProcuLink.Api.Services.Documents.Intake;

/// <summary>
/// An Outlook or Microsoft 365 mailbox, through Microsoft Graph with the connected account's token (plan 30). It behaves
/// like <see cref="ImapPickupSource"/>: unread mail in one folder that matches the filters gives one file per readable
/// attachment, or the body as email.txt when there is none; a matching message is marked read once its files are done,
/// and a message that does not match is never touched. Nothing here logs a token or a mail body.
/// </summary>
public sealed class OutlookPickupSource(
    IHttpClientFactory http, ILinkedAccountTokens tokens, LinkedAccountAvailability availability, DocumentEngineGate gate) : IPickupSource
{
    private const string Graph = "https://graph.microsoft.com/v1.0/";
    private const int MaxPages = 10;

    public string Kind => IntakeKind.Outlook;

    public async Task<IPickupSession> OpenAsync(FlowIntake intake, JsonElement credentials, CancellationToken ct)
    {
        if (!availability.On(intake.OrgId, LinkedAccountProviders.Outlook))
            throw new PickupRefusedException("This pickup isn't switched on for this workspace yet.");
        var config = JsonNode.Parse(intake.ConfigJson)!.AsObject();
        string? Config(string key) => config[key]?.GetValue<string>();

        // An id that is no Guid finds no account, which reads as "reconnect".
        Guid.TryParse(Config("accountId"), out var accountId);
        var token = await tokens.GetAccessTokenAsync(intake.OrgId, accountId, LinkedAccountProviders.Outlook, ct);
        if (token.Token is null) throw new PickupRefusedException(token.Failure!.ErrorMessage!);

        var client = new GraphClient(http.CreateClient(MicrosoftOAuthClient.HttpClientName), token.Token);
        return new Session(client, await FolderAsync(client, Config("folder"), ct),
            Config("fromFilter"), Config("subjectFilter"), gate.AcceptedFormats(intake.OrgId));
    }

    /// <summary>The well-known inbox, else a top-level folder or one inside the inbox with that display name.</summary>
    private static async Task<string> FolderAsync(GraphClient graph, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Equals("inbox", StringComparison.OrdinalIgnoreCase)) return "inbox";
        var filter = Uri.EscapeDataString($"displayName eq '{name.Trim().Replace("'", "''")}'");
        foreach (var parent in new[] { "me/mailFolders", "me/mailFolders/inbox/childFolders" })
        {
            var found = (await graph.GetAsync($"{Graph}{parent}?$filter={filter}&$select=id", ct))["value"]?.AsArray();
            if (found is { Count: > 0 }) return found[0]!["id"]!.GetValue<string>();
        }
        throw new PickupRefusedException($"There is no folder called “{name.Trim()}” in this mailbox.");
    }

    /// <summary>Graph calls with the bearer token and immutable ids, so a message keeps its key when it is moved.</summary>
    private sealed class GraphClient(HttpClient client, string token)
    {
        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken ct, HttpContent? content = null, string? prefer = null)
        {
            var request = new HttpRequestMessage(method, url) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");
            if (prefer is not null) request.Headers.TryAddWithoutValidation("Prefer", prefer);
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new HttpRequestException("Microsoft did not answer in time.", ex);
            }
            if (response.IsSuccessStatusCode) return response;
            var status = response.StatusCode;
            response.Dispose();
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new PickupRefusedException("Microsoft refused access to this mailbox. Reconnect the account, or ask your Microsoft 365 admin to approve ProcuLink.");
            throw new HttpRequestException($"Microsoft answered HTTP {(int)status}.", null, status);
        }

        public async Task<JsonNode> GetAsync(string url, CancellationToken ct, string? prefer = null)
        {
            using var response = await SendAsync(HttpMethod.Get, url, ct, prefer: prefer);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? new JsonObject();
        }

        public async Task MarkReadAsync(string messageId, CancellationToken ct)
        {
            using var _ = await SendAsync(HttpMethod.Patch, $"{Graph}me/messages/{Uri.EscapeDataString(messageId)}", ct,
                new StringContent("""{"isRead":true}""", Encoding.UTF8, "application/json"));
        }
    }

    private sealed class Session(GraphClient graph, string folder, string? fromFilter, string? subjectFilter, IReadOnlyList<string> accepted) : IPickupSession
    {
        private readonly Dictionary<string, (string MessageId, string AttachmentId)> _parts = [];
        private readonly Dictionary<string, int> _open = [];
        private readonly List<(string MessageId, string Sender)> _unreadable = [];
        private readonly List<string> _failed = [];
        private readonly Dictionary<string, string> _bodies = [];

        public async Task<IReadOnlyList<PickupFile>> ListAsync(CancellationToken ct)
        {
            var files = new List<PickupFile>();
            var url = $"{Graph}me/mailFolders/{Uri.EscapeDataString(folder)}/messages?$filter={Uri.EscapeDataString("isRead eq false")}"
                      + "&$select=id,subject,from,hasAttachments,internetMessageHeaders&$top=50";
            for (var page = 0; page < MaxPages && url is not null; page++)
            {
                var body = await graph.GetAsync(url, ct);
                foreach (var m in body["value"]?.AsArray() ?? new JsonArray())
                {
                    // One message Graph keeps failing on must not stall the folder: it stays unread for the next run.
                    try { await AddAsync(m!, files, ct); }
                    catch (HttpRequestException) { _failed.Add(m!["from"]?["emailAddress"]?["address"]?.GetValue<string>() ?? "an unknown sender"); }
                }
                // Only Graph's own next page: the link comes from the response, and it carries the token.
                var next = body["@odata.nextLink"]?.GetValue<string>();
                url = next is not null && next.StartsWith(Graph, StringComparison.Ordinal) ? next : null;
            }
            return files;
        }

        private async Task AddAsync(JsonNode m, List<PickupFile> files, CancellationToken ct)
        {
            var id = m["id"]!.GetValue<string>();
            var sender = m["from"]?["emailAddress"]?["address"]?.GetValue<string>();
            if (!ImapPickupSource.Matches(fromFilter, subjectFilter, new[] { sender }.OfType<string>(), m["subject"]?.GetValue<string>())) return;
            // Graph lists headers in the message's order (top down), so the first Authentication-Results is the topmost.
            // A list item without them is asked again on its own; still none means unconfirmed, so the document waits.
            var listed = m["internetMessageHeaders"]?.AsArray()
                ?? (await graph.GetAsync($"{Graph}me/messages/{Uri.EscapeDataString(id)}?$select=internetMessageHeaders", ct))["internetMessageHeaders"]?.AsArray();
            var headers = (listed ?? new JsonArray())
                .Select(h => (h?["name"]?.GetValue<string>() ?? "", h?["value"]?.GetValue<string>() ?? ""));
            var unconfirmed = !SenderAuthentication.Mailbox(headers, sender);

            var readable = new List<(string Id, string Name)>();
            if (m["hasAttachments"]?.GetValue<bool>() == true)
            {
                var list = await graph.GetAsync($"{Graph}me/messages/{Uri.EscapeDataString(id)}/attachments?$select=id,name,isInline", ct);
                foreach (var a in list["value"]?.AsArray() ?? new JsonArray())
                {
                    var name = a?["name"]?.GetValue<string>();
                    if (a?["@odata.type"]?.GetValue<string>() == "#microsoft.graph.fileAttachment" && a["isInline"]?.GetValue<bool>() != true
                        && ImapPickupSource.Readable(name, accepted))
                        readable.Add((a["id"]!.GetValue<string>(), name!));
                }
            }

            var bodyOnly = readable.Count == 0 && accepted.Contains("txt");
            // A run claims at most MaxFilesPerRun files, so later bodies stay unread for the next run instead of being downloaded now.
            if (bodyOnly && _bodies.Count >= FlowPickupJob.MaxFilesPerRun) return;
            if (bodyOnly && await BodyAsync(id, ct) is { } text)
            {
                var key = $"outlook:{id}#body";
                _bodies[key] = text;
                _parts[key] = (id, "");
                _open[id] = 1;
                files.Add(new PickupFile(key, "email.txt", sender, unconfirmed));
                return;
            }

            if (readable.Count == 0)
            {
                _unreadable.Add((id, sender ?? "an unknown sender"));
                return;
            }

            _open[id] = readable.Count;
            foreach (var (attachmentId, name) in readable)
            {
                // Graph's ids, never the sender's Message-ID: a sender can reuse that to suppress or swap a file.
                var key = $"outlook:{id}#{attachmentId}";
                _parts[key] = (id, attachmentId);
                files.Add(new PickupFile(key, name, sender, unconfirmed));
            }
        }

        /// <summary>The body as plain text, or null when it is blank or over the file limit.</summary>
        private async Task<string?> BodyAsync(string id, CancellationToken ct)
        {
            var message = await graph.GetAsync($"{Graph}me/messages/{Uri.EscapeDataString(id)}?$select=body", ct, "outlook.body-content-type=\"text\"");
            var text = message["body"]?["content"]?.GetValue<string>();
            return text is null || Encoding.UTF8.GetByteCount(text) > IngressLimits.MaxFileBytes ? null : EmailBodyText.Resolve(text, null);
        }

        public async Task<MemoryStream> DownloadAsync(PickupFile file, long maxBytes, CancellationToken ct)
        {
            if (_bodies.TryGetValue(file.Key, out var body))
                return Encoding.UTF8.GetByteCount(body) > maxBytes
                    ? throw new CatalogFileTooLargeException(maxBytes)
                    : new MemoryStream(Encoding.UTF8.GetBytes(body));
            // No check against Graph's size: it counts encoding overhead. The bounded read enforces the real limit, and an oversize
            // file is reported as skipped by the job, the same as IMAP.
            var (messageId, attachmentId) = _parts[file.Key];
            using var response = await graph.SendAsync(HttpMethod.Get,
                $"{Graph}me/messages/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(attachmentId)}/$value", ct);
            await using var content = await response.Content.ReadAsStreamAsync(ct);
            return await BoundedRead.CopyAsync(content, maxBytes, ct);
        }

        public async Task MarkDoneAsync(PickupFile file, CancellationToken ct)
        {
            var id = _parts[file.Key].MessageId;
            if (--_open[id] == 0) await graph.MarkReadAsync(id, ct);
        }

        public async Task<IReadOnlyList<string>> SettleAsync(CancellationToken ct)
        {
            foreach (var (id, _) in _unreadable) await graph.MarkReadAsync(id, ct);
            // Only the latest, so one run's message stays one sentence however many such mails arrived.
            return _unreadable.TakeLast(1).Select(u => $"An email from {u.Sender} had no file we can read attached.")
                .Concat(_failed.TakeLast(1).Select(s => $"An email from {s} couldn't be opened, so it stays unread for the next try.")).ToList();
        }

        public void Dispose() { }
    }
}
