namespace ApiAggregator.Admin.Tests;

public class StartupTests
{
    [Fact]
    public void A_dev_signing_key_outside_development_stops_the_admin_api_from_starting()
    {
        using var misconfigured = new AdminApp { Settings = { ["Auth:DevSigningKey"] = "dev-only-signing-key-change-me-0123456789abcdef" } };

        var error = Assert.ThrowsAny<Exception>(() => misconfigured.CreateClient());
        Assert.Contains("Auth:DevSigningKey must not be set outside Development", error.ToString());
    }
}
