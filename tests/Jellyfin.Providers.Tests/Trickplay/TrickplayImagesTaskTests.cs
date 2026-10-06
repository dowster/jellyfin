using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        var task = new TrickplayImagesTask(NullLogger<TrickplayImagesTask>.Instance, library.Object, Mock.Of<ILocalizationManager>(), manager.Object);
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
        var task = new TrickplayImagesTask(NullLogger<TrickplayImagesTask>.Instance, library.Object, Mock.Of<ILocalizationManager>(), Mock.Of<ITrickplayManager>());
        await Assert.ThrowsAsync<IOException>(() => task.ExecuteAsync(new Progress<double>(), CancellationToken.None));
        var metrics = await ExportAsync();
        Assert.Contains("jellyfin_trickplay_remaining_items 0\n", metrics, StringComparison.Ordinal);
        Assert.Contains("jellyfin_trickplay_inventory_complete 0\n", metrics, StringComparison.Ordinal);
    }

    private static async Task<string> ExportAsync()
    {
        using var stream = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
