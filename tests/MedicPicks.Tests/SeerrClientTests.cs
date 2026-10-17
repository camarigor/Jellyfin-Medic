using System.Net;
using System.Text;
using Jellyfin.Plugin.MedicPicks.Picks;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicPicks.Tests;

public sealed class SeerrClientTests : IDisposable
{
    private static readonly Guid Person = Guid.NewGuid();

    private readonly string _root = Directory.CreateTempSubdirectory("medic-picks-tests-").FullName;
    private readonly AccountPerHost _seerr = new(new Dictionary<string, int> { ["seerr-a"] = 42, ["seerr-b"] = 43 });
    private readonly HttpClient _http;
    private readonly SeerrClient _client;

    public SeerrClientTests()
    {
        var store = new PicksStore(new TestPaths(_root), NullLogger<PicksStore>.Instance);
        store.SetSeerrKey("seerr-key");
        _http = new HttpClient(_seerr);
        _client = new SeerrClient(new OneClient(_http), store, NullLogger<SeerrClient>.Instance);
    }

    [Fact]
    public async Task Another_Seerr_address_gets_its_own_account_lookup()
    {
        // An id remembered from the old server would make requests and approvals act as someone else on the new one.
        Assert.Equal(42, await _client.FindSeerrUserAsync("http://seerr-a:5055", Person, CancellationToken.None));

        Assert.Equal(43, await _client.FindSeerrUserAsync("http://seerr-b:5055", Person, CancellationToken.None));
        Assert.Equal(new[] { "seerr-a", "seerr-b" }, _seerr.Asked);
    }

    [Theory]
    [InlineData("http://seerr-a:5055/seerr/")]
    [InlineData("HTTP://SEERR-A:5055/seerr")]
    [InlineData(" http://seerr-a:5055/seerr ")]
    public async Task The_same_address_written_another_way_keeps_the_remembered_account(string sameAddress)
    {
        await _client.FindSeerrUserAsync("http://seerr-a:5055/seerr", Person, CancellationToken.None);

        Assert.Equal(42, await _client.FindSeerrUserAsync(sameAddress, Person, CancellationToken.None));
        Assert.Equal(new[] { "seerr-a" }, _seerr.Asked);
    }

    [Theory]
    [InlineData("seerr-a:5055")]
    [InlineData("seerr-a")]
    [InlineData("ftp://seerr-a:5055")]
    public async Task An_address_that_is_not_http_counts_as_Seerr_out_of_reach(string address)
    {
        // Saved before addresses got http:// added on save; My requests catches this one and says Seerr is out of reach.
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _client.FindSeerrUserAsync(address, Person, CancellationToken.None));

        Assert.Contains("http:// or https://", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_seerr.Asked);
    }

    [Fact]
    public async Task Test_doesnt_follow_a_redirect_and_names_where_it_went()
    {
        var (client, sent) = Redirecting(HttpStatusCode.TemporaryRedirect, "https://login.example/?rd=seerr");

        var (ok, message) = await client.TestAsync("http://seerr-a:5055", CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("https://login.example/?rd=seerr", message, StringComparison.Ordinal);
        Assert.Equal(new[] { "http://seerr-a:5055/api/v1/auth/me" }, sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task A_redirected_call_counts_as_Seerr_out_of_reach(HttpStatusCode status)
    {
        // Not followed, so the key and the request stay with the saved address; My requests says Seerr is out of reach.
        var (client, sent) = Redirecting(status, "http://elsewhere.example/api/v1/user/jellyfin/x");

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.FindSeerrUserAsync("http://seerr-a:5055", Person, CancellationToken.None));
        Assert.Null(await client.GetRequestsAsync("http://seerr-a:5055", null, "all", 10, CancellationToken.None));
        Assert.All(sent, url => Assert.StartsWith("http://seerr-a:5055/", url, StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private (SeerrClient Client, List<string> Sent) Redirecting(HttpStatusCode status, string location)
    {
        var sent = new List<string>();
        var http = new HttpClient(new Redirects(status, location, sent));
        var store = new PicksStore(new TestPaths(_root), NullLogger<PicksStore>.Instance);
        return (new SeerrClient(new OneClient(http), store, NullLogger<SeerrClient>.Instance), sent);
    }

    private sealed class Redirects(HttpStatusCode status, string location, List<string> sent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sent.Add(request.RequestUri!.ToString());
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri(location);
            return Task.FromResult(response);
        }
    }

    /// <summary>A Seerr on each host, each with its own account id for the same Jellyfin user.</summary>
    private sealed class AccountPerHost(Dictionary<string, int> ids) : HttpMessageHandler
    {
        public List<string> Asked { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string host = request.RequestUri!.Host;
            Asked.Add(host);
            return Task.FromResult(ids.TryGetValue(host, out int id)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":" + id + "}", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class OneClient(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
