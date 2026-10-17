using System.Net;
using System.Text;
using Jellyfin.Plugin.MedicProfiles;
using Jellyfin.Plugin.MedicProfiles.Arr;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicProfiles.Tests;

[Collection(PluginInstanceCollection.Name)]
public sealed class ArrClientTests : IDisposable
{
    private const string SignInPage = "<!DOCTYPE html><html><body><form action=\"/login\">Sign in</form></body></html>";

    private readonly string _root = Directory.CreateTempSubdirectory("medic-profiles-tests-").FullName;
    private readonly ArrStore _store;

    public ArrClientTests()
    {
        var paths = new TestPaths(_root);
        var plugin = new Plugin(paths, new DefaultSettingsSerializer());
        plugin.Configuration.SonarrUrl = "http://sonarr:8989";
        plugin.Configuration.RadarrUrl = "http://radarr:7878";
        _store = new ArrStore(paths, NullLogger<ArrStore>.Instance);
        _store.SetKey(ArrApp.Sonarr, "sonarr-key");
        _store.SetKey(ArrApp.Radarr, "radarr-key");
    }

    [Theory]
    [InlineData(400, """{"message":"Not allowed"}""", "Sonarr says: Not allowed")]
    [InlineData(400, """[{"propertyName":"Path","errorMessage":"Path is wrong"}]""", "Sonarr says: Path is wrong")]
    [InlineData(400, """["Path is wrong"]""", "Sonarr answered with 400.")]
    [InlineData(500, "<html>Server error</html>", "Sonarr answered with 500.")]
    [InlineData(404, """{"message":"NotFound"}""", "Sonarr says that wasn't found. It may have been removed already.")]
    public async Task An_error_answer_is_worded_from_what_Sonarr_sent(int status, string body, string message)
    {
        // An answer of any shape is still an answer: never "couldn't reach Sonarr".
        var arr = Client(_ => Answer((HttpStatusCode)status, body));

        using var result = await arr.GetAsync(ArrApp.Sonarr, "queue", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(message, result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Not found</html>")]
    public async Task A_404_that_isnt_from_Sonarr_points_at_the_address(string body)
    {
        // Sonarr words its own 404s as JSON; an empty or HTML one means the address or URL base is wrong.
        var arr = Client(_ => Answer(HttpStatusCode.NotFound, body, "text/html"));

        using var result = await arr.DeleteAsync(ArrApp.Sonarr, "queue/7", null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.DoesNotContain("removed already", result.Message, StringComparison.Ordinal);
        Assert.Contains("URL base", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_passes_when_Sonarr_answers_its_status()
    {
        var arr = Client(_ => Answer(HttpStatusCode.OK, """{"appName":"Sonarr","instanceName":"Sonarr","version":"4.0.15"}"""));

        var (ok, message) = await arr.StatusAsync(ArrApp.Sonarr, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("Sonarr answered (version 4.0.15) and accepted the key.", message);
    }

    [Theory]
    [InlineData(SignInPage, "text/html")]
    [InlineData("""{"appName":"Radarr","version":"5.26.2"}""", "application/json")]
    [InlineData("[]", "application/json")]
    public async Task Test_fails_when_something_else_answers(string body, string type)
    {
        // A proxy's sign-in page answers 200 too, as does Radarr at Sonarr's address.
        var arr = Client(_ => Answer(HttpStatusCode.OK, body, type));

        var (ok, message) = await arr.StatusAsync(ArrApp.Sonarr, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("http://sonarr:8989", message, StringComparison.Ordinal);
        Assert.Contains("URL base", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_fails_on_a_redirect_and_names_where_it_went()
    {
        var arr = Client(request => Redirected(request, "http://auth.example/login?rd=sonarr", HttpMethod.Get, SignInPage, "text/html"));

        var (ok, message) = await arr.StatusAsync(ArrApp.Sonarr, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("http://auth.example/login?rd=sonarr", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_fails_even_when_the_redirect_reaches_Sonarr()
    {
        // Sonarr with a URL base sends base-less calls on with a 307; the address to save is the one it went to.
        var arr = Client(request => Redirected(request, "http://sonarr:8989/sonarr/api/v3/system/status", HttpMethod.Get, """{"appName":"Sonarr","version":"4.0.15"}"""));

        var (ok, message) = await arr.StatusAsync(ArrApp.Sonarr, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("http://sonarr:8989/sonarr/api/v3/system/status", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_that_keeps_the_method_still_works()
    {
        var arr = Client(request => Redirected(request, "http://sonarr:8989/sonarr/api/v3/queue", request.Method, """{"records":[],"totalRecords":0}"""));

        using var result = await arr.GetAsync(ArrApp.Sonarr, "queue", CancellationToken.None);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task A_redirect_that_turns_a_post_into_a_get_fails_and_names_where_it_went()
    {
        // .NET follows a 301 or 302 for a POST with a GET, and GET command answers Sonarr's list of commands.
        var arr = Client(request => Redirected(request, "http://sonarr:8989/sonarr/api/v3/command", HttpMethod.Get, "[]"));

        using var result = await arr.PostRawAsync(ArrApp.Sonarr, "command", "{}", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("http://sonarr:8989/sonarr/api/v3/command", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SignInPage, "text/html")]
    [InlineData("[]", "application/json")]
    public async Task Import_fails_when_the_answer_isnt_a_command(string body, string type)
    {
        var arr = Client(request => request.Method == HttpMethod.Get
            ? Answer(HttpStatusCode.OK, """[{"path":"/downloads/Show.S01E01.mkv","relativePath":"Show.S01E01.mkv"}]""")
            : Answer(HttpStatusCode.OK, body, type));
        var downloads = new DownloadsService(arr, _store);
        var file = new ImportFileRequest { Path = "/downloads/Show.S01E01.mkv", MediaId = 3, EpisodeIds = [11] };

        var (ok, message) = await downloads.ImportAsync(ArrApp.Sonarr, "ABC", "move", [file], "Show", null, CancellationToken.None);

        Assert.False(ok);
        Assert.DoesNotContain("importing", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_passes_when_Sonarr_starts_the_command()
    {
        var arr = Client(request => request.Method == HttpMethod.Get
            ? Answer(HttpStatusCode.OK, """[{"path":"/downloads/Show.S01E01.mkv","relativePath":"Show.S01E01.mkv"}]""")
            : Answer(HttpStatusCode.Created, """{"id":42,"name":"ManualImport","status":"queued"}"""));
        var downloads = new DownloadsService(arr, _store);
        var file = new ImportFileRequest { Path = "/downloads/Show.S01E01.mkv", MediaId = 3, EpisodeIds = [11] };

        var (ok, message) = await downloads.ImportAsync(ArrApp.Sonarr, "ABC", "move", [file], "Show", null, CancellationToken.None);

        Assert.True(ok, message);
    }

    [Fact]
    public async Task A_slow_Sonarr_doesnt_hold_up_Radarr()
    {
        // Sonarr answers only once Radarr has been asked, so asking one after the other leaves Sonarr waiting.
        var radarrAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arr = Client(async request =>
        {
            if (request.RequestUri!.Host == "radarr")
            {
                radarrAsked.TrySetResult();
                return Answer(HttpStatusCode.OK, Queue("radarr-1", "Film.2024.1080p"));
            }

            bool asked = await Task.WhenAny(radarrAsked.Task, Task.Delay(TimeSpan.FromSeconds(5))) == radarrAsked.Task;
            return asked
                ? Answer(HttpStatusCode.OK, Queue("sonarr-1", "Show.S01E01.1080p"))
                : Answer(HttpStatusCode.ServiceUnavailable, "{\"message\":\"Radarr was never asked\"}");
        });
        var downloads = new DownloadsService(arr, _store);

        var (rows, errors, _) = await downloads.QueueAsync(CancellationToken.None);

        Assert.Empty(errors);
        Assert.Equal(new[] { "radarr", "sonarr" }, rows.Select(r => r.App).Order());
    }

    [Fact]
    public async Task A_timeout_says_the_app_didnt_answer_in_time()
    {
        // HttpClient reports its own time limit as a cancellation, which ServiceHttp turns into an error.
        var arr = Client(request => request.RequestUri!.Host == "radarr"
            ? Answer(HttpStatusCode.OK, Queue("radarr-1", "Film.2024.1080p"))
            : throw new TaskCanceledException());
        var downloads = new DownloadsService(arr, _store);

        var (rows, errors, _) = await downloads.QueueAsync(CancellationToken.None);

        Assert.Equal("Sonarr at http://sonarr:8989 didn't answer within 30 seconds.", errors["sonarr"]);
        Assert.Equal("radarr", Assert.Single(rows).App);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ArrClient Client(Func<HttpRequestMessage, HttpResponseMessage> answer) => Client(request => Task.FromResult(answer(request)));

    private ArrClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) =>
        new(new Factory(new Answers(answer)), _store, NullLogger<ArrClient>.Instance);

    private static HttpResponseMessage Answer(HttpStatusCode status, string body, string type = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    // As .NET's own redirect handler does: the request itself is moved on, and changed to a GET for a 301 or 302.
    private static HttpResponseMessage Redirected(HttpRequestMessage request, string to, HttpMethod method, string body, string type = "application/json")
    {
        request.RequestUri = new Uri(to);
        request.Method = method;
        var response = Answer(HttpStatusCode.OK, body, type);
        response.RequestMessage = request;
        return response;
    }

    private static string Queue(string downloadId, string title) =>
        $$"""{"totalRecords":1,"records":[{"id":1,"downloadId":"{{downloadId}}","title":"{{title}}","status":"downloading"}]}""";

    private sealed class Answers(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request);
    }

    // ArrClient disposes each client it gets, so each gets its own over the one handler.
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
