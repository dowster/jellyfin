using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Providers.Trickplay;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Prometheus;
using Xunit;

namespace Jellyfin.Providers.Tests.Trickplay;

public class TrickplayImagesTaskTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InventoryExcludesExistingTilesAndResetsAfterCompletionOrCancellation(bool fail, bool cancel)
    {
        var existing = new Video { Id = Guid.NewGuid() };
        var first = new Video { Id = Guid.NewGuid() };
        var second = new Video { Id = Guid.NewGuid() };
        var options = new LibraryOptions();
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetCount(It.IsAny<InternalItemsQuery>())).Returns(3);
        library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new BaseItem[] { existing, first, second });
        library.Setup(x => x.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(options);
        var manager = new Mock<ITrickplayManager>();
        manager.Setup(x => x.NeedsTrickplayGeneration(first, options)).Returns(true);
        manager.Setup(x => x.NeedsTrickplayGeneration(second, options)).Returns(true);
        using var cancellation = new CancellationTokenSource();
        var samples = new List<(Video Video, string Metrics)>();
        manager.Setup(x => x.RefreshTrickplayDataAsync(It.IsAny<Video>(), false, options, It.IsAny<CancellationToken>()))
            .Returns(async (Video video, bool replace, LibraryOptions libraryOptions, CancellationToken token) =>
            {
                var metrics = await ExportAsync();
                samples.Add((video, metrics));
                if (video == first)
                {
                    if (cancel)
                    {
                        await cancellation.CancelAsync();
                        token.ThrowIfCancellationRequested();
                    }

                    if (fail)
                    {
                        throw new IOException("Generation failed");
                    }
                }
            });
        var task = new TrickplayImagesTask(NullLogger<TrickplayImagesTask>.Instance, library.Object, Mock.Of<ILocalizationManager>(), manager.Object, Configuration(1));
        var execution = task.ExecuteAsync(new Progress<double>(), cancellation.Token);
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        }
        else
        {
            await execution;
            manager.Verify(x => x.RefreshTrickplayDataAsync(second, false, options, It.IsAny<CancellationToken>()), Times.Once);
        }

        Assert.Equal(cancel ? 2 : 3, samples.Count);
        foreach (var sample in samples)
        {
            Assert.Contains("jellyfin_trickplay_inventory_complete 1\n", sample.Metrics, StringComparison.Ordinal);
            Assert.Contains($"jellyfin_trickplay_remaining_items {(sample.Video == second ? 1 : 2)}\n", sample.Metrics, StringComparison.Ordinal);
        }

        var final = await ExportAsync();
        Assert.Contains("jellyfin_trickplay_remaining_items 0\n", final, StringComparison.Ordinal);
        Assert.Contains("jellyfin_trickplay_inventory_complete 0\n", final, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedInventoryDoesNotPublishPartialCount()
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetCount(It.IsAny<InternalItemsQuery>())).Returns(1);
        library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Throws(new IOException("Library unavailable"));
        var task = new TrickplayImagesTask(NullLogger<TrickplayImagesTask>.Instance, library.Object, Mock.Of<ILocalizationManager>(), Mock.Of<ITrickplayManager>(), Configuration(1));
        await Assert.ThrowsAsync<IOException>(() => task.ExecuteAsync(new Progress<double>(), CancellationToken.None));
        var metrics = await ExportAsync();
        Assert.Contains("jellyfin_trickplay_remaining_items 0\n", metrics, StringComparison.Ordinal);
        Assert.Contains("jellyfin_trickplay_inventory_complete 0\n", metrics, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task GenerationIsBoundedAndProgressIsMonotonicAcrossPages(int concurrency)
    {
        var videos = new Video[105];
        for (var i = 0; i < videos.Length; i++)
        {
            videos[i] = new Video { Id = Guid.NewGuid() };
        }

        var options = new LibraryOptions();
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetCount(It.IsAny<InternalItemsQuery>())).Returns(videos.Length);
        library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) => videos.AsSpan(query.StartIndex ?? 0, Math.Min(100, videos.Length - (query.StartIndex ?? 0))).ToArray());
        library.Setup(x => x.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(options);
        var manager = new Mock<ITrickplayManager>();
        manager.Setup(x => x.NeedsTrickplayGeneration(It.IsAny<Video>(), options)).Returns(true);
        var active = 0;
        var peak = 0;
        var peakLock = new Lock();
        var reachedLimit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentDictionary<Guid, byte>();
        manager.Setup(x => x.RefreshTrickplayDataAsync(It.IsAny<Video>(), false, options, It.IsAny<CancellationToken>()))
            .Returns(async (Video video, bool replace, LibraryOptions libraryOptions, CancellationToken token) =>
            {
                Assert.True(seen.TryAdd(video.Id, 0));
                var count = Interlocked.Increment(ref active);
                Assert.InRange(count, 1, concurrency);
                lock (peakLock)
                {
                    peak = Math.Max(peak, count);
                }

                if (count == concurrency)
                {
                    reachedLimit.TrySetResult();
                }

                try
                {
                    await release.Task.WaitAsync(token);
                    if (video == videos[0])
                    {
                        throw new IOException("One item fails without stopping the task");
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });
        var progress = new RecordingProgress();
        var task = new TrickplayImagesTask(NullLogger<TrickplayImagesTask>.Instance, library.Object, Mock.Of<ILocalizationManager>(), manager.Object, Configuration(concurrency));
        var execution = task.ExecuteAsync(progress, CancellationToken.None);
        try
        {
            await reachedLimit.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(concurrency, Volatile.Read(ref active));
            Assert.Equal(concurrency, seen.Count);
            Assert.Contains($"jellyfin_trickplay_remaining_items {videos.Length}\n", await ExportAsync(), StringComparison.Ordinal);
        }
        finally
        {
            release.TrySetResult();
        }

        await execution.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(concurrency, peak);
        Assert.Equal(0, active);
        Assert.Equal(videos.Length, seen.Count);
        Assert.Equal(100, progress.Values[^1]);
        for (var i = 1; i < progress.Values.Count; i++)
        {
            Assert.True(progress.Values[i] >= progress.Values[i - 1]);
        }

        Assert.Contains("jellyfin_trickplay_remaining_items 0\n", await ExportAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDrainsActiveJobsAndResetsInventory()
    {
        var videos = new[] { new Video { Id = Guid.NewGuid() }, new Video { Id = Guid.NewGuid() }, new Video { Id = Guid.NewGuid() } };
        var options = new LibraryOptions();
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetCount(It.IsAny<InternalItemsQuery>())).Returns(videos.Length);
        library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(videos);
        library.Setup(x => x.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(options);
        var manager = new Mock<ITrickplayManager>();
        manager.Setup(x => x.NeedsTrickplayGeneration(It.IsAny<Video>(), options)).Returns(true);
        var active = 0;
        var started = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Setup(x => x.RefreshTrickplayDataAsync(It.IsAny<Video>(), false, options, It.IsAny<CancellationToken>()))
            .Returns(async (Video video, bool replace, LibraryOptions libraryOptions, CancellationToken token) =>
            {
                Interlocked.Increment(ref started);
                if (Interlocked.Increment(ref active) == 2)
                {
                    ready.TrySetResult();
                }

                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });
        using var cancellation = new CancellationTokenSource();
        var task = new TrickplayImagesTask(NullLogger<TrickplayImagesTask>.Instance, library.Object, Mock.Of<ILocalizationManager>(), manager.Object, Configuration(2));
        var execution = task.ExecuteAsync(new RecordingProgress(), cancellation.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(0, active);
        Assert.Equal(2, started);
        var metrics = await ExportAsync();
        Assert.Contains("jellyfin_trickplay_remaining_items 0\n", metrics, StringComparison.Ordinal);
        Assert.Contains("jellyfin_trickplay_inventory_complete 0\n", metrics, StringComparison.Ordinal);
    }

    private static IServerConfigurationManager Configuration(int concurrency)
        => Mock.Of<IServerConfigurationManager>(x => x.Configuration == new ServerConfiguration { TrickplayOptions = new TrickplayOptions { MaxConcurrentJobs = concurrency } });

    private static async Task<string> ExportAsync()
    {
        using var stream = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }
}
