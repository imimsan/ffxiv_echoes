using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Replay.Capture;

/// <summary>
/// 描画決定（<see cref="IMinimapSink"/> 呼出）と source イベントを JSON trace に集積する。
/// </summary>
/// <remarks>
/// <para>
/// 設計書 §3 の trace.json schema に従う。本実装は Phase 1 の最小版で、フィールドは
/// 抽出可能なものだけ書き出す。anchor 解析・innerRadius / fanDeg 等の詳細は後段の T5 で精緻化する。
/// </para>
/// <para>
/// 出力スキーマ：
/// <code>
/// {
///   "meta": {...},
///   "arena": {...},
///   "events": [
///     { "t": 9.123, "kind": "cast_start", ... },
///     { "t": 14.0,  "kind": "draw_aoe",   ... },
///     { "t": 28.5,  "kind": "remove_aoe", ... },
///     ...
///   ]
/// }
/// </code>
/// </para>
/// </remarks>
public sealed class TraceRecorder : IMinimapSink, IDisposable
{
    private readonly object _gate = new();
    private readonly List<TraceEvent> _events = new();
    private IDisposable? _subscription;
    private DateTimeOffset? _combatStart;
    private DateTimeOffset _wallClockStart = DateTimeOffset.UtcNow;
    private int _drawIdCounter;
    private TraceEvent? _lastSourceEvent;

    public string SchemaVersion { get; init; } = "1.0";
    public string HarnessVersion { get; init; } = "0.1.0";
    public string? RecordingPath { get; set; }
    public string? Zone { get; set; }
    public string? Boss { get; set; }

    /// <summary>アリーナ中心・半径・形状。デフォルトは月の底ライクな円 r=20m。</summary>
    public ArenaMeta Arena { get; set; } = new(100.0, 100.0, 20.0, "circle");

    /// <summary>
    /// イベントバスに購読して、すべての source イベントを trace に流す。
    /// </summary>
    public void Subscribe(IEventBus bus)
    {
        _subscription = bus.SubscribeAll(OnEvent);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
    }

    /// <summary>外部から combat start 時刻を明示的にセット（jsonl の combat-relative 時刻計算用）。</summary>
    public void SetCombatStart(DateTimeOffset combatStart)
    {
        lock (_gate)
        {
            _combatStart = combatStart;
        }
    }

    private double TimeOf(DateTimeOffset ts)
    {
        var origin = _combatStart ?? _wallClockStart;
        return (ts - origin).TotalSeconds;
    }

