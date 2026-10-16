using System.Net;

namespace MedicShared.Tests;

public sealed class ServiceHttpTests
{
    [Fact]
    public async Task A_service_that_stops_answering_is_out_of_reach_not_a_cancelled_caller()
    {
        using var client = new HttpClient(new NeverAnswers());
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://seerr:5055/api/v1/status");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => ServiceHttp.SendAsync(client, request, TimeSpan.FromMilliseconds(200), "Seerr", CancellationToken.None));

        Assert.StartsWith("Seerr didn't answer within", ex.Message);
    }

    [Fact]
    public async Task A_cancelled_caller_is_still_cancelled()
    {
        using var client = new HttpClient(new NeverAnswers());
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://seerr:5055/api/v1/status");
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ServiceHttp.SendAsync(client, request, TimeSpan.FromSeconds(30), "Seerr", caller.Token));
    }

    [Fact]
    public async Task An_answer_in_time_comes_through()
    {
        using var client = new HttpClient(new Answers());
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://sonarr:8989/api/v3/queue");

        using var response = await ServiceHttp.SendAsync(client, request, TimeSpan.FromSeconds(30), "Sonarr", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"message":"Not allowed"}""", "Not allowed")]
    [InlineData("""[{"propertyName":"Path","errorMessage":"Path is wrong"}]""", "Path is wrong")]
    [InlineData("""["Path is wrong"]""", null)]
    [InlineData("[]", null)]
    [InlineData("""{"message":1}""", null)]
    [InlineData("\"Not allowed\"", null)]
    [InlineData("<html>Sign in</html>", null)]
    [InlineData("", null)]
    public void The_service_s_own_words_are_read_from_an_error_answer(string body, string? expected) =>
        Assert.Equal(expected, ServiceHttp.ErrorMessage(body));

    private sealed class NeverAnswers : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("never reached");
        }
    }

    private sealed class Answers : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
    }
}
