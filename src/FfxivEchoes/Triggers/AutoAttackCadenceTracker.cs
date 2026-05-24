using System;
using System.Collections.Generic;

namespace FfxivEchoes.Triggers;

public sealed class AutoAttackCadenceTracker
{
    private const double MinReasonableIntervalSeconds = 0.8;
    private const double MaxReasonableIntervalSeconds = 8.0;
    private const double ExpiredPredictionGraceSeconds = 0.25;
    private const double NewSampleWeight = 0.35;

    private readonly Dictionary<AutoAttackKey, State> _states = new();

    public AutoAttackPrediction? Record(AutoAttackSample sample)
    {
        var key = new AutoAttackKey(sample.SourceId);
        if (!_states.TryGetValue(key, out var state))
        {
            _states[key] = new State
            {
                LastAt = sample.Timestamp,
                TargetId = sample.TargetId,
                SampleCount = 1,
            };
            return null;
        }

        var rawInterval = (sample.Timestamp - state.LastAt).TotalSeconds;
        if (rawInterval < MinReasonableIntervalSeconds)
        {
            state.TargetId = sample.TargetId ?? state.TargetId;
            return CurrentPrediction(sample.SourceId, state);
        }

        state.LastAt = sample.Timestamp;
        state.TargetId = sample.TargetId;
        if (rawInterval > MaxReasonableIntervalSeconds)
        {
            state.IntervalSeconds = null;
            state.NextAt = null;
            state.WarnedForNextAt = null;
            state.SampleCount = 1;
            return null;
        }

        state.IntervalSeconds = state.IntervalSeconds is { } previous
            ? previous * (1.0 - NewSampleWeight) + rawInterval * NewSampleWeight
            : rawInterval;
        state.SampleCount++;
        state.NextAt = sample.Timestamp.AddSeconds(state.IntervalSeconds.Value);
        state.WarnedForNextAt = null;

        return CurrentPrediction(sample.SourceId, state);
    }

    public IReadOnlyList<AutoAttackWarning> GetDueWarnings(DateTimeOffset now, double warnBeforeSeconds)
    {
        var warnings = new List<AutoAttackWarning>();
        var threshold = Math.Max(0, warnBeforeSeconds);

        foreach (var (key, state) in _states)
        {
            if (state.SampleCount < 2 || state.IntervalSeconds is null || state.NextAt is null)
            {
                continue;
            }

            var nextAt = state.NextAt.Value;
            if (state.WarnedForNextAt == nextAt)
            {
                continue;
            }

            var remaining = (nextAt - now).TotalSeconds;
            if (remaining <= threshold && remaining >= -ExpiredPredictionGraceSeconds)
            {
                state.WarnedForNextAt = nextAt;
                warnings.Add(new AutoAttackWarning(
                    key.SourceId,
                    state.TargetId,
                    nextAt,
                    Math.Max(0, remaining),
                    state.IntervalSeconds.Value,
                    ConfidenceFor(state.SampleCount)));
            }
        }

        return warnings;
    }

    public void Clear() => _states.Clear();

    private static AutoAttackPrediction? CurrentPrediction(uint sourceId, State state)
    {
        if (state.SampleCount < 2 || state.IntervalSeconds is null || state.NextAt is null)
        {
            return null;
        }

        return new AutoAttackPrediction(
            sourceId,
            state.TargetId,
            state.IntervalSeconds.Value,
            state.NextAt.Value,
            ConfidenceFor(state.SampleCount));
    }

    private static double ConfidenceFor(int sampleCount)
    {
        return Math.Clamp((sampleCount - 1) / 3.0, 0.0, 1.0);
    }

    private readonly record struct AutoAttackKey(uint SourceId);

    private sealed class State
    {
        public DateTimeOffset LastAt { get; set; }
        public uint? TargetId { get; set; }
        public int SampleCount { get; set; }
        public double? IntervalSeconds { get; set; }
        public DateTimeOffset? NextAt { get; set; }
        public DateTimeOffset? WarnedForNextAt { get; set; }
    }
}

public readonly record struct AutoAttackSample(uint SourceId, uint? TargetId, DateTimeOffset Timestamp);

public sealed record AutoAttackPrediction(
    uint SourceId,
    uint? TargetId,
    double IntervalSeconds,
    DateTimeOffset NextAt,
    double Confidence);

public sealed record AutoAttackWarning(
    uint SourceId,
    uint? TargetId,
    DateTimeOffset ExpectedAt,
    double RemainingSeconds,
    double IntervalSeconds,
    double Confidence);
