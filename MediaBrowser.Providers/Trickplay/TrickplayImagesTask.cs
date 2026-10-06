using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace MediaBrowser.Providers.Trickplay;

/// <summary>
/// Class TrickplayImagesTask.
/// </summary>
public class TrickplayImagesTask : IScheduledTask
{
    private const int QueryPageLimit = 100;

    private static readonly Gauge _remaining = Metrics.CreateGauge("jellyfin_trickplay_remaining_items", "Videos still awaiting an attempt in the current trickplay task, including the active item; excludes existing tiles and resets when the task ends.");
    private static readonly Gauge _inventoryComplete = Metrics.CreateGauge("jellyfin_trickplay_inventory_complete", "Whether the running trickplay task has finished counting videos requiring generation.");

    private readonly ILogger<TrickplayImagesTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ILocalizationManager _localization;
    private readonly ITrickplayManager _trickplayManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrickplayImagesTask"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="localization">The localization manager.</param>
    /// <param name="trickplayManager">The trickplay manager.</param>
    public TrickplayImagesTask(
        ILogger<TrickplayImagesTask> logger,
        ILibraryManager libraryManager,
        ILocalizationManager localization,
        ITrickplayManager trickplayManager)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _localization = localization;
        _trickplayManager = trickplayManager;
    }

    /// <inheritdoc />
    public string Name => _localization.GetLocalizedString("TaskRefreshTrickplayImages");

    /// <inheritdoc />
    public string Description => _localization.GetLocalizedString("TaskRefreshTrickplayImagesDescription");

    /// <inheritdoc />
    public string Key => "RefreshTrickplayImages";

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        ];
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            SourceTypes = [SourceType.Library],
            IsVirtualItem = false,
            IsFolder = false,
            Recursive = true,
            IncludeOwnedItems = true,
            Limit = QueryPageLimit
        };

        var numberOfVideos = _libraryManager.GetCount(query);

        var remaining = new HashSet<Guid>();
        var inventoryComplete = true;
        _remaining.Set(0);
        _inventoryComplete.Set(0);
        try
        {
            // Inventory before generation so the gauge includes unvisited library items.
            // Keep only IDs and continue refreshing every video for discovery and cleanup.
            for (var index = 0; index < numberOfVideos; index += QueryPageLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                query.StartIndex = index;
                foreach (var video in _libraryManager.GetItemList(query).OfType<Video>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (_trickplayManager.NeedsTrickplayGeneration(video, _libraryManager.GetLibraryOptions(video)))
                        {
                            remaining.Add(video.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error checking trickplay generation eligibility for {ItemName}", video.Name);
                        inventoryComplete = false;
                    }
                }
            }

            _remaining.Set(remaining.Count);
            _inventoryComplete.Set(inventoryComplete ? 1 : 0);
            await RefreshAsync().ConfigureAwait(false);
        }
        finally
        {
            _remaining.Set(0);
            _inventoryComplete.Set(0);
        }

        async Task RefreshAsync()
        {
            var startIndex = 0;
            var numComplete = 0;

            while (startIndex < numberOfVideos)
            {
                query.StartIndex = startIndex;
                var videos = _libraryManager.GetItemList(query).OfType<Video>();

                foreach (var video in videos)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var libraryOptions = _libraryManager.GetLibraryOptions(video);
                        await _trickplayManager.RefreshTrickplayDataAsync(video, false, libraryOptions, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error creating trickplay files for {ItemName}", video.Name);
                    }

                    if (remaining.Remove(video.Id))
                    {
                        _remaining.Set(remaining.Count);
                    }

                    numComplete++;
                    progress.Report(100d * numComplete / numberOfVideos);
                }

                startIndex += QueryPageLimit;
            }

            progress.Report(100);
        }
    }
}
