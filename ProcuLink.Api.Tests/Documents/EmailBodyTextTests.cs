using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public class EmailBodyTextTests
{
    [Fact]
    public void Markup_only_html_is_not_a_body() => Assert.Null(EmailBodyText.Resolve(null, "<p>&nbsp;</p>"));
}
