using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MedicPosters.Posters;
using Jellyfin.Plugin.MedicPosters.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MedicPosters.Api;

/// <summary>Everything under /MedicPosters is for admins only: it changes every film's poster.</summary>
[ApiController]
[Route("MedicPosters")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class PostersController : ControllerBase
{
    private readonly PosterService _service;
    private readonly PosterStore _store;
    private readonly ITaskManager _taskManager;

    public PostersController(PosterService service, PosterStore store, ITaskManager taskManager)
    {
        _service = service;
        _store = store;
        _taskManager = taskManager;
    }

    [HttpGet("Status")]
    public ActionResult GetStatus()
    {
        var config = Plugin.Instance!.Configuration;
        var titles = _service.Titles();
        var libraries = _service.ChosenLibraries();
        var (posters, failures, lastRun) = _store.Read(s => (
            s.Posters.Values.OrderByDescending(p => p.MadeUtc).ToList(),
            s.Failures.Values.OrderByDescending(f => f.Utc).Take(50).ToList(),
            s.LastRun));
        var ids = titles.Select(t => t.Id).Concat(libraries.Select(l => l.Id)).ToHashSet();
        var mine = posters.Where(p => ids.Contains(p.ItemId)).ToList();
        var minePosters = mine.Where(p => p.Kind != PosterService.CoverKind).ToList();
        var progress = _service.Progress;
        bool running = progress is not null
            || _taskManager.ScheduledTasks.Any(t => t.ScheduledTask is MakePostersTask && t.State != TaskState.Idle);

        return Json(new
        {
            settings = new
            {
                libraryIds = config.LibraryIds,
                width = config.Width,
                moments = config.Moments,
                momentTenths = config.MomentTenths,
                framesPerSecond = config.FramesPerSecond,
                colours = config.Colours,
                showTitle = config.ShowTitle,
                showFrame = config.ShowFrame,
                makeLibraryCovers = config.MakeLibraryCovers,
                coverWidth = config.CoverWidth
            },
            libraries = _service.Libraries().Select(l => new { id = l.Id.ToString("N"), name = l.Name }),
            // "films" is every title that gets a poster (films, series, home and music videos); the name is kept for 1.0's page.
            films = titles.Count,
            made = minePosters.Count,
            averageBytes = minePosters.Count == 0 ? 0 : (long)minePosters.Average(p => p.Bytes),
            coverLibraries = config.MakeLibraryCovers ? libraries.Count : 0,
            coversMade = mine.Count(p => p.Kind == PosterService.CoverKind),
            running,
            progress,
            lastRun,
            failures,
            recent = mine.Take(24).Select(p => new { itemId = p.ItemId.ToString("N"), title = p.Title, kind = p.Kind, bytes = p.Bytes, madeUtc = p.MadeUtc })
        });
    }

    [HttpPost("Settings")]
    public ActionResult SaveSettings([FromBody] PosterSettingsRequest request)
    {
        if (request is null)
        {
            return BadRequest();
        }

        var plugin = Plugin.Instance!;
        var c = plugin.Configuration;
        var known = _service.Libraries().Select(l => l.Id).ToHashSet();
        if (request.LibraryIds is not null)
        {
            c.LibraryIds = request.LibraryIds
                .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
                .Where(known.Contains)
                .Select(g => g.ToString("N"))
                .Distinct()
                .ToArray();
        }

        c.Width = Math.Clamp(request.Width ?? c.Width, 120, 600) / 2 * 2;
        c.Moments = Math.Clamp(request.Moments ?? c.Moments, 1, 6);
        c.MomentTenths = Math.Clamp(request.MomentTenths ?? c.MomentTenths, 5, 30);
        c.FramesPerSecond = Math.Clamp(request.FramesPerSecond ?? c.FramesPerSecond, 4, 15);
        c.Colours = Math.Clamp(request.Colours ?? c.Colours, 16, 256);
        c.ShowTitle = request.ShowTitle ?? c.ShowTitle;
        c.ShowFrame = request.ShowFrame ?? c.ShowFrame;
        c.MakeLibraryCovers = request.MakeLibraryCovers ?? c.MakeLibraryCovers;
        c.CoverWidth = Math.Clamp(request.CoverWidth ?? c.CoverWidth, 240, 960) / 2 * 2;
        plugin.SaveConfiguration();
        return NoContent();
    }

    /// <summary>Makes a poster for one title, or a cover for one library (a random one if none is given), without changing anything.</summary>
    [HttpPost("Preview")]
    public async Task<ActionResult> Preview([FromBody] PosterPreviewRequest? request, CancellationToken ct)
    {
        string kind = request?.Kind == PosterService.CoverKind ? PosterService.CoverKind : PosterService.PosterKind;
        var (film, error) = await _service.PreviewAsync(request?.ItemId, kind, ct).ConfigureAwait(false);
        long bytes = error is null && System.IO.File.Exists(_store.PreviewPath) ? new FileInfo(_store.PreviewPath).Length : 0;
        return Json(new { ok = error is null, error, kind, itemId = film?.Id.ToString("N"), title = film?.Name, bytes });
    }

    [HttpGet("Preview/Image")]
    [Produces("image/gif")]
    public ActionResult PreviewImage()
    {
        if (!System.IO.File.Exists(_store.PreviewPath))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(_store.PreviewPath, "image/gif");
    }

    /// <summary>Starts the scheduled task now. With redoAll, films that already have a poster get a new one too.</summary>
    [HttpPost("Run")]
    public ActionResult Run([FromQuery] bool redoAll = false)
    {
        if (_service.Progress is not null)
        {
            return Conflict(new { error = "Posters are already being made." });
        }

        _service.RedoAllNextRun = redoAll;
        _taskManager.QueueScheduledTask<MakePostersTask>();
        return Accepted();
    }

    /// <summary>Stops a run that's going.</summary>
    [HttpPost("Stop")]
    public ActionResult Stop()
    {
        _taskManager.CancelIfRunning<MakePostersTask>();
        return NoContent();
    }

    [HttpPost("Restore/{itemId}")]
    public async Task<ActionResult> Restore([FromRoute] Guid itemId, CancellationToken ct) =>
        await _service.RestoreAsync(itemId, ct).ConfigureAwait(false) ? NoContent() : NotFound();

    /// <summary>Puts every film's own poster back.</summary>
    [HttpPost("RestoreAll")]
    public async Task<ActionResult> RestoreAll(CancellationToken ct)
    {
        if (_service.Progress is not null)
        {
            return Conflict(new { error = "Stop making posters first." });
        }

        int count = await _service.RestoreAllAsync(ct).ConfigureAwait(false);
        return Json(new { restored = count });
    }

    // Jellyfin's own JSON is PascalCase; the page reads camelCase, like the other Medic plugins.
    private ContentResult Json(object body) =>
        Content(JsonSerializer.Serialize(body, PosterStore.JsonOptions), MediaTypeNames.Application.Json);
}

public class PosterSettingsRequest
{
    public string[]? LibraryIds { get; set; }

    public int? Width { get; set; }

    public int? Moments { get; set; }

    public int? MomentTenths { get; set; }

    public int? FramesPerSecond { get; set; }

    public int? Colours { get; set; }

    public bool? ShowTitle { get; set; }

    public bool? ShowFrame { get; set; }

    public bool? MakeLibraryCovers { get; set; }

    public int? CoverWidth { get; set; }
}

public class PosterPreviewRequest
{
    public Guid? ItemId { get; set; }

    /// <summary>"cover" for a library cover; anything else is a poster.</summary>
    public string? Kind { get; set; }
}
