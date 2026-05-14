using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using FfxivEchoes.Events;

namespace FfxivEchoes.Recording;

/// <summary>
/// <see cref="IGameEvent"/> を SPEC.md §3.1 の JSON Lines 形式に変換する。
/// </summary>
public static class EventSerializer
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
    };

    public static string SerializeMeta(
        string zone,
        DateTimeOffset startTime,
        string pluginVersion,
        IReadOnlyList<PartyMemberInfo> party)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("meta", true);
            writer.WriteString("zone", zone);
            writer.WriteString("start_time", startTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            writer.WriteString("plugin_version", pluginVersion);

            writer.WriteStartArray("party");
            foreach (var member in party)
            {
                writer.WriteStartObject();
                writer.WriteString("name", member.Name);
                writer.WriteString("job", member.Job);
                writer.WriteString("role", member.Role);
                if (member.ObjectId is { } objectId)
                {
                    writer.WriteNumber("object_id", objectId);
                }
                if (member.IsSelf)
                {
                    writer.WriteBoolean("is_self", true);
                }
                if (!string.IsNullOrEmpty(member.SubRole))
                {
                    writer.WriteString("subrole", member.SubRole);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string Serialize(IGameEvent ev, DateTimeOffset combatStart)
        => Serialize(ev, combatStart, partyIdToName: null);

    /// <summary>
    /// party id → name 解決マップを受け取って serialize する。status events に source 名を
    /// 埋め込んで、aggregation 段階での party_related フィルタ（名前マッチ）を強化する。
    /// </summary>
    public static string Serialize(
        IGameEvent ev,
        DateTimeOffset combatStart,
        System.Collections.Generic.IReadOnlyDictionary<uint, string>? partyIdToName)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, WriterOptions))
        {
            var time = (ev.Timestamp - combatStart).TotalSeconds;
            writer.WriteStartObject();
            writer.WriteNumber("time", Math.Round(time, 3));

            switch (ev)
            {
                case CombatStartedEvent:
                    writer.WriteString("type", "combat_start");
                    break;

                case CombatEndedEvent x:
                    writer.WriteString("type", "combat_end");
                    writer.WriteString("result", ResultString(x.Reason));
                    break;

                case ZoneChangedEvent x:
                    writer.WriteString("type", "zone_change");
                    writer.WriteNumber("territory_id", x.TerritoryId);
                    writer.WriteString("zone", x.ZoneName);
                    break;

                case CastStartedEvent x:
                    writer.WriteString("type", "cast_start");
                    writer.WriteString("source", x.SourceName);
                    writer.WriteNumber("source_id", x.SourceId);
                    writer.WriteString("cast_id", FormatHexId(x.CastActionId));
                    writer.WriteString("cast_name", x.CastActionName);
                    if (x.TargetId.HasValue)
                    {
                        writer.WriteNumber("target_id", x.TargetId.Value);
                    }
                    else
                    {
                        writer.WriteNull("target");
                    }
                    if (x.TargetWorld is { } tw)
                    {
                        writer.WriteNumber("target_x", Math.Round(tw.X, 3));
                        writer.WriteNumber("target_y", Math.Round(tw.Y, 3));
                        writer.WriteNumber("target_z", Math.Round(tw.Z, 3));
                    }
                    writer.WriteNumber("cast_time", Math.Round(x.CastTime, 2));
                    break;

                case CastCompletedEvent x:
                    writer.WriteString("type", "cast_complete");
                    writer.WriteString("source", x.SourceName);
                    writer.WriteNumber("source_id", x.SourceId);
                    writer.WriteString("cast_id", FormatHexId(x.CastActionId));
                    writer.WriteString("cast_name", x.CastActionName);
                    break;

                case CastCanceledEvent x:
                    writer.WriteString("type", "cast_cancel");
                    writer.WriteString("source", x.SourceName);
                    writer.WriteNumber("source_id", x.SourceId);
                    writer.WriteString("cast_id", FormatHexId(x.CastActionId));
                    writer.WriteString("cast_name", x.CastActionName);
                    break;

                case ActionUsedEvent x:
                    writer.WriteString("type", "action_used");
                    writer.WriteString("source", x.SourceName);
                    writer.WriteNumber("source_id", x.SourceId);
                    writer.WriteString("action_id", FormatHexId(x.ActionId));
                    writer.WriteString("action_name", x.ActionName);
                    writer.WriteBoolean("auto_attack", x.IsAutoAttack);
                    if (x.TargetId.HasValue)
                    {
                        writer.WriteNumber("target_id", x.TargetId.Value);
                    }
                    else
                    {
                        writer.WriteNull("target");
                    }
                    if (x.TargetWorld is { } aw)
                    {
                        writer.WriteNumber("target_x", Math.Round(aw.X, 3));
                        writer.WriteNumber("target_y", Math.Round(aw.Y, 3));
                        writer.WriteNumber("target_z", Math.Round(aw.Z, 3));
                    }
                    break;

                case StatusGainedEvent x:
                    writer.WriteString("type", "status_gain");
                    if (x.SourceId != 0)
                    {
                        writer.WriteNumber("source_id", x.SourceId);
                        // party メタの id → name で source 名を解決して書く。
                        // これにより aggregation の名前マッチが PC 自己バフ・PC DoT を確実に拾える。
                        // 自己付与（source==target）の場合は target 名を使う（lookup 不要）。
                        if (x.SourceId == x.TargetId && !string.IsNullOrEmpty(x.TargetName))
                        {
                            writer.WriteString("source", x.TargetName);
                        }
                        else if (partyIdToName is { } map && map.TryGetValue(x.SourceId, out var srcName) &&
                                 !string.IsNullOrEmpty(srcName))
                        {
                            writer.WriteString("source", srcName);
                        }
                    }
                    writer.WriteString("target", x.TargetName);
                    writer.WriteNumber("target_id", x.TargetId);
                    writer.WriteNumber("status_id", x.StatusId);
                    writer.WriteString("status_name", x.StatusName);
                    writer.WriteNumber("duration", Math.Round(x.RemainingTime, 2));
                    writer.WriteNumber("stacks", x.Stacks);
                    break;

                case StatusLostEvent x:
                    writer.WriteString("type", "status_lose");
                    writer.WriteString("target", x.TargetName);
                    writer.WriteNumber("target_id", x.TargetId);
                    writer.WriteNumber("status_id", x.StatusId);
                    break;

                case StatusUpdatedEvent x:
                    writer.WriteString("type", "status_update");
                    writer.WriteNumber("target_id", x.TargetId);
                    writer.WriteNumber("status_id", x.StatusId);
                    writer.WriteNumber("stacks", x.Stacks);
                    writer.WriteNumber("remaining", Math.Round(x.RemainingTime, 2));
                    break;

                case HpChangedEvent x:
                    writer.WriteString("type", "hp_change");
                    writer.WriteString("actor", x.ActorName);
                    writer.WriteNumber("actor_id", x.ActorId);
                    writer.WriteNumber("hp_pct", Math.Round(x.HpPct, 2));
                    writer.WriteNumber("hp", x.CurrentHp);
                    writer.WriteNumber("hp_max", x.MaxHp);
                    break;

                case ObjectAppearedEvent x:
                    writer.WriteString("type", "object_appear");
                    writer.WriteString("object_name", x.ObjectName);
                    writer.WriteNumber("object_id", x.ObjectId);
                    if (x.EntityId is { } entityId && entityId != 0)
                    {
                        writer.WriteNumber("entity_id", entityId);
                    }
                    writer.WriteNumber("data_id", x.DataId);
                    writer.WriteStartObject("position");
                    writer.WriteNumber("x", Math.Round(x.Position.X, 3));
                    writer.WriteNumber("y", Math.Round(x.Position.Y, 3));
                    writer.WriteNumber("z", Math.Round(x.Position.Z, 3));
                    writer.WriteEndObject();
                    break;

                case ObjectDisappearedEvent x:
                    writer.WriteString("type", "object_disappear");
                    writer.WriteString("object_name", x.ObjectName);
                    writer.WriteNumber("object_id", x.ObjectId);
                    break;

                case LocalPlayerPositionEvent x:
                    writer.WriteString("type", "player_pos");
                    writer.WriteStartObject("position");
                    writer.WriteNumber("x", Math.Round(x.Position.X, 3));
                    writer.WriteNumber("y", Math.Round(x.Position.Y, 3));
                    writer.WriteNumber("z", Math.Round(x.Position.Z, 3));
                    writer.WriteEndObject();
                    break;

                default:
                    writer.WriteString("type", "unknown");
                    writer.WriteString("event_class", ev.GetType().Name);
                    break;
            }

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string FormatHexId(uint id) => $"0x{id:X}";

    private static string ResultString(CombatEndReason reason) => reason switch
    {
        CombatEndReason.Cleared => "clear",
        CombatEndReason.Wiped => "wipe",
        CombatEndReason.Left => "left",
        _ => "unknown",
    };
}