    private void OnEvent(IGameEvent ev)
    {
        lock (_gate)
        {
            switch (ev)
            {
                case CombatStartedEvent c:
                    _combatStart = c.Timestamp;
                    _events.Add(new TraceEvent(0.0, "combat_start"));
                    break;
                case CombatEndedEvent c:
                    _events.Add(new TraceEvent(TimeOf(c.Timestamp), "combat_end")
                    {
                        Extra = new Dictionary<string, object?> { ["reason"] = c.Reason.ToString() },
                    });
                    break;
                case ZoneChangedEvent z:
                    Zone ??= z.ZoneName;
                    _events.Add(new TraceEvent(TimeOf(z.Timestamp), "zone_change")
                    {
                        Extra = new Dictionary<string, object?> { ["zone"] = z.ZoneName },
                    });
                    break;
                case CastStartedEvent c:
                {
                    var te = new TraceEvent(TimeOf(c.Timestamp), "cast_start")
                    {
                        Extra = new Dictionary<string, object?>
                        {
                            ["cast_id"] = $"0x{c.CastActionId:X}",
                            ["cast_name"] = c.CastActionName,
                            ["source"] = c.SourceName,
                            ["source_id"] = c.SourceId,
                            ["cast_time"] = c.CastTime,
                            ["target_id"] = c.TargetId,
                        },
                    };
                    _events.Add(te);
                    _lastSourceEvent = te;
                    break;
                }
                case CastCompletedEvent c:
                {
                    var te = new TraceEvent(TimeOf(c.Timestamp), "cast_complete")
                    {
                        Extra = new Dictionary<string, object?>
                        {
                            ["cast_id"] = $"0x{c.CastActionId:X}",
                            ["cast_name"] = c.CastActionName,
                            ["source"] = c.SourceName,
                            ["source_id"] = c.SourceId,
                        },
                    };
                    _events.Add(te);
                    _lastSourceEvent = te;
                    break;
                }
                case CastCanceledEvent c:
                    _events.Add(new TraceEvent(TimeOf(c.Timestamp), "cast_cancel")
                    {
                        Extra = new Dictionary<string, object?>
                        {
                            ["cast_id"] = $"0x{c.CastActionId:X}",
                            ["cast_name"] = c.CastActionName,
                            ["source"] = c.SourceName,
                        },
                    });
                    break;
                case ActionUsedEvent a:
                {
                    var te = new TraceEvent(TimeOf(a.Timestamp), "action_used")
                    {
                        Extra = new Dictionary<string, object?>
                        {
                            ["action_id"] = $"0x{a.ActionId:X}",
                            ["action_name"] = a.ActionName,
                            ["source"] = a.SourceName,
                            ["source_id"] = a.SourceId,
                            ["target_id"] = a.TargetId,
                            ["auto_attack"] = a.IsAutoAttack,
                        },
                    };
                    _events.Add(te);
                    _lastSourceEvent = te;
                    break;
                }
                case ObjectAppearedEvent o:
                {
                    var te = new TraceEvent(TimeOf(o.Timestamp), "object_appear")
                    {
                        Extra = new Dictionary<string, object?>
                        {
                            ["object_id"] = o.ObjectId,
                            ["object_name"] = o.ObjectName,
                            ["data_id"] = o.DataId,
                            ["x"] = Math.Round(o.Position.X, 3),
                            ["y"] = Math.Round(o.Position.Y, 3),
                            ["z"] = Math.Round(o.Position.Z, 3),
                        },
                    };
                    _events.Add(te);
                    _lastSourceEvent = te;
                    break;
                }
                case ObjectDisappearedEvent o:
                    _events.Add(new TraceEvent(TimeOf(o.Timestamp), "object_disappear")
                    {
                        Extra = new Dictionary<string, object?>
                        {
                            ["object_id"] = o.ObjectId,
                            ["object_name"] = o.ObjectName,
                        },
                    });
                    break;
                case StatusGainedEvent s:
                    _events.Add(new TraceEvent(TimeOf(s.Timestamp), "status_gain")
                    {
                        Extra = new Dictionary<string, object?>
                        {
                            ["status_id"] = s.StatusId,
                            ["status_name"] = s.StatusName,
                            ["target"] = s.TargetName,
                            ["target_id"] = s.TargetId,
                            ["duration"] = s.RemainingTime,
                        },
                    });
                    break;
                case StatusLostEvent s:
                    _events.Add(new TraceEvent(TimeOf(s.Timestamp), "status_lose")
                    {
                        Extra = new Dictionary<string, object?>
                        {
                            ["status_id"] = s.StatusId,
                            ["target"] = s.TargetName,
                            ["target_id"] = s.TargetId,
                        },
                    });
                    break;
                case HpChangedEvent h:
                    _events.Add(new TraceEvent(TimeOf(h.Timestamp), "hp_change")
                    {
                        Extra = new Dictionary<string, object?>
                        {
                            ["actor"] = h.ActorName,
                            ["actor_id"] = h.ActorId,
                            ["hp_pct"] = h.HpPct,
                        },
                    });
                    break;
                // それ以外は無視（player_pos など volume が大きいものは trace のノイズになるため）
            }
        }
    }

