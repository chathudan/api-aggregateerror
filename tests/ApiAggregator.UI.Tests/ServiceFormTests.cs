using ApiAggregator.Admin.Services;

namespace ApiAggregator.UI.Tests;

/// <summary>Form-model rules that need no browser: how a spec's server becomes the suggested base address.</summary>
public sealed class ServiceFormTests
{
    [Fact]
    public void A_relative_server_is_resolved_against_the_spec_address() =>
        Assert.Equal("http://localhost:5100/d", NewServiceForm.ResolveServer("/d", "http://localhost:5100/d/openapi.json"));

    [Fact]
    public void An_absolute_https_server_is_kept() =>
        Assert.Equal("https://billing.internal/api", NewServiceForm.ResolveServer("https://billing.internal/api", "http://x/spec.json"));

    [Fact]
    public void A_relative_server_in_pasted_spec_text_suggests_nothing() =>
        Assert.Null(NewServiceForm.ResolveServer("/d", ""));

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://files.example/api")]
    public void A_server_that_isnt_http_or_https_is_never_suggested(string server) =>
        Assert.Null(NewServiceForm.ResolveServer(server, "http://localhost:5100/d/openapi.json"));
}
