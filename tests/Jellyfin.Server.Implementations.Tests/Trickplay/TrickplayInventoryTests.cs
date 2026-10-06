using System;
using System.IO;
using Jellyfin.Server.Implementations.Trickplay;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Trickplay;

public class TrickplayInventoryTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void InventoryRequiresEnabledLibraryAndAvailableMedia(bool enabled, bool available, bool expected)
    {
        using var fixture = new InventoryFixture();
        if (!available)
        {
            File.Delete(fixture.Video.Path);
        }

        Assert.Equal(expected, fixture.Manager.NeedsTrickplayGeneration(fixture.Video, new LibraryOptions { EnableTrickplayImageExtraction = enabled }));
    }

    [Fact]
    public void ExistingImportableTilesAndClampedResolutionsAreExcluded()
    {
        using var fixture = new InventoryFixture();
        var options = new LibraryOptions { EnableTrickplayImageExtraction = true };
        var directory = fixture.Manager.GetTrickplayDirectory(fixture.Video, 10, 10, 240);
        Directory.CreateDirectory(directory);
        Assert.True(fixture.Manager.NeedsTrickplayGeneration(fixture.Video, options));
        File.WriteAllText(Path.Combine(directory, "0.jpg"), "tile");
        Assert.False(fixture.Manager.NeedsTrickplayGeneration(fixture.Video, options));
        fixture.Options.WidthResolutions = [160, 320];
        Assert.True(fixture.Manager.NeedsTrickplayGeneration(fixture.Video, options));
    }

    [Fact]
    public void BackdropMediaIsExcluded()
    {
        using var fixture = new InventoryFixture();
        var directory = Path.Combine(fixture.DirectoryPath, "backdrops");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "video.mkv");
        File.Move(fixture.Video.Path, path);
        fixture.Video.Path = path;
        fixture.Source.Path = path;
        Assert.False(fixture.Manager.NeedsTrickplayGeneration(fixture.Video, new LibraryOptions { EnableTrickplayImageExtraction = true }));
    }

    private sealed class InventoryFixture : IDisposable
    {
        public InventoryFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "video.mkv");
            File.WriteAllText(path, "media");
            var video = new Mock<Video> { CallBase = true };
            video.Protected().Setup<bool>("IsActiveRecording").Returns(false);
            Video = video.Object;
            Video.Id = Guid.NewGuid();
            Video.Path = path;
            Video.RunTimeTicks = TimeSpan.FromMinutes(1).Ticks;
            var stream = new MediaStream { Type = MediaStreamType.Video, Width = 240 };
            Source = new MediaSourceInfo { Id = Video.Id.ToString("N"), Path = path, MediaStreams = [stream] };
            video.Setup(x => x.GetMediaStreams()).Returns([stream]);
            video.Setup(x => x.GetMediaSources(false)).Returns([Source]);
            var config = new Mock<IServerConfigurationManager>();
            config.SetupGet(x => x.Configuration).Returns(new ServerConfiguration { TrickplayOptions = Options });
            var paths = new Mock<IPathManager>();
            paths.Setup(x => x.GetTrickplayDirectory(It.IsAny<BaseItem>(), It.IsAny<bool>())).Returns(DirectoryPath);
            Manager = new TrickplayManager(NullLogger<TrickplayManager>.Instance, null!, null!, null!, config.Object, null!, null!, null!, paths.Object);
        }

        public string DirectoryPath { get; }

        public Video Video { get; }

        public MediaSourceInfo Source { get; }

        public TrickplayOptions Options { get; } = new();

        public TrickplayManager Manager { get; }

        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
}
