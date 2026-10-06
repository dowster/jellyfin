using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Trickplay;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Trickplay;

public sealed class TrickplayParallelGenerationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;

    public TrickplayParallelGenerationTests()
    {
        Directory.CreateDirectory(_directory);
        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_directory, "test.db")};Pooling=False")
            .Options;
        using var context = CreateDbContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task RefreshesShareCapacitySerializeSameVideoAndRecoverAfterCancellation()
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateDbContext);
        var paths = new Mock<IPathManager>();
        paths.Setup(x => x.GetTrickplayDirectory(It.IsAny<BaseItem>(), It.IsAny<bool>()))
            .Returns((BaseItem item, bool saveWithMedia) => Path.Combine(_directory, item.Id.ToString("N"), "trickplay"));
        var config = Mock.Of<IServerConfigurationManager>(x => x.Configuration == new ServerConfiguration
        {
            TrickplayOptions = new TrickplayOptions { MaxConcurrentJobs = 2 }
        });
        using var started = new SemaphoreSlim(0);
        var active = 0;
        var encoder = new Mock<IMediaEncoder>();
        encoder.Setup(x => x.ExtractVideoImagesOnIntervalAccelerated(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<MediaSourceInfo>(),
            It.IsAny<MediaStream>(),
            It.IsAny<int>(),
            It.IsAny<TimeSpan>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<int?>(),
            It.IsAny<int?>(),
            It.IsAny<System.Diagnostics.ProcessPriorityClass?>(),
            It.IsAny<bool>(),
            It.IsAny<EncodingHelper>(),
            It.IsAny<CancellationToken>()))
            .Returns(new InvocationFunc(invocation => ExtractAsync((CancellationToken)invocation.Arguments[^1])));
        using var manager = new TrickplayManager(
            NullLogger<TrickplayManager>.Instance,
            encoder.Object,
            null!,
            null!,
            config,
            null!,
            factory.Object,
            null!,
            paths.Object);
        var options = new LibraryOptions { EnableTrickplayImageExtraction = true };
        var first = CreateVideo();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var firstJob = manager.RefreshTrickplayDataAsync(first, false, options, cancellation.Token);
        var duplicate = manager.RefreshTrickplayDataAsync(first, false, options, cancellation.Token);
        var secondJob = manager.RefreshTrickplayDataAsync(CreateVideo(), false, options, cancellation.Token);
        var waiting = manager.RefreshTrickplayDataAsync(CreateVideo(), false, options, cancellation.Token);
        var all = Task.WhenAll(firstJob, duplicate, secondJob, waiting);
        try
        {
            Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(2, Volatile.Read(ref active));
            Assert.Equal(2, encoder.Invocations.Count);
            Assert.False(duplicate.IsCompleted);
            Assert.False(waiting.IsCompleted);
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => all.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(0, active);
        Assert.Equal(2, encoder.Invocations.Count);

        using var retryCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var retry = manager.RefreshTrickplayDataAsync(first, false, options, retryCancellation.Token);
        try
        {
            Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        finally
        {
            await retryCancellation.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
        Assert.Equal(0, active);

        async Task<string> ExtractAsync(CancellationToken token)
        {
            Assert.InRange(Interlocked.Increment(ref active), 1, 2);
            started.Release();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return string.Empty;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }

    public void Dispose() => Directory.Delete(_directory, true);

    private JellyfinDbContext CreateDbContext()
    {
        var appPaths = Mock.Of<IApplicationPaths>();
        return new JellyfinDbContext(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(appPaths, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private Video CreateVideo()
    {
        var video = new Mock<Video> { CallBase = true };
        video.Protected().Setup<bool>("IsActiveRecording").Returns(false);
        video.Object.Id = Guid.NewGuid();
        video.Object.Path = Path.Combine(_directory, video.Object.Id + ".mkv");
        video.Object.RunTimeTicks = TimeSpan.FromMinutes(1).Ticks;
        File.WriteAllText(video.Object.Path, "media");
        var stream = new MediaStream { Type = MediaStreamType.Video, Width = 320 };
        var source = new MediaSourceInfo { Id = video.Object.Id.ToString("N"), Path = video.Object.Path, MediaStreams = [stream] };
        video.Setup(x => x.GetMediaStreams()).Returns([stream]);
        video.Setup(x => x.GetMediaSources(false)).Returns([source]);
        return video.Object;
    }
}
