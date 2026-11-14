using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services.Delivery;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

public class DeliveryRepairTests
{
    [Theory]
    [InlineData("sftp", "{\"overwriteExisting\":false}", true)]
    [InlineData("sftp", "{}", false)]
    [InlineData("http", "{\"overwriteExisting\":false}", false)]
    public void CannotRepairItsOwnFile_only_for_a_file_drop_with_overwrite_off(string protocol, string json, bool expected) =>
        DeliveryRepair.CannotRepairItsOwnFile(new SupplierDeliveryConfig { Protocol = protocol, ConfigJson = json })
            .Should().Be(expected);
}
