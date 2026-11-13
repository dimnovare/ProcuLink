using FluentAssertions;
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
    public void LookupSuggestionsOn_needs_the_engine_and_the_org_or_ForAll()
    {
        var org = Guid.NewGuid();
        Gate(new() { LookupSuggestionOrgIds = [org.ToString()] }).LookupSuggestionsOn(org).Should().BeFalse();
        Gate(new() { EnabledOrgIds = [org.ToString()], LookupSuggestionOrgIds = [org.ToString()] }).LookupSuggestionsOn(org).Should().BeTrue();
        Gate(new() { EnabledOrgIds = [org.ToString()] }).LookupSuggestionsOn(org).Should().BeFalse();
        Gate(new() { EnabledForAll = true, LookupSuggestionsForAll = true }).LookupSuggestionsOn(Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void NativeConnectorsOn_needs_the_engine_and_the_org_or_ForAll()
    {
        var org = Guid.NewGuid();
        Gate(new() { NativeConnectorOrgIds = [org.ToString()] }).NativeConnectorsOn(org).Should().BeFalse();
        Gate(new() { EnabledOrgIds = [org.ToString()] }).NativeConnectorsOn(org).Should().BeFalse();
        Gate(new() { EnabledOrgIds = [org.ToString()], NativeConnectorOrgIds = [org.ToString()] }).NativeConnectorsOn(org).Should().BeTrue();
        Gate(new() { EnabledForAll = true, NativeConnectorsForAll = true }).NativeConnectorsOn(Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_true_for_any_org_when_EnabledForAll()
    {
        Assert.True(Gate(new() { EnabledForAll = true }).IsEnabled(Guid.NewGuid()));
    }
}
