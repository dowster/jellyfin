using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Prometheus;

namespace MediaBrowser.MediaEncoding.Transcoding;

/// <summary>
/// Aggregate transcode metrics with bounded backend labels and no session identifiers.
/// </summary>
internal sealed partial class TranscodingMetrics
{
    private readonly Lock _lock = new();
    private readonly Dictionary<TranscodingJob, JobInfo> _jobs = new();
    private readonly HashSet<(string Decode, string Encode)> _backends = new();
    private readonly Gauge _active;
    private readonly Gauge _throttled;
    private readonly Gauge _fps;
    private readonly Gauge _speed;
    private readonly Gauge _buffer;
    private readonly Gauge _samples;
    private readonly Counter _outcomes;
    private readonly Counter _failures;
    private readonly Counter _software;

    public TranscodingMetrics(CollectorRegistry? registry = null)
    {
        registry ??= Metrics.DefaultRegistry;
        var factory = Metrics.WithCustomRegistry(registry);
        string[] labels = ["decode_backend", "encode_backend"];
        _active = factory.CreateGauge("jellyfin_transcode_active_jobs", "Running FFmpeg jobs by selected command-line backends, including remux and audio-only jobs.", labels);
        _throttled = factory.CreateGauge("jellyfin_transcode_throttled_jobs", "Running FFmpeg jobs currently paused by the transcoding throttler.", labels);
        _fps = factory.CreateGauge("jellyfin_transcode_frames_per_second", "Sum of latest reported FFmpeg FPS across running, unthrottled jobs.", labels);
        _speed = factory.CreateGauge("jellyfin_transcode_speed_ratio_min", "Minimum latest FFmpeg speed relative to real time across running, unthrottled jobs.", labels);
        _buffer = factory.CreateGauge("jellyfin_transcode_buffer_seconds_min", "Minimum generated media seconds ahead of the last client-reported playback position; includes completed jobs until playback cleanup.", labels);
        _samples = factory.CreateGauge("jellyfin_transcode_progress_jobs", "Number of jobs with a valid latest progress sample for each measurement.", ["decode_backend", "encode_backend", "measurement"]);
        _outcomes = factory.CreateCounter("jellyfin_transcode_jobs_total", "FFmpeg jobs ending by outcome; intentional stops count as cancelled.", ["decode_backend", "encode_backend", "outcome"]);
        _failures = factory.CreateCounter("jellyfin_transcode_failed_total", "Unexpected FFmpeg failures, excluding intentional stops.", ["decode_backend", "encode_backend", "phase"]);
        _software = factory.CreateCounter("jellyfin_transcode_software_selected_total", "Jobs selecting software for a stage while hardware acceleration is configured; does not detect driver fallback.", ["configured_backend", "stage"]);
        registry.AddBeforeCollectCallback(Collect);
    }

    public void Start(TranscodingJob job, string arguments, bool hasVideo, HardwareAccelerationType configuredBackend)
    {
        var (decode, encode) = GetBackends(arguments, hasVideo);
        lock (_lock)
        {
            _jobs.Add(job, new JobInfo(job, decode, encode));
            _backends.Add((decode, encode));
            if (hasVideo && encode != "copy" && configuredBackend != HardwareAccelerationType.none)
            {
                if (decode == "software")
                {
                    _software.WithLabels(configuredBackend.ToString(), "decode").Inc();
                }

                if (encode == "software")
                {
                    _software.WithLabels(configuredBackend.ToString(), "encode").Inc();
                }
            }
        }
    }

