using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// After an erase on request the workspace's sign-in accounts go at Clerk, but never a person who also
/// works in another workspace, never platform staff, and nothing at all when the member list is unreadable.
/// </summary>
public class ClerkAccountRemoverTests
{
    private const string OrgId = "org_test_1";

    private sealed class FakeClerk(bool failMembers) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath.Replace("/v1/", "");
            static string Memberships(int total) =>
                $"{{\"data\":[{{\"object\":\"organization_membership\",\"organization\":{{\"object\":\"organization\",\"id\":\"{OrgId}\"}}}}],\"total_count\":{total}}}";
            var (status, json) = (request.Method.Method, path) switch
            {
                ("GET", $"organizations/{OrgId}/memberships") when !failMembers => (HttpStatusCode.OK,
                    "{\"data\":[" + string.Join(",", new[] { "only", "shared", "staff" }.Select(u =>
                        $"{{\"public_user_data\":{{\"user_id\":\"user_test_{u}\",\"identifier\":\"{u}@example.test\"}}}}")) + "],\"total_count\":3}"),
                ("GET", "users/user_test_only/organization_memberships") => (HttpStatusCode.OK, Memberships(1)),
                ("GET", "users/user_test_shared/organization_memberships") => (HttpStatusCode.OK, Memberships(2)),
                ("GET", "users/user_test_staff/organization_memberships") => (HttpStatusCode.OK, Memberships(1)),
                ("DELETE", "users/user_test_only") => (HttpStatusCode.NotFound, "{}"),
                ("DELETE", $"organizations/{OrgId}") => (HttpStatusCode.OK, "{}"),
                _ => (HttpStatusCode.InternalServerError, "{}"),
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://api.clerk.com/v1/") };
    }

    private static ClerkAccountRemover Remover(FakeClerk handler, string? key)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Clerk:SecretKey"] = key,
            ["Admin:Emails"] = "staff@example.test",
        }).Build();
        return new ClerkAccountRemover(new Factory(handler), config, new AdminAllowlist(config), NullLogger<ClerkAccountRemover>.Instance);
    }

    private static List<string> Deletes(FakeClerk handler) =>
        handler.Requests.Where(r => r.Method == HttpMethod.Delete).Select(r => r.RequestUri!.AbsolutePath.Replace("/v1/", "")).ToList();

    [Fact]
    public async Task Removes_only_members_who_belong_nowhere_else_and_are_not_staff_then_the_organisation()
    {
        var handler = new FakeClerk(failMembers: false);

        var result = await Remover(handler, "sk_test_fake").RemoveAsync(OrgId, CancellationToken.None);

        Deletes(handler).Should().Equal("users/user_test_only", $"organizations/{OrgId}");
        result.Step.Should().Be("done");
        result.AccountsRemoved.Should().Be(1);
        result.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_member_read_deletes_nothing_and_no_key_calls_nothing()
    {
        var failing = new FakeClerk(failMembers: true);
        (await Remover(failing, "sk_test_fake").RemoveAsync(OrgId, CancellationToken.None)).Step.Should().Be("failed");
        Deletes(failing).Should().BeEmpty();

        var noKey = new FakeClerk(failMembers: false);
        (await Remover(noKey, null).RemoveAsync(OrgId, CancellationToken.None)).Step.Should().Be("skipped_no_key");
        noKey.Requests.Should().BeEmpty();
    }
}