    /// <inheritdoc />
    public void AddArenaView(
        string gimmick,
        string? callout,
        double durationSec,
        string? direction,
        double? fanDeg,
        double? arenaRadius,
        Vector3? safeZoneWorld = null,
        float? safeZoneRadius = null,
        float? directionAngleRad = null,
        Vector3? sourceWorld = null,
        IReadOnlyList<StrategyPosition>? strategyPositions = null,
        float? aoeRadius = null,
        float? aoeHalfWidthM = null,
        int? aoeCastType = null,
        uint? aoeOmenId = null,
        IReadOnlyList<Vector3>? multiSourceWorlds = null,
        IReadOnlyList<StrategyObjectMarker>? objectMarkers = null,
        IReadOnlyList<StrategyAoeZone>? aoeZones = null,
        IReadOnlyList<StatusHighlightSpec>? partyStatusHighlights = null,
        string? arenaShape = null,
        double? arenaWidth = null,
        double? arenaDepth = null,
        Vector3? lockedArenaCenter = null,
        uint? autoLuminaCastId = null)
    {
        lock (_gate)
        {
            var id = $"draw_{_drawIdCounter++:D5}";
            var extra = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["gimmick"] = gimmick,
                ["callout"] = callout,
                ["duration_sec"] = durationSec,
                ["direction"] = direction,
                ["fan_deg"] = fanDeg,
                ["arena_radius"] = arenaRadius,
                ["aoe_radius"] = aoeRadius,
                ["aoe_half_width_m"] = aoeHalfWidthM,
                ["aoe_cast_type"] = aoeCastType,
                ["aoe_omen_id"] = aoeOmenId,
                ["auto_lumina_cast_id"] = autoLuminaCastId is { } luminaId ? $"0x{luminaId:X}" : null,
            };
            if (sourceWorld is { } sw)
            {
                extra["source_x"] = Math.Round(sw.X, 3);
                extra["source_y"] = Math.Round(sw.Y, 3);
                extra["source_z"] = Math.Round(sw.Z, 3);
            }
            if (lockedArenaCenter is { } lc)
            {
                extra["arena_center_x"] = Math.Round(lc.X, 3);
                extra["arena_center_z"] = Math.Round(lc.Z, 3);
            }
            if (multiSourceWorlds is { Count: > 0 })
            {
                var positions = new List<object?>(multiSourceWorlds.Count);
                foreach (var p in multiSourceWorlds)
                {
                    positions.Add(new
                    {
                        x = Math.Round(p.X, 3),
                        y = Math.Round(p.Y, 3),
                        z = Math.Round(p.Z, 3),
                    });
                }
                extra["multi_source_worlds"] = positions;
            }
            if (aoeZones is { Count: > 0 })
            {
                extra["aoe_zone_count"] = aoeZones.Count;
            }
            extra["trigger_event"] = SnapshotLastSource();

            _events.Add(new TraceEvent(WallClockNow(), "draw_aoe") { Extra = extra });
        }
    }

    /// <inheritdoc />
    public void AddMultiAoeView(
        string callout,
        IReadOnlyList<Vector3> positions,
        float aoeRadiusM,
        double durationSec,
        double? arenaRadius = null,
        int aoeCastType = 2,
        string? arenaShape = null,
        double? arenaWidth = null,
        double? arenaDepth = null,
        Vector3? lockedArenaCenter = null)
    {
        // 内部実装は AddArenaView へ委譲（MinimapWindow 同様）。
        AddArenaView(
            gimmick: "multi_aoe",
            callout: callout,
            durationSec: durationSec,
            direction: null,
            fanDeg: null,
            arenaRadius: arenaRadius,
            aoeRadius: aoeRadiusM,
            aoeCastType: aoeCastType,
            multiSourceWorlds: positions,
            arenaShape: arenaShape,
            arenaWidth: arenaWidth,
            arenaDepth: arenaDepth,
            lockedArenaCenter: lockedArenaCenter);
    }

    /// <inheritdoc />
    public void SuppressAutoLuminaForCast(uint castId)
    {
        lock (_gate)
        {
            _events.Add(new TraceEvent(WallClockNow(), "suppress_auto_lumina")
            {
                Extra = new Dictionary<string, object?> { ["cast_id"] = $"0x{castId:X}" },
            });
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            _events.Add(new TraceEvent(WallClockNow(), "minimap_clear"));
        }
    }

    /// <summary>
    /// サービスから「描画スキップしました」を自発的にロギングする経路（オプション）。
    /// 既存サービスからは呼ばれないが、ReplayHarness 拡張で「skip 理由を可視化」する際に使う。
    /// </summary>
    public void RecordAoeSkipped(string service, string reason, IDictionary<string, object?>? actionMeta = null)
    {
        lock (_gate)
        {
            var extra = new Dictionary<string, object?>
            {
                ["service"] = service,
                ["reason"] = reason,
            };
            if (actionMeta is not null)
            {
                extra["action"] = actionMeta;
            }
            extra["trigger_event"] = SnapshotLastSource();
            _events.Add(new TraceEvent(WallClockNow(), "aoe_skipped") { Extra = extra });
        }
    }

    private object? SnapshotLastSource()
    {
        if (_lastSourceEvent is null) return null;
        var snap = new Dictionary<string, object?>
        {
            ["t"] = _lastSourceEvent.T,
            ["kind"] = _lastSourceEvent.Kind,
        };
        if (_lastSourceEvent.Extra is { } extra)
        {
            foreach (var kv in extra)
            {
                snap[kv.Key] = kv.Value;
            }
        }
        return snap;
    }

    private double WallClockNow()
    {
        // jsonl 駆動再生中は最後に publish した event の time が最新。
        // 直前 source event の T を再利用するのが「描画決定の発火時刻」として最も自然。
        return _lastSourceEvent?.T ?? TimeOf(DateTimeOffset.UtcNow);
    }

    public void WriteTo(string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var jsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        TraceEvent[] eventsSnapshot;
        lock (_gate)
        {
            eventsSnapshot = _events.ToArray();
        }

        var doc = new Dictionary<string, object?>
        {
            ["meta"] = new Dictionary<string, object?>
            {
                ["schema_version"] = SchemaVersion,
                ["recording_path"] = RecordingPath,
                ["zone"] = Zone,
                ["boss"] = Boss,
                ["trace_generated_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["harness_version"] = HarnessVersion,
            },
            ["arena"] = new Dictionary<string, object?>
            {
                ["center_x"] = Arena.CenterX,
                ["center_z"] = Arena.CenterZ,
                ["radius_m"] = Arena.RadiusM,
                ["shape"] = Arena.Shape,
            },
            ["events"] = SerializeEvents(eventsSnapshot),
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(doc, jsonOpts));
    }

    private static List<Dictionary<string, object?>> SerializeEvents(TraceEvent[] events)
    {
        var list = new List<Dictionary<string, object?>>(events.Length);
        foreach (var e in events)
        {
            var d = new Dictionary<string, object?>
            {
                ["t"] = Math.Round(e.T, 3),
                ["kind"] = e.Kind,
            };
            if (e.Extra is { } extra)
            {
                foreach (var kv in extra)
                {
                    d[kv.Key] = kv.Value;
                }
            }
            list.Add(d);
        }
        return list;
    }

    public int EventCount
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    public int CountKind(string kind)
    {
        lock (_gate)
        {
            var n = 0;
            foreach (var e in _events)
            {
                if (e.Kind == kind) n++;
            }
            return n;
        }
    }

    public sealed record ArenaMeta(double CenterX, double CenterZ, double RadiusM, string Shape);

    private sealed record TraceEvent(double T, string Kind)
    {
        public IDictionary<string, object?>? Extra { get; set; }
    }
}
