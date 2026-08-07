using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Tests.Controllers;

public class IntegrationWebhookSecretBindingTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SubOne = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid SubTwo = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private static DeliveryEncryptionService Encryption()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    [Fact]
    public void WebhookSecret_DoesNotDecryptForAnotherSubscription()
    {
        var enc = Encryption();
        var blob = enc.Encrypt("signing-secret", CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.OrgIntegrationWebhookSecret, SubOne));

        var act = () => enc.Decrypt(blob, CredentialScope.ForSupplier(
            OrgA, CredentialPurpose.OrgIntegrationWebhookSecret, SubTwo));

        act.Should().Throw<CredentialUnbindableException>();
    }
}
