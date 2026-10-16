using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class SecretMaskingTests
{
    // Made up, but shaped like real web tokens (JWT): the same kind of token for two different people.
    private const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhbGljZSJ9.c2lnbmF0dXJlLWFsaWNl";
    private const string OtherJwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJib2IifQ.c2lnbmF0dXJlLWJvYg";

    [Theory]
    [InlineData("Send failed for https://api.telegram.org/bot123456789:AAHfake1token2for3tests4only/sendMessage", "Send failed for https://api.telegram.org/bot••••/sendMessage")]
    [InlineData("Token rejected: " + Jwt, "Token rejected: ••••")]
    [InlineData("Proxy refused Authorization: Bearer abc123def456", "Proxy refused Authorization: Bearer ••••")]
    public void A_repeated_error_hides_a_secret_that_holds_digits(string message, string shown)
    {
        Assert.Equal(shown, LogScanner.Normalise(message));
    }

    [Fact]
    public void Two_errors_that_differ_only_by_their_token_count_as_one()
    {
        Assert.Equal(LogScanner.Normalise("Token rejected: " + Jwt), LogScanner.Normalise("Token rejected: " + OtherJwt));
    }

    [Fact]
    public void The_banner_hides_a_credential_in_the_line_it_quotes()
    {
        string shown = AdminAlerts.Shorten("[2026-10-16 03:12:45.123 +00:00] [FTL] [1] Main: Proxy refused Authorization: Bearer abc123def456");

        Assert.DoesNotContain("abc123def456", shown);
        Assert.EndsWith("Authorization: Bearer ••••", shown);
    }

    [Fact]
    public void The_banner_hides_a_token_that_starts_just_before_the_line_is_cut()
    {
        // The token starts 14 characters before the cut at 220: cut first, its start would still show.
        string shown = AdminAlerts.Shorten("[2026-10-16 03:12:45.123 +00:00] [FTL] [1] Main: " + new string('x', 150) + " token " + Jwt);

        Assert.DoesNotContain("eyJhbGci", shown);
        Assert.EndsWith(" token ••••", shown);
    }

    [Theory]
    [InlineData("const cfg = { apiKey: 'a1b2c3' };", "const cfg = { apiKey: '****' };")]
    [InlineData("const token = `a1b2c3`;", "const token = `****`;")]
    [InlineData("fetch(url, { headers: { \"X-Api-Key\": \"a1b2c3\" } });", "fetch(url, { headers: { \"X-Api-Key\": \"****\" } });")]
    public void The_support_export_hides_a_secret_in_a_script_whatever_its_quotes(string script, string exported)
    {
        Assert.Equal(exported, SettingsExporter.Mask("Script", script));
    }

    [Theory]
    [InlineData("token: getToken()")]
    [InlineData("placeholder: 'Enter your password'")]
    public void The_support_export_leaves_code_and_text_that_only_name_a_secret(string script)
    {
        Assert.Equal(script, SettingsExporter.Mask("Script", script));
    }
}
