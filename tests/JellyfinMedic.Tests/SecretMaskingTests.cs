using System.Xml.Linq;
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

    [Theory]
    [InlineData("const cfg = { apiKey: 'a1b2c3' };", "const cfg = { apiKey: '••••' };")]
    [InlineData("const cfg = { \"apiKey\": \"a1b2c3\" };", "const cfg = { \"apiKey\": \"••••\" };")]
    [InlineData("fetch(url, { headers: { 'X-Api-Key': 'a1b2c3' } });", "fetch(url, { headers: { 'X-Api-Key': '••••' } });")]
    public void The_plugins_view_hides_a_secret_in_a_script_as_the_export_does(string script, string shown)
    {
        Assert.Equal(shown, PluginAuditor.ShownSetting("PluginConfiguration.CustomJavaScriptEntries.CustomJavaScriptEntry[0].Script", new XElement("Script", script)));
    }

    [Theory]
    [InlineData("PluginConfiguration.ApiKey", "a1b2c3", "•••• (hidden)")]
    [InlineData("PluginConfiguration.UseApiKey", "true", "On")]
    [InlineData("PluginConfiguration.Script", "", "(empty)")]
    [InlineData("PluginConfiguration.Script", "token: getToken()", "token: getToken()")]
    public void The_plugins_view_shows_other_settings_as_before(string path, string value, string shown)
    {
        Assert.Equal(shown, PluginAuditor.ShownSetting(path, new XElement(SettingsReader.LeafName(path), value)));
    }

    [Theory]
    [InlineData("X-Api-Key", "a1b2c3", "•••• (hidden)")]
    [InlineData("Title", "Seerr", "Seerr")]
    public void The_plugins_view_hides_the_value_of_a_pair_whose_label_is_secret(string label, string value, string shown)
    {
        var header = new XElement("Header", new XElement("Name", label), new XElement("Value", value));

        Assert.Equal(shown, PluginAuditor.ShownSetting("PluginConfiguration.Headers.Header.Value", header.Element("Value")!));
    }

    [Fact]
    public void The_support_export_still_writes_its_own_mask_for_a_secret_setting()
    {
        Assert.Equal("****", SettingsExporter.Mask("ApiKey", "a1b2c3"));
    }

    [Theory]
    [InlineData("Request failed: Authorization: Bearer abc123def456", "abc123def456")]
    [InlineData("Response status code does not indicate success: 401 (Unauthorized) for http://sonarr:8989/api/v3/series?apikey=0123456789abcdef0123456789abcdef", "0123456789abcdef")]
    public void A_failed_task_error_in_checks_hides_a_token_or_key(string error, string secret)
    {
        Assert.DoesNotContain(secret, DiagnosticsEngine.Shorten(error));
    }

    [Fact]
    public void A_failed_task_error_hides_a_token_that_starts_just_before_it_is_cut()
    {
        // The token starts 13 characters before the cut at 600: cut first, its start would still show.
        string shown = DiagnosticsEngine.Shorten(new string('x', 580) + " token " + Jwt);

        Assert.DoesNotContain("eyJhbGci", shown);
        Assert.EndsWith(" token ••••", shown);
    }

    [Theory]
    [InlineData("QbitUser", "admin")]
    [InlineData("SmtpUser", "mailer")]
    [InlineData("PluginConfiguration.ProxyUser", "jelly")]
    public void The_support_export_hides_an_account_name_kept_under_a_name_ending_in_user(string name, string value)
    {
        Assert.Equal("****", SettingsExporter.Mask(name, value));
    }

    [Theory]
    [InlineData("MaxStreamsPerUser", "3")]
    [InlineData("QbitUser", "")]
    public void The_support_export_keeps_a_count_kept_under_a_name_ending_in_user(string name, string value)
    {
        Assert.Equal(value, SettingsExporter.Mask(name, value));
    }

    [Fact]
    public void The_plugins_view_hides_an_account_name_kept_under_a_name_ending_in_user()
    {
        Assert.Equal("•••• (hidden)", PluginAuditor.ShownSetting("PluginConfiguration.QbitUser", new XElement("QbitUser", "admin")));
    }

    [Theory]
    [InlineData("NotifyTo", "someone@example.com", "••••")]
    [InlineData("Footer", "Mail bob.smith+jf@mail.example.org for access", "Mail •••• for access")]
    public void The_support_export_hides_an_email_address_whatever_the_setting_is_called(string name, string value, string exported)
    {
        Assert.Equal(exported, SettingsExporter.Mask(name, value));
    }

    [Theory]
    [InlineData(".logo { background: url('/web/assets/img/banner-light@2x.png'); }")]
    [InlineData("Uses Jellyfin.Plugin.Template@10.11.0")]
    public void The_support_export_keeps_an_image_name_or_version_that_only_holds_an_at_sign(string css)
    {
        Assert.Equal(css, SettingsExporter.Mask("CustomCss", css));
    }

    [Theory]
    [InlineData("Error opening input file http://prov.example:8080/live/myuser/S3cretPw/123.ts", "Error opening input file http://prov.example:8080/live/••••/••••/123.ts")]
    [InlineData("GET https://prov.example/movie/myuser/S3cretPw/4567.mkv failed", "GET https://prov.example/movie/••••/••••/4567.mkv failed")]
    [InlineData("http://prov.example/series/myuser/S3cretPw/89.mp4", "http://prov.example/series/••••/••••/89.mp4")]
    [InlineData("http://prov.example/timeshift/myuser/S3cretPw/120/2026-10-17:20-00/5.ts", "http://prov.example/timeshift/••••/••••/120/2026-10-17:20-00/5.ts")]
    public void A_stream_address_hides_the_account_in_its_path(string text, string shown)
    {
        Assert.Equal(shown, SettingsReader.MaskSecrets(text));
    }

    [Theory]
    [InlineData("Could not read /data/series/Andor/S01/Andor.S01E01.mkv")]
    [InlineData("Could not read /media/live/concerts/2019/show.mkv")]
    public void A_library_path_that_only_looks_like_a_stream_address_is_left_alone(string text)
    {
        Assert.Equal(text, SettingsReader.MaskSecrets(text));
    }

    [Fact]
    public void A_repeated_stream_error_hides_the_account_in_its_address()
    {
        string shown = LogScanner.Normalise("Error opening http://prov.example:8080/live/myuser/S3cretPw/123.ts");

        Assert.DoesNotContain("myuser", shown);
        Assert.DoesNotContain("S3cretPw", shown);
    }

    [Fact]
    public void The_banner_and_a_failed_task_hide_the_account_in_a_stream_address()
    {
        const string Line = "[2026-10-17 03:12:45.123 +00:00] [ERR] [1] MediaBrowser.MediaEncoding: Error opening http://prov.example:8080/live/myuser/S3cretPw/123.ts";

        Assert.DoesNotContain("S3cretPw", AdminAlerts.Shorten(Line));
        Assert.DoesNotContain("S3cretPw", DiagnosticsEngine.Shorten(Line));
    }

    [Fact]
    public void The_playback_log_reader_hides_the_account_in_an_ffmpeg_error_line()
    {
        string dir = Directory.CreateTempSubdirectory("medic-ffmpeg-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "FFmpeg.Remux-2026-10-17_20-00-00_abc.log"),
                "ffmpeg -i \"http://prov.example:8080/live/myuser/S3cretPw/123.ts\" -codec:v:0 copy\n"
                + "[http @ 0x55] HTTP error 403 Forbidden\n"
                + "[in#0 @ 0x56] Error opening input: http://prov.example:8080/live/myuser/S3cretPw/123.ts\n");

            var result = TranscodeLogReader.Read(dir, DateTime.UtcNow.AddMinutes(-5), null, "vaapi");

            Assert.Contains(result.Errors, e => e.Contains("live/••••/••••/123.ts", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Errors, e => e.Contains("S3cretPw", StringComparison.Ordinal) || e.Contains("myuser", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
