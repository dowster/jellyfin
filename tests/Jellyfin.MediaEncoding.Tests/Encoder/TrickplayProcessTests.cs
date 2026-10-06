using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.MediaEncoding.Encoder;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.Encoder;

public class TrickplayProcessTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Uses a POSIX shell to stand in for FFmpeg processes.", SkipWhen = nameof(IsWindows))]
    public async Task TrickplayProcessesAreNotSerializedByTheSingleThumbnailLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var firstDirectory = Path.Combine(directory, "first");
        var secondDirectory = Path.Combine(directory, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var release = Path.Combine(directory, "release");
        var options = new ServerConfiguration { ParallelImageEncodingLimit = 1, ImageExtractionTimeoutMs = 30000 };
        var config = Mock.Of<IServerConfigurationManager>(x => x.Configuration == options);
        var files = new Mock<IFileSystem>();
        files.Setup(x => x.GetFilePaths(It.IsAny<string>(), It.IsAny<bool>())).Returns(["started"]);
        using var encoder = new MediaEncoder(
            Mock.Of<ILogger<MediaEncoder>>(),
            config,
            files.Object,
            Mock.Of<IBlurayExaminer>(),
            Mock.Of<ILocalizationManager>(),
            new ConfigurationBuilder().Build(),
            config);
        using var first = CreateProcess(firstDirectory, release);
        using var second = CreateProcess(secondDirectory, release);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var firstJob = encoder.RunTrickplayProcessAsync(first, firstDirectory, null, cancellation.Token);
        var secondJob = encoder.RunTrickplayProcessAsync(second, secondDirectory, null, cancellation.Token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(firstDirectory, "started")) || !File.Exists(Path.Combine(secondDirectory, "started")))
            {
                await Task.Delay(20, timeout.Token);
            }

            Assert.False(firstJob.IsCompleted);
            Assert.False(secondJob.IsCompleted);
        }
        finally
        {
            await File.WriteAllTextAsync(release, "release", CancellationToken.None);
            try
            {
                await Task.WhenAll(firstJob, secondJob).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            finally
            {
                await cancellation.CancelAsync();
                Directory.Delete(directory, true);
            }
        }
    }

    private static Process CreateProcess(string directory, string release)
    {
        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("touch \"$1/started\"; while [ ! -f \"$2\" ]; do sleep 0.02; done");
        info.ArgumentList.Add("trickplay-test");
        info.ArgumentList.Add(directory);
        info.ArgumentList.Add(release);
        return new Process { StartInfo = info, EnableRaisingEvents = true };
    }
}
