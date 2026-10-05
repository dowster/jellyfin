using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Jellyfin.Server.Implementations.Trickplay;
using Prometheus;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Trickplay;

public class TrickplayMetricsTests
{
    [Theory]
    [InlineData(true, false, 1, 0)]
    [InlineData(false, false, 0, 1)]
    [InlineData(false, true, 0, 0)]
    public async Task GenerationTracksOutcomeAndBalancesGauges(bool completed, bool cancelled, int successes, int failures)
    {
        var registry = Metrics.NewCustomRegistry();
        var metrics = new TrickplayMetrics(registry);
        var job = metrics.QueueJob();
        Assert.Contains("jellyfin_trickplay_pending_jobs 1", await ExportAsync(registry), StringComparison.Ordinal);
        job.Acquired();
        job.Start();
        Assert.Contains("jellyfin_trickplay_active_jobs 1", await ExportAsync(registry), StringComparison.Ordinal);
        if (completed)
        {
            job.Complete();
        }

        if (cancelled)
        {
            job.Cancel();
        }

        job.Dispose();
        job.Dispose();
        var output = await ExportAsync(registry);
        Assert.Contains("jellyfin_trickplay_pending_jobs 0", output, StringComparison.Ordinal);
        Assert.Contains("jellyfin_trickplay_active_jobs 0", output, StringComparison.Ordinal);
        Assert.Contains($"jellyfin_trickplay_completed_total {successes}", output, StringComparison.Ordinal);
        Assert.Contains($"jellyfin_trickplay_failed_total {failures}", output, StringComparison.Ordinal);
        Assert.Contains("jellyfin_trickplay_duration_seconds_count 1", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingCancellationAndSkippedItemsDoNotCountAsGeneration(bool acquired)
    {
        var registry = Metrics.NewCustomRegistry();
        var metrics = new TrickplayMetrics(registry);
        using (var job = metrics.QueueJob())
        {
            if (acquired)
            {
                job.Acquired();
            }
        }

        var output = await ExportAsync(registry);
        Assert.Contains("jellyfin_trickplay_pending_jobs 0", output, StringComparison.Ordinal);
        Assert.Contains("jellyfin_trickplay_active_jobs 0", output, StringComparison.Ordinal);
        Assert.Contains("jellyfin_trickplay_completed_total 0", output, StringComparison.Ordinal);
        Assert.Contains("jellyfin_trickplay_failed_total 0", output, StringComparison.Ordinal);
        Assert.Contains("jellyfin_trickplay_duration_seconds_count 0", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledLockWaitPreservesActiveJob()
    {
        var registry = Metrics.NewCustomRegistry();
        var metrics = new TrickplayMetrics(registry);
        using var resourcePool = new AsyncNonKeyedLocker(1);
        using var cancellation = new CancellationTokenSource();
        using var active = metrics.QueueJob();
        using (await resourcePool.LockAsync())
        {
            active.Acquired();
            active.Start();
            var waiting = WaitAsync();
            Assert.Contains("jellyfin_trickplay_pending_jobs 1", await ExportAsync(registry), StringComparison.Ordinal);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            var output = await ExportAsync(registry);
            Assert.Contains("jellyfin_trickplay_pending_jobs 0", output, StringComparison.Ordinal);
            Assert.Contains("jellyfin_trickplay_active_jobs 1", output, StringComparison.Ordinal);
            Assert.Contains("jellyfin_trickplay_failed_total 0", output, StringComparison.Ordinal);
            active.Complete();
        }

        async Task WaitAsync()
        {
            using var queued = metrics.QueueJob();
            using (await resourcePool.LockAsync(cancellation.Token))
            {
                queued.Acquired();
            }
        }
    }

    private static async Task<string> ExportAsync(CollectorRegistry registry)
    {
        using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
