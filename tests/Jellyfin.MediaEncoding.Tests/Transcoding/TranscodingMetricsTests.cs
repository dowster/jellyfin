using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.MediaEncoding.Transcoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Prometheus;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.Transcoding;

public class TranscodingMetricsTests
{
    private const string Labels = "decode_backend=\"qsv\",encode_backend=\"qsv\"";
    private const string Arguments = "-hwaccel qsv -i input.mkv -codec:v:0 h264_qsv output.m3u8";

    [Theory]
    [InlineData(Arguments, true, "qsv", "qsv")]
    [InlineData("-hwaccel vaapi -i input -c:v h264_vaapi output", true, "vaapi", "vaapi")]
    [InlineData("-hwaccel cuda -i input -c:v hevc_nvenc output", true, "cuda", "nvenc")]
    [InlineData("-c:v h264_cuvid -i input -c:v h264_nvenc output", true, "cuda", "nvenc")]
    [InlineData("-i input -c:v h264_qsv output", true, "software", "qsv")]
    [InlineData("-hwaccel qsv -i input -codec:v:0 libx264 output", true, "qsv", "software")]
    [InlineData("-init_hw_device vaapi=va:/dev/dri/renderD128 -i input -codec:v:0 libx264 output", true, "software", "software")]
    [InlineData("-i input -c:v copy output", true, "copy", "copy")]
    [InlineData("-i input -c:a aac output", false, "none", "none")]
    [InlineData("-i \"movie -hwaccel cuda -c:v h264_nvenc.mkv\" -c:v libx264 output", true, "software", "software")]
    [InlineData("-hwaccel future_backend -i input -c:v future_codec output", true, "unknown", "software")]
    public void BackendLabelsDescribeSelectedCommand(string arguments, bool hasVideo, string decode, string encode)
        => Assert.Equal((decode, encode), TranscodingMetrics.GetBackends(arguments, hasVideo));

    [Fact]
    public async Task ProgressUsesWorstBufferAndSpeedAndRetainsCompletedBuffer()
    {
        var registry = Metrics.NewCustomRegistry();
        var metrics = new TranscodingMetrics(registry);
        using var first = CreateJob("first");
        using var second = CreateJob("second");
        metrics.Start(first, Arguments, true, HardwareAccelerationType.qsv);
        metrics.Start(second, Arguments, true, HardwareAccelerationType.qsv);
        metrics.Progress(first, Seconds(120), 100, 4);
        metrics.Progress(second, Seconds(40), 20, 0.8);
        metrics.Playback("first", Seconds(60));
        metrics.Playback("SECOND", Seconds(35));
        var output = await ExportAsync(registry);
        Contains($"jellyfin_transcode_active_jobs{{{Labels}}} 2", output);
        Contains($"jellyfin_transcode_frames_per_second{{{Labels}}} 120", output);
        Contains($"jellyfin_transcode_speed_ratio_min{{{Labels}}} 0.8", output);
        Contains($"jellyfin_transcode_buffer_seconds_min{{{Labels}}} 5", output);
        metrics.Finish(second, false, false);
        metrics.Progress(second, null, null, null);
        metrics.Playback("second", Seconds(39));
        output = await ExportAsync(registry);
        Contains($"jellyfin_transcode_active_jobs{{{Labels}}} 1", output);
        Contains($"jellyfin_transcode_frames_per_second{{{Labels}}} 100", output);
        Contains($"jellyfin_transcode_buffer_seconds_min{{{Labels}}} 1", output);
        metrics.Stop(second);
        output = await ExportAsync(registry);
        Contains($"jellyfin_transcode_buffer_seconds_min{{{Labels}}} 60", output);
        metrics.Stop(first);
        output = await ExportAsync(registry);
        Contains($"jellyfin_transcode_active_jobs{{{Labels}}} 0", output);
        Assert.DoesNotContain($"jellyfin_transcode_buffer_seconds_min{{{Labels}}}", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, false, "start_failed", "start")]
    [InlineData(false, true, false, "failed", "run")]
    [InlineData(false, true, true, "cancelled", null)]
    [InlineData(false, false, false, "completed", null)]
    public async Task OutcomesAreCountedOnceAndIntentionalStopsAreNotFailures(bool startFailed, bool failed, bool cancelled, string outcome, string? phase)
    {
        var registry = Metrics.NewCustomRegistry();
        var metrics = new TranscodingMetrics(registry);
        using var job = CreateJob("session");
        metrics.Start(job, Arguments, true, HardwareAccelerationType.qsv);
        metrics.Finish(job, startFailed, failed, cancelled);
        metrics.Finish(job, startFailed, failed, cancelled);
        metrics.Stop(job);
        var output = await ExportAsync(registry);
        Contains($"jellyfin_transcode_jobs_total{{{Labels},outcome=\"{outcome}\"}} 1", output);
        Contains($"jellyfin_transcode_active_jobs{{{Labels}}} 0", output);
        if (phase is null)
        {
            Assert.DoesNotContain("jellyfin_transcode_failed_total{", output, StringComparison.Ordinal);
        }
        else
        {
            Contains($"jellyfin_transcode_failed_total{{{Labels},phase=\"{phase}\"}} 1", output);
        }
    }

