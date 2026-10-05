using System;
using System.Diagnostics;
using Prometheus;

namespace Jellyfin.Server.Implementations.Trickplay;

/// <summary>
/// Process-wide metrics for trickplay generation, without per-item labels.
/// </summary>
internal sealed class TrickplayMetrics
{
    private readonly Gauge _pending;
    private readonly Gauge _active;
    private readonly Counter _completed;
    private readonly Counter _failed;
    private readonly Histogram _duration;

    public TrickplayMetrics(CollectorRegistry? registry = null)
    {
        var factory = Metrics.WithCustomRegistry(registry ?? Metrics.DefaultRegistry);
        _pending = factory.CreateGauge("jellyfin_trickplay_pending_jobs", "Trickplay requests waiting for the generation lock; excludes unvisited library items.");
        _active = factory.CreateGauge("jellyfin_trickplay_active_jobs", "Trickplay item-resolution generation attempts currently running.");
        _completed = factory.CreateCounter("jellyfin_trickplay_completed_total", "Successfully generated and saved trickplay item-resolution attempts.");
        _failed = factory.CreateCounter("jellyfin_trickplay_failed_total", "Failed trickplay item-resolution generation attempts; excludes cancellation.");
        _duration = factory.CreateHistogram("jellyfin_trickplay_duration_seconds", "Duration of trickplay item-resolution generation attempts, excluding lock wait and including failures and cancellation.", new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(1, 2, 15)
        });
    }

    public Job QueueJob() => new(this);

    internal sealed class Job : IDisposable
    {
        private readonly TrickplayMetrics _metrics;
        private bool _pending = true;
        private bool _started;
        private bool _completed;
        private bool _cancelled;
        private bool _disposed;
        private long _start;

        public Job(TrickplayMetrics metrics)
        {
            _metrics = metrics;
            _metrics._pending.Inc();
        }

        public void Acquired()
        {
            _metrics._pending.Dec();
            _pending = false;
        }

        public void Start()
        {
            _start = Stopwatch.GetTimestamp();
            _started = true;
            _metrics._active.Inc();
        }

        public void Complete() => _completed = true;

        public void Cancel() => _cancelled = true;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_pending)
            {
                _metrics._pending.Dec();
            }

            if (!_started)
            {
                return;
            }

            _metrics._active.Dec();
            _metrics._duration.Observe(Stopwatch.GetElapsedTime(_start).TotalSeconds);
            if (_completed)
            {
                _metrics._completed.Inc();
            }
            else if (!_cancelled)
            {
                _metrics._failed.Inc();
            }
        }
    }
}