    public void Progress(TranscodingJob job, long? positionTicks, float? fps, double? speed)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(job, out var info) && !info.Finished)
            {
                info.PositionTicks = positionTicks ?? info.PositionTicks;
                if (fps.HasValue && float.IsFinite(fps.Value) && fps.Value >= 0)
                {
                    info.Fps = fps;
                }

                if (speed.HasValue && double.IsFinite(speed.Value) && speed.Value >= 0)
                {
                    info.Speed = speed;
                }
            }
        }
    }

    public void Playback(string playSessionId, long? positionTicks)
    {
        if (!positionTicks.HasValue || positionTicks.Value < 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var info in _jobs.Values.Where(i => string.Equals(i.Job.PlaySessionId, playSessionId, StringComparison.OrdinalIgnoreCase)))
            {
                info.PlaybackTicks = positionTicks;
            }
        }
    }

    public void Finish(TranscodingJob job, bool startFailed, bool failed, bool cancelled = false)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(job, out var info) || info.Finished)
            {
                return;
            }

            info.Finished = true;
            var outcome = cancelled ? "cancelled" : startFailed ? "start_failed" : failed ? "failed" : "completed";
            _outcomes.WithLabels(info.Decode, info.Encode, outcome).Inc();
            if (!cancelled && (startFailed || failed))
            {
                _failures.WithLabels(info.Decode, info.Encode, startFailed ? "start" : "run").Inc();
            }

            if (cancelled || startFailed || failed)
            {
                _jobs.Remove(job);
            }
        }
    }

    public void Stop(TranscodingJob job)
    {
        lock (_lock)
        {
            Finish(job, false, false, true);
            _jobs.Remove(job);
        }
    }

    internal static (string Decode, string Encode) GetBackends(string arguments, bool hasVideo)
    {
        if (!hasVideo)
        {
            return ("none", "none");
        }

        var decode = "software";
        var encode = "unknown";
        var input = true;
        var tokens = ArgumentRegex().Matches(arguments).Select(m => m.Value).ToArray();
        for (var i = 0; i + 1 < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token == "-i")
            {
                input = false;
                i++;
            }
            else if (token is "-hwaccel" or "-c:v" or "-codec:v" or "-codec:v:0" or "-c:v:0")
            {
                var value = tokens[++i].Trim('"');
                if (input)
                {
                    var backend = token == "-hwaccel" ? NormalizeBackend(value) : EncoderBackend(value);
                    if (backend != "software")
                    {
                        decode = backend;
                    }
                }
                else if (token != "-hwaccel")
                {
                    encode = EncoderBackend(value);
                }
            }
        }

        return encode == "copy" ? ("copy", "copy") : (decode, encode);
    }

    private static string NormalizeBackend(string value) => value switch
    {
        "cuda" => "cuda",
        "qsv" => "qsv",
        "vaapi" => "vaapi",
        "d3d11va" => "d3d11va",
        "dxva2" => "dxva2",
        "videotoolbox" => "videotoolbox",
        "rkmpp" => "rkmpp",
        "v4l2m2m" => "v4l2m2m",
        _ => "unknown"
    };

    private static string EncoderBackend(string value)
    {
        if (value == "copy")
        {
            return "copy";
        }

        var suffix = value[(value.LastIndexOf('_') + 1)..];
        return suffix switch
        {
            "nvenc" => "nvenc",
            "cuvid" => "cuda",
            "amf" => "amf",
            "qsv" or "vaapi" or "videotoolbox" or "rkmpp" or "v4l2m2m" => suffix,
            _ => "software"
        };
    }

    private void Collect()
    {
        lock (_lock)
        {
            foreach (var (decode, encode) in _backends)
            {
                var jobs = _jobs.Values.Where(i => i.Decode == decode && i.Encode == encode).ToArray();
                var running = jobs.Where(i => !i.Finished).ToArray();
                var progressing = running.Where(i => i.Job.TranscodingThrottler?.IsPaused != true).ToArray();
                _active.WithLabels(decode, encode).Set(running.Length);
                _throttled.WithLabels(decode, encode).Set(running.Length - progressing.Length);
                SetProgress(_fps, "fps", progressing.Where(i => i.Fps.HasValue).Select(i => (double)i.Fps!.Value).ToArray(), decode, encode, false);
                SetProgress(_speed, "speed", progressing.Where(i => i.Speed.HasValue).Select(i => i.Speed!.Value).ToArray(), decode, encode, true);
                var buffers = jobs.Where(i => i.PositionTicks.HasValue && i.PlaybackTicks.HasValue)
                    .Select(i => Math.Max(0, (i.PositionTicks!.Value - i.PlaybackTicks!.Value) / (double)TimeSpan.TicksPerSecond)).ToArray();
                SetProgress(_buffer, "buffer", buffers, decode, encode, true);
            }
        }
    }

    private void SetProgress(Gauge gauge, string measurement, double[] values, string decode, string encode, bool minimum)
    {
        _samples.WithLabels(decode, encode, measurement).Set(values.Length);
        var child = gauge.WithLabels(decode, encode);
        if (values.Length == 0)
        {
            child.Unpublish();
        }
        else
        {
            child.Set(minimum ? values.Min() : values.Sum());
            child.Publish();
        }
    }

    [GeneratedRegex("\"(?:\\\\.|[^\"\\\\])*\"|[^\\s\"]+")]
    private static partial Regex ArgumentRegex();

    private sealed class JobInfo(TranscodingJob job, string decode, string encode)
    {
        public TranscodingJob Job { get; } = job;

        public string Decode { get; } = decode;

        public string Encode { get; } = encode;

        public bool Finished { get; set; }

        public long? PositionTicks { get; set; }

        public long? PlaybackTicks { get; set; }

        public float? Fps { get; set; }

        public double? Speed { get; set; }
    }
}
