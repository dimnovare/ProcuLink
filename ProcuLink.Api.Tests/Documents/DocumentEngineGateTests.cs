using Microsoft.Extensions.Options;
using ProcuLink.Api.Services.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public class DocumentEngineGateTests
{
    private static DocumentEngineGate Gate(DocumentEngineOptions o) => new(Options.Create(o));

    [Fact]
    public void IsEnabled_true_only_for_listed_org()
    {
        var listed = Guid.NewGuid();
        var gate = Gate(new() { EnabledOrgIds = [listed.ToString()] });
        Assert.True(gate.IsEnabled(listed));
        Assert.False(gate.IsEnabled(Guid.NewGuid()));
    }

    [Fact]
    public void IsEnabled_true_for_any_org_when_EnabledForAll()
    {
        Assert.True(Gate(new() { EnabledForAll = true }).IsEnabled(Guid.NewGuid()));
    }
}
