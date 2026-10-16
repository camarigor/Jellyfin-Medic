using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimatedPosters;

/// <summary>
/// Adds a script tag for the plugin to the web client's index.html as it is served,
/// so nothing on disk is modified.
/// </summary>
public class ScriptInjectionMiddleware
{
    private const string Marker = "data-animated-posters";
    private readonly RequestDelegate _next;
    private readonly ILogger<ScriptInjectionMiddleware> _logger;

    public ScriptInjectionMiddleware(RequestDelegate next, ILogger<ScriptInjectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsIndexRequest(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Ask for an uncompressed, full response so the HTML can be edited.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");
        context.Request.Headers.Remove("Range");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;
        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false)
            && !context.Response.Headers.ContainsKey("Content-Encoding");

        if (!isHtml || Plugin.Instance?.Configuration.Enabled != true)
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        var html = Encoding.UTF8.GetString(buffer.ToArray());
        if (!html.Contains(Marker, StringComparison.Ordinal))
        {
            // index.html lives at {baseUrl}/web/, so a relative path keeps any base URL intact.
            var tag = $"<script {Marker} defer src=\"../AnimatedPosters/script.js?v={DateTime.UtcNow.Ticks}\"></script>";
            var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            html = at >= 0 ? html.Insert(at, tag) : html + tag;
            _logger.LogDebug("Injected Animated Posters script into {Path}", context.Request.Path);
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static bool IsIndexRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;
        return path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }
}
