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

                case StatusGainedEvent x:
                    writer.WriteString("type", "status_gain");
                    if (x.SourceId != 0)
                    {
                        writer.WriteNumber("source_id", x.SourceId);
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