    [Fact]
    public async Task StopBeforeExitDoesNotCountKillAsFailure()
    {
        var registry = Metrics.NewCustomRegistry();
        var metrics = new TranscodingMetrics(registry);
        using var job = CreateJob("session");
        metrics.Start(job, Arguments, true, HardwareAccelerationType.qsv);
        metrics.Stop(job);
        metrics.Finish(job, false, true);
        var output = await ExportAsync(registry);
        Contains($"jellyfin_transcode_jobs_total{{{Labels},outcome=\"cancelled\"}} 1", output);
        Assert.DoesNotContain("jellyfin_transcode_failed_total{", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingProgressIsNotPublishedAsZeroAndBufferClampsAfterSeek()
    {
        var registry = Metrics.NewCustomRegistry();
        var metrics = new TranscodingMetrics(registry);
        using var job = CreateJob("session");
        metrics.Start(job, Arguments, true, HardwareAccelerationType.qsv);
        metrics.Progress(job, null, float.NaN, double.PositiveInfinity);
        var output = await ExportAsync(registry);
        Assert.DoesNotContain($"jellyfin_transcode_buffer_seconds_min{{{Labels}}}", output, StringComparison.Ordinal);
        Assert.DoesNotContain($"jellyfin_transcode_frames_per_second{{{Labels}}}", output, StringComparison.Ordinal);
        Contains($"jellyfin_transcode_progress_jobs{{{Labels},measurement=\"buffer\"}} 0", output);
        metrics.Progress(job, Seconds(100), 0, 0);
        metrics.Playback("session", 0);
        output = await ExportAsync(registry);
        Contains($"jellyfin_transcode_buffer_seconds_min{{{Labels}}} 100", output);
        metrics.Playback("session", Seconds(110));
        output = await ExportAsync(registry);
        Contains($"jellyfin_transcode_buffer_seconds_min{{{Labels}}} 0", output);
    }

    [Fact]
    public async Task SoftwareSelectionCountsStagesButExcludesPassthrough()
    {
        var registry = Metrics.NewCustomRegistry();
        var metrics = new TranscodingMetrics(registry);
        using var software = CreateJob("software");
        using var passthrough = CreateJob("copy");
        metrics.Start(software, "-i input -c:v libx264 output", true, HardwareAccelerationType.qsv);
        metrics.Start(passthrough, "-i input -c:v copy output", true, HardwareAccelerationType.qsv);
        var output = await ExportAsync(registry);
        Contains("jellyfin_transcode_software_selected_total{configured_backend=\"qsv\",stage=\"decode\"} 1", output);
        Contains("jellyfin_transcode_software_selected_total{configured_backend=\"qsv\",stage=\"encode\"} 1", output);
    }

    private static TranscodingJob CreateJob(string session)
        => new(NullLogger<TranscodingJob>.Instance) { PlaySessionId = session };

    private static long Seconds(int seconds) => TimeSpan.FromSeconds(seconds).Ticks;

    private static void Contains(string expected, string output)
        => Assert.Contains(expected, output, StringComparison.Ordinal);

    private static async Task<string> ExportAsync(CollectorRegistry registry)
    {
        using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
