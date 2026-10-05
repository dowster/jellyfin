using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.Transcoding;

public class JobLoggerTests
{
    [Theory]
    [InlineData("frame= 10 fps=  30.0 time=00:00:10.00 speed= 2.5x", 2.5)]
    [InlineData("frame=10 fps=30.0 time=00:00:10.00 speed=0.75x", 0.75)]
    [InlineData("frame=10 fps=30.0 time=00:00:10.00 speed=N/A", null)]
    [InlineData("frame=10 fps=30.0 time=00:00:10.00 speed=NaNx", null)]
    [InlineData("frame=10 fps=30.0 time=00:00:10.00 speed=-1x", null)]
    public async Task ReadsSpeedAndAbsolutePositionAfterSeek(string line, double? speed)
    {
        var state = new Mock<EncodingJobInfo>(TranscodingJobType.Hls) { CallBase = true };
        state.Object.BaseRequest = new BaseEncodingJobOptions { StartTimeTicks = TimeSpan.FromSeconds(60).Ticks };
        state.Object.RunTimeTicks = TimeSpan.FromSeconds(120).Ticks;
        state.Setup(s => s.ReportTranscodingProgress(It.IsAny<TimeSpan?>(), It.IsAny<float?>(), It.IsAny<double?>(), It.IsAny<long?>(), It.IsAny<int?>()));
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(line + "\n"));
        using var reader = new StreamReader(source);
        using var target = new MemoryStream();
        await new JobLogger(NullLogger.Instance).StartStreamingLog(state.Object, reader, target);
        Assert.Equal(speed, state.Object.TranscodingSpeed);
        state.Verify(s => s.ReportTranscodingProgress(TimeSpan.FromSeconds(70), 30f, It.IsAny<double?>(), null, null), Times.Once);
    }
}
