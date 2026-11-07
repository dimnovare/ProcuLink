using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Api.Services.Documents.Intake;

/// <summary>S3 or S3-compatible (R2, MinIO) bucket pickup. A key is taken once per version: the ledger key includes the eTag.</summary>
public sealed class S3PickupSource(IAmazonS3ClientFactory factory, OutboundRequestGuard guard) : IPickupSource
{
    public string Kind => IntakeKind.S3;

    public async Task<IPickupSession> OpenAsync(FlowIntake intake, JsonElement credentials, CancellationToken ct)
    {
        var config = JsonNode.Parse(intake.ConfigJson)!.AsObject();
        var serviceUrl = config["serviceUrl"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            var guarded = await guard.ValidateAsync(serviceUrl, ct);
            if (!guarded.Allowed) throw new InvalidOperationException(guarded.Reason);
        }

        var secret = credentials.TryGetProperty("secretKey", out var s) ? s.GetString() ?? "" : "";
        var client = factory.Create(config["accessKeyId"]!.GetValue<string>(), secret,
            config["region"]!.GetValue<string>(), string.IsNullOrWhiteSpace(serviceUrl) ? null : serviceUrl);
        return new Session(client, config["bucket"]!.GetValue<string>(), config["prefix"]?.GetValue<string>());
    }

    private sealed class Session(IAmazonS3 s3, string bucket, string? prefix) : IPickupSession
    {
        public async Task<IReadOnlyList<PickupFile>> ListAsync(CancellationToken ct)
        {
            var files = new List<PickupFile>();
            var request = new ListObjectsV2Request { BucketName = bucket, Prefix = string.IsNullOrEmpty(prefix) ? null : prefix };
            ListObjectsV2Response page;
            do
            {
                page = await s3.ListObjectsV2Async(request, ct);
                files.AddRange(page.S3Objects.Where(o => !o.Key.EndsWith('/'))
                    .Select(o => new PickupFile($"s3:{o.Key}@{o.ETag}", Path.GetFileName(o.Key), o.Key)));
                request.ContinuationToken = page.NextContinuationToken;
            } while (page.IsTruncated == true);
            return files;
        }

        public async Task<MemoryStream> DownloadAsync(PickupFile file, long maxBytes, CancellationToken ct)
        {
            using var response = await s3.GetObjectAsync(bucket, file.SourceRef!, ct);
            return await BoundedRead.CopyAsync(response.ResponseStream, maxBytes, ct);
        }

        public Task MarkDoneAsync(PickupFile file, CancellationToken ct) => Task.CompletedTask;

        public void Dispose() => s3.Dispose();
    }
}
