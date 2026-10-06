using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.Trickplay;
using MediaBrowser.Model.Configuration;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Trickplay;

public class TrickplayJobLimiterTests
{
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(100, 32)]
    public void ConfigurationClampsUnsafeLimits(int configured, int expected)
    {
        Assert.Equal(1, new TrickplayOptions().MaxConcurrentJobs);
        Assert.Equal(expected, new TrickplayOptions { MaxConcurrentJobs = configured }.MaxConcurrentJobs);
    }

    [Fact]
    public async Task WaitingCancellationDoesNotConsumeCapacityAndLeasesReleaseOnce()
    {
        var limiter = new TrickplayJobLimiter(() => 2);
        using var first = await limiter.LockAsync(CancellationToken.None);
        using var second = await limiter.LockAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelled = limiter.LockAsync(cancellation.Token).AsTask();
        var waiting = limiter.LockAsync(CancellationToken.None).AsTask();
        Assert.False(waiting.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        first.Dispose();
        first.Dispose();
        using var third = await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var cleanup = new CancellationTokenSource();
        var fourth = limiter.LockAsync(cleanup.Token).AsTask();
        Assert.False(fourth.IsCompleted);
        await cleanup.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fourth);
    }

    [Fact]
    public async Task LoweringLimitDrainsExistingJobsAndRaisingItAdmitsNewJobs()
    {
        var options = new TrickplayOptions { MaxConcurrentJobs = 2 };
        var limiter = new TrickplayJobLimiter(() => options.MaxConcurrentJobs);
        using var first = await limiter.LockAsync(CancellationToken.None);
        using var second = await limiter.LockAsync(CancellationToken.None);
        options.MaxConcurrentJobs = 1;
        var waiting = limiter.LockAsync(CancellationToken.None).AsTask();
        first.Dispose();
        Assert.False(waiting.IsCompleted);
        second.Dispose();
        using var third = await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        options.MaxConcurrentJobs = 3;
        using var fourth = await limiter.LockAsync(CancellationToken.None);
        using var fifth = await limiter.LockAsync(CancellationToken.None);
    }
}
