using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Medic.Shared;

/// <summary>
/// What calls to Seerr, Sonarr and Radarr have in common. Medic Picks and Medic Profiles each compile this file in.
/// </summary>
internal static class ServiceHttp
{
    /// <summary>
    /// Sends a request that gives up after <paramref name="limit"/>. Running out of time fails like a service that
    /// can't be reached (<see cref="HttpRequestException"/>), so a service that hangs can't hold a page or a task;
    /// a cancelled caller still comes through as cancelled.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, TimeSpan limit, string service, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try
        {
            return await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException($"{service} didn't answer within {limit.TotalSeconds:0} seconds", ex);
        }
    }

    /// <summary>
    /// The service's own words in an error answer: <c>{"message": "..."}</c>, or the first of a list of validation
    /// errors (<c>[{"errorMessage": "..."}]</c>) as Sonarr and Radarr send. Null for an answer of any other shape.
    /// </summary>
    public static string? ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Array
                ? root.GetArrayLength() > 0 ? Json.Text(root[0], "errorMessage") : null
                : Json.Text(root, "message");
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
