using System.Net;
using System.Text;
using Jellyfin.Plugin.MedicProfiles;
using Jellyfin.Plugin.MedicProfiles.Arr;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicProfiles.Tests;

public sealed class ArrClientTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("medic-profiles-tests-").FullName;
    private readonly ArrStore _store;

    public ArrClientTests()
    {
        var paths = new TestPaths(_root);
        var plugin = new Plugin(paths, new DefaultSettingsSerializer());
        plugin.Configuration.SonarrUrl = "http://sonarr:8989";
        _store = new ArrStore(paths, NullLogger<ArrStore>.Instance);
        _store.SetKey(ArrApp.Sonarr, "sonarr-key");
    }

    [Theory]
    [InlineData(400, """{"message":"Not allowed"}""", "Sonarr says: Not allowed")]
    [InlineData(400, """[{"propertyName":"Path","errorMessage":"Path is wrong"}]""", "Sonarr says: Path is wrong")]
    [InlineData(400, """["Path is wrong"]""", "Sonarr answered with 400.")]
    [InlineData(500, "<html>Server error</html>", "Sonarr answered with 500.")]
    public async Task An_error_answer_is_worded_from_what_Sonarr_sent(int status, string body, string message)
    {
        // An answer of any shape is still an answer: never "couldn't reach Sonarr".
        var arr = new ArrClient(new Factory(new Answers((HttpStatusCode)status, body)), _store, NullLogger<ArrClient>.Instance);

        using var result = await arr.GetAsync(ArrApp.Sonarr, "queue", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(message, result.Message);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Answers(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    // ArrClient disposes each client it gets, so each gets its own over the one handler.
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
