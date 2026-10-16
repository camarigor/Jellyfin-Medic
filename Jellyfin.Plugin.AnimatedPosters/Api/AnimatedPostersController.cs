using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AnimatedPosters.Api;

/// <summary>
/// Serves the client script with the current settings baked in.
/// </summary>
[ApiController]
[Route("AnimatedPosters")]
public class AnimatedPostersController : ControllerBase
{
    private static readonly Lazy<string> ScriptSource = new(() =>
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("Jellyfin.Plugin.AnimatedPosters.Web.animatedPosters.js")
            ?? throw new InvalidOperationException("Embedded script missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    [HttpGet("script.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ContentResult GetScript()
    {
        var c = Plugin.Instance!.Configuration;
        var settings = new
        {
            enabled = c.Enabled,
            interval = (int)(Math.Max(1, c.IntervalSeconds) * 1000),
            fade = (int)(Math.Max(0, c.FadeSeconds) * 1000),
            itemsPerLibrary = Math.Clamp(c.ItemsPerLibrary, 1, 200),
            refreshMinutes = Math.Max(1, c.RefreshMinutes),
            kenBurns = c.KenBurns,
            showLogos = c.ShowLogos,
            originalArt = c.OriginalArt is "slide" or "screen" or "none" ? c.OriginalArt : "slide",
            originalEvery = Math.Max(2, c.OriginalEvery),
            screenOpacity = Math.Clamp(c.ScreenOpacity, 0, 1),
            skipLibraries = c.SkipLibraries
                .Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            hideText = c.HideText,
            hideBorder = c.HideBorder
        };

        Response.Headers.CacheControl = "no-cache";
        var js = "window.AnimatedPostersConfig = " + JsonSerializer.Serialize(settings) + ";\n" + ScriptSource.Value;
        return Content(js, "application/javascript; charset=utf-8");
    }
}
