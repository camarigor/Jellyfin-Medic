using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicPosters.Posters;

/// <summary>Where a run has got to, for the settings page.</summary>
public class RunProgress
{
    public int Total { get; set; }

    public int Done { get; set; }

    public string? Current { get; set; }
}

/// <summary>
/// Finds the films, makes their posters and swaps them in, and puts the films' own posters back.
/// A film's own poster file is never changed or deleted: Medic Posters only points Jellyfin at
/// another picture, and remembers the old one.
/// </summary>
public class PosterService
{
    private readonly ILibraryManager _libraryManager;
    private readonly PosterMaker _maker;
    private readonly PosterStore _store;
    private readonly ILogger<PosterService> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);
    private volatile RunProgress? _progress;

    public PosterService(ILibraryManager libraryManager, PosterMaker maker, PosterStore store, ILogger<PosterService> logger)
    {
        _libraryManager = libraryManager;
        _maker = maker;
        _store = store;
        _logger = logger;
    }

    /// <summary>Set by "Make them all again": the next run also redoes films that already have one.</summary>
    public bool RedoAllNextRun { get; set; }

    public RunProgress? Progress => _progress;

    /// <summary>Every film library on the server.</summary>
    public IReadOnlyList<(Guid Id, string Name)> FilmLibraries() =>
        _libraryManager.GetVirtualFolders()
            .Where(f => f.CollectionType == CollectionTypeOptions.movies && Guid.TryParse(f.ItemId, out _))
            .Select(f => (Guid.Parse(f.ItemId), f.Name))
            .ToList();

    /// <summary>The films in the libraries chosen in the settings (all film libraries if none are chosen).</summary>
    public IReadOnlyList<Movie> Films()
    {
        var chosen = (Plugin.Instance?.Configuration.LibraryIds ?? Array.Empty<string>())
            .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet();
        // A library's films sit under its folders on disk, which is what Jellyfin files them by, not the library itself.
        var folders = FilmLibraries()
            .Where(l => chosen.Count == 0 || chosen.Contains(l.Id))
            .Select(l => _libraryManager.GetItemById(l.Id))
            .OfType<CollectionFolder>()
            .SelectMany(f => f.PhysicalFolderIds)
            .Distinct()
            .ToArray();
        if (folders.Length == 0)
        {
            return Array.Empty<Movie>();
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            TopParentIds = folders,
            Recursive = true,
            IsVirtualItem = false
        }).OfType<Movie>().ToList();
    }

    /// <summary>Why a film can't have a poster made, or null if it can.</summary>
    public static string? CantMake(BaseItem film)
    {
        if (string.IsNullOrEmpty(film.Path) || film.IsShortcut || !File.Exists(film.Path))
        {
            return "It isn't a video file on this server (a stream, a disc folder or a missing file).";
        }

        return null;
    }

    /// <summary>
    /// Makes posters for every film that needs one. Films with a Medic poster are skipped, and so are films
    /// whose poster was changed by hand (or by a metadata refresh) since, unless <see cref="RedoAllNextRun"/> is set.
    /// </summary>
    public async Task RunAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        bool redo = RedoAllNextRun;
        RedoAllNextRun = false;
        var summary = new RunSummary { StartedUtc = DateTime.UtcNow };
        try
        {
            var spec = PosterSpec.From(Plugin.Instance!.Configuration);
            var films = Films();
            _progress = new RunProgress { Total = films.Count };
            _logger.LogInformation("Medic Posters: checking {Count} films{Redo}", films.Count, redo ? ", making every poster again" : string.Empty);

            for (int i = 0; i < films.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var film = films[i];
                _progress = new RunProgress { Total = films.Count, Done = i, Current = film.Name };
                // A metadata refresh may have swapped the film's own poster back in while Medic Posters wasn't watching.
                await KeepOursAsync(film.Id, ct).ConfigureAwait(false);
                var record = _store.Read(s => s.Posters.GetValueOrDefault(film.Id));
                string? current = (_libraryManager.GetItemById(film.Id) ?? film).GetImageInfo(ImageType.Primary, 0)?.Path;
                bool hasOurs = record is not null && _store.IsOurs(current) && File.Exists(current);
                bool changedElsewhere = record is not null && !_store.IsOurs(current);

                if (!redo && (hasOurs || changedElsewhere))
                {
                    if (changedElsewhere)
                    {
                        summary.LeftAlone++;
                    }
                }
                else
                {
                    string? error = await MakeForFilmAsync(film, spec, ct).ConfigureAwait(false);
                    if (error is null)
                    {
                        summary.Made++;
                    }
                    else
                    {
                        summary.Failed++;
                    }
                }

                progress?.Report(100.0 * (i + 1) / films.Count);
            }
        }
        catch (OperationCanceledException)
        {
            summary.Cancelled = true;
            throw;
        }
        finally
        {
            summary.FinishedUtc = DateTime.UtcNow;
            _store.Update(s => s.LastRun = summary);
            _progress = null;
            _running.Release();
            _logger.LogInformation("Medic Posters: made {Made}, failed {Failed}, left {LeftAlone} alone", summary.Made, summary.Failed, summary.LeftAlone);
        }
    }

    /// <summary>Makes one film's poster and swaps it in. Returns null when it worked, otherwise why not.</summary>
    public async Task<string?> MakeForFilmAsync(Movie film, PosterSpec spec, CancellationToken ct)
    {
        string? error = CantMake(film);
        if (error is null)
        {
            string output = _store.PosterPath(film.Id);
            string temp = output + ".new.gif";
            error = await _maker.MakeAsync(film.Path, film.Name, Seconds(film), IsHdr(film), spec, temp, ct).ConfigureAwait(false);
            if (error is null)
            {
                File.Move(temp, output, overwrite: true);
                await UsePosterAsync(film, output, spec.Width, spec.Height, ct).ConfigureAwait(false);
                _store.Update(s => s.Failures.Remove(film.Id));
                return null;
            }

            try { File.Delete(temp); } catch (IOException) { }
        }

        _store.Update(s => s.Failures[film.Id] = new PosterFailure { ItemId = film.Id, Title = film.Name, Reason = error, Utc = DateTime.UtcNow });
        return error;
    }

    /// <summary>Makes a poster for one film into the preview file, without changing the film. Returns the film used, or why it didn't work.</summary>
    public async Task<(Movie? Film, string? Error)> PreviewAsync(Guid? itemId, PosterSpec spec, CancellationToken ct)
    {
        var films = Films();
        Movie? film = itemId is { } id && id != Guid.Empty
            ? films.FirstOrDefault(f => f.Id == id)
            : films.OrderBy(_ => Random.Shared.Next()).FirstOrDefault(f => CantMake(f) is null);
        if (film is null)
        {
            return (null, films.Count == 0 ? "There are no films in the chosen libraries." : "None of the films is a video file this server can read.");
        }

        string? error = CantMake(film) ?? await _maker.MakeAsync(film.Path, film.Name, Seconds(film), IsHdr(film), spec, _store.PreviewPath, ct).ConfigureAwait(false);
        return (film, error);
    }

    /// <summary>Puts a film's own poster back and deletes Medic's. Returns false if Medic never made one for it.</summary>
    public async Task<bool> RestoreAsync(Guid itemId, CancellationToken ct)
    {
        var record = _store.Read(s => s.Posters.GetValueOrDefault(itemId));
        if (record is null)
        {
            return false;
        }

        if (_libraryManager.GetItemById(itemId) is BaseItem film)
        {
            var current = film.GetImageInfo(ImageType.Primary, 0);
            // Only undo Medic's own poster: one chosen by hand since then stays.
            if (current is null || _store.IsOurs(current.Path))
            {
                var images = film.ImageInfos.Where(i => i.Type != ImageType.Primary || !_store.IsOurs(i.Path)).ToList();
                if (record.Original is { } o)
                {
                    images.Add(new ItemImageInfo { Type = ImageType.Primary, Path = o.Path, DateModified = o.DateModified, Width = o.Width, Height = o.Height, BlurHash = o.BlurHash });
                }

                film.ImageInfos = images.ToArray();
                await film.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);
            }
        }

        try { File.Delete(_store.PosterPath(itemId)); } catch (IOException) { }
        _store.Update(s => s.Posters.Remove(itemId));
        return true;
    }

    /// <summary>Puts every film's own poster back. Returns how many films were changed.</summary>
    public async Task<int> RestoreAllAsync(CancellationToken ct)
    {
        await _running.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int count = 0;
            foreach (var id in _store.Read(s => s.Posters.Keys.ToList()))
            {
                ct.ThrowIfCancellationRequested();
                if (await RestoreAsync(id, ct).ConfigureAwait(false))
                {
                    count++;
                }
            }

            _store.Update(s => s.Failures.Clear());
            return count;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>
    /// Puts Medic's poster back when a metadata refresh has swapped in the film's own poster again (Jellyfin reads
    /// poster.jpg and the like from the film's folder on every refresh). Any other poster is someone's new choice and
    /// stays. Returns whether Medic's poster was put back.
    /// </summary>
    public async Task<bool> KeepOursAsync(Guid itemId, CancellationToken ct)
    {
        var record = _store.Read(s => s.Posters.GetValueOrDefault(itemId));
        string ours = _store.PosterPath(itemId);
        if (record?.Original is null || !File.Exists(ours) || _libraryManager.GetItemById(itemId) is not BaseItem film)
        {
            return false;
        }

        var current = film.GetImageInfo(ImageType.Primary, 0);
        // The same file, unchanged: an upload under Edit Images is saved under the same name, but is a new file.
        if (current is null || _store.IsOurs(current.Path) || !SamePath(current.Path, record.Original.Path)
            || Math.Abs((current.DateModified - record.Original.DateModified).TotalSeconds) > 2)
        {
            return false;
        }

        _logger.LogInformation("Medic Posters: a refresh put back {Title}'s own poster; using the animated one again", film.Name);
        await UsePosterAsync(film, ours, record.Width, record.Height, ct).ConfigureAwait(false);
        return true;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task UsePosterAsync(BaseItem film, string path, int width, int height, CancellationToken ct)
    {
        var current = film.GetImageInfo(ImageType.Primary, 0);
        var existing = _store.Read(s => s.Posters.GetValueOrDefault(film.Id));

        // The film's own poster: the one it has now, unless that's already Medic's.
        SavedImage? original = existing?.Original;
        if (current is not null && !_store.IsOurs(current.Path))
        {
            original = new SavedImage { Path = current.Path, DateModified = current.DateModified, Width = current.Width, Height = current.Height, BlurHash = current.BlurHash };
        }

        var poster = new ItemImageInfo
        {
            Type = ImageType.Primary,
            Path = path,
            DateModified = File.GetLastWriteTimeUtc(path),
            Width = width,
            Height = height
        };
        film.ImageInfos = film.ImageInfos.Where(i => i.Type != ImageType.Primary).Append(poster).ToArray();
        await film.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);

        _store.Update(s => s.Posters[film.Id] = new PosterRecord
        {
            ItemId = film.Id,
            Title = film.Name,
            MadeUtc = DateTime.UtcNow,
            Bytes = new FileInfo(path).Length,
            Width = width,
            Height = height,
            Original = original
        });
    }

    private static double? Seconds(BaseItem film) => film.RunTimeTicks is > 0 ? film.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond : null;

    private static bool IsHdr(BaseItem film)
    {
        var video = film.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);
        return video is not null && video.VideoRange == VideoRange.HDR;
    }
}
