using System.Net;
using System.Text;
using Jellyfin.Plugin.MedicProfiles;
using Jellyfin.Plugin.MedicProfiles.Arr;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicProfiles.Tests;

/// <summary>Searching Sonarr's or Radarr's library when a download's match is wrong.</summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class DownloadsSearchTests : IDisposable
{
    private const string Library = """
        [
          { "id": 1, "title": "The Long Film", "year": 2019 },
          { "id": 2, "title": "Long", "year": 2021 },
          { "id": 3, "title": "Something Else", "year": 2020 }
        ]
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("medic-profiles-tests-").FullName;
    private readonly ArrStore _store;
    private readonly List<string> _asked = new();

    public DownloadsSearchTests()
    {
        var paths = new TestPaths(_root);
        var plugin = new Plugin(paths, new DefaultSettingsSerializer());
        plugin.Configuration.RadarrUrl = "http://radarr:7878";
        _store = new ArrStore(paths, NullLogger<ArrStore>.Instance);
        _store.SetKey(ArrApp.Radarr, "radarr-key");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // The whole library can be thousands of entries, so a search with nothing to match isn't worth fetching it.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-.!")]
    public async Task A_search_with_nothing_to_match_doesnt_fetch_the_library(string query)
    {
        var (results, error) = await Service().SearchLibraryAsync(ArrApp.Radarr, query, CancellationToken.None);

        Assert.Null(error);
        Assert.Empty(results!);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task A_search_matches_titles_and_puts_the_closest_first()
    {
        var (results, error) = await Service().SearchLibraryAsync(ArrApp.Radarr, "long", CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(new[] { 2, 1 }, results!.Select(r => r.Id));
        Assert.EndsWith("/api/v3/movie", Assert.Single(_asked), StringComparison.Ordinal);
    }

    // The ids belong to the server they came from: sent to another one, an import would land on whatever film has
    // that id there, so a list kept from before the address or key changed mustn't be offered.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_kept_library_isnt_used_once_the_address_or_key_changes(bool newAddress)
    {
        var service = Service(request => request.Headers.GetValues("X-Api-Key").Single() == "other-key" || request.RequestUri!.Host == "radarr2"
            ? """[ { "id": 7, "title": "The Long Film", "year": 2019 } ]"""
            : Library);
        Assert.Equal(new[] { 2, 1 }, (await service.SearchLibraryAsync(ArrApp.Radarr, "long", CancellationToken.None)).Results!.Select(r => r.Id));

        if (newAddress)
        {
            Plugin.Instance!.Configuration.RadarrUrl = "http://radarr2:7878";
        }
        else
        {
            _store.SetKey(ArrApp.Radarr, "other-key");
        }

        var (results, error) = await service.SearchLibraryAsync(ArrApp.Radarr, "long", CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(new[] { 7 }, results!.Select(r => r.Id));
        Assert.Equal(2, _asked.Count);
    }

    // Within the same server the list is kept, so typing a title doesn't fetch thousands of entries per letter.
    [Fact]
    public async Task The_library_is_kept_while_the_server_stays_the_same()
    {
        var service = Service();
        await service.SearchLibraryAsync(ArrApp.Radarr, "long", CancellationToken.None);

        var (results, _) = await service.SearchLibraryAsync(ArrApp.Radarr, "film", CancellationToken.None);

        Assert.Equal(new[] { 1 }, results!.Select(r => r.Id));
        Assert.Single(_asked);
    }

    private DownloadsService Service(Func<HttpRequestMessage, string>? library = null)
    {
        var arr = new ArrClient(new Factory(new Answers(request =>
        {
            _asked.Add(request.RequestUri!.AbsolutePath);
            string body = library is null ? Library : library(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request });
        })), _store, NullLogger<ArrClient>.Instance);
        return new DownloadsService(arr, _store);
    }

    private sealed class Answers(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
