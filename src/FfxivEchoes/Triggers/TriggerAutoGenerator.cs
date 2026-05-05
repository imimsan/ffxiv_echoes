using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers.Models;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace FfxivEchoes.Triggers;

/// <summary>
/// 録画 aggregate と Lumina Action データから、AoE 形状を自動判定して
/// トリガー定義を一括生成する。
/// </summary>
/// <remarks>
/// 各 cast_start に対して以下を生成：
/// - TTS でキャスト名を読み上げ
/// - 推定された AoE 形状に応じた arena_view または field_marker
/// PT 内のキャスト（プレイヤー由来）は生成対象外。
/// 既に同じ cast_id を扱うトリガーがあるものはスキップ。
/// </remarks>
public sealed class TriggerAutoGenerator
{
    private readonly IDataManager _dataManager;
    private readonly IPluginLog _log;

    public TriggerAutoGenerator(IDataManager dataManager, IPluginLog log)
    {
        _dataManager = dataManager;
        _log = log;
    }

    /// <summary>
    /// 録画 aggregate からトリガーを自動生成する。
    /// 既存トリガー（existing）と重複する cast_id はスキップ。
    /// </summary>
    public GenerationResult Generate(AggregatedEvents agg, IReadOnlyList<TriggerDefinition> existing,
        IReadOnlyList<string>? partyMembers = null)
    {
        var existingCastIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trig in existing)
        {
            if (trig.Match?.CastId is { Length: > 0 } cid)
            {
                existingCastIds.Add(cid);
            }
        }
        var partySet = partyMembers is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(partyMembers, StringComparer.OrdinalIgnoreCase);

        var generated = new List<TriggerDefinition>();
        var skipped = new List<string>();

        foreach (var ev in agg.Events)
        {
            if (ev.Key.Type != "cast_start") continue;
            if (string.IsNullOrEmpty(ev.Key.Id)) continue;

            // PT メンバー（自分含む）由来のキャストはスキップ
            if (!string.IsNullOrEmpty(ev.Key.Source) && partySet.Contains(ev.Key.Source))
            {
                continue;
            }

            // 既存トリガーと重複ならスキップ
            if (existingCastIds.Contains(ev.Key.Id))
            {
                skipped.Add($"{ev.Key.Name ?? ev.Key.Id}（既存）");
                continue;
            }

            var trigger = BuildTrigger(ev);
            if (trigger is not null)
            {
                generated.Add(trigger);
            }
        }

        return new GenerationResult(generated, skipped);
    }

    private TriggerDefinition? BuildTrigger(AggregatedEvent ev)
    {
        if (!TryParseCastId(ev.Key.Id!, out var actionId))
        {
            return null;
        }

        var castName = ev.Key.Name ?? ev.Key.Id ?? "?";
        var trigger = new TriggerDefinition
        {
            Id = $"auto_cast_{actionId:x}",
            Name = $"{castName}（自動生成）",
            Enabled = true,
            Type = "cast_start",
            Match = new MatchCondition
            {
                CastId = ev.Key.Id,
                CastName = ev.Key.Name,
                Source = ev.Key.Source,
            },
        };

        // 必ず TTS を入れる
        trigger.Actions.Add(new ActionDefinition { Type = "tts", Text = castName });

        // Lumina から AoE 情報取得
        var aoe = ResolveAoeFromLumina(actionId);
        if (aoe.HasValue)
        {
            var (radius, castType, fromCaster) = aoe.Value;

            // CastType に応じてビジュアルを追加
            switch (castType)
            {
                case 2: // Circle (target-centered)
                case 5: // PB on caster
                    trigger.Actions.Add(new ActionDefinition
                    {
                        Type = "field_marker",
                        Shape = "circle",
                        Radius = radius,
                        Duration = 5.0,
                        Color = "#FF6464",
                        SafeZone = new SafeZoneCalculation
                        {
                            Method = fromCaster ? "boss_relative" : "boss_relative",
                        },
                    });
                    // 大きい AoE なら「外周回避」とみなして arena_view も追加
                    if (radius >= 25f)
                    {
                        trigger.Actions.Add(new ActionDefinition
                        {
                            Type = "arena_view",
                            Gimmick = "outer_ring",
                            Callout = $"中央安置：{castName}",
                            Duration = 5.0,
                            ArenaRadius = 20.0,
                        });
                    }
                    break;
                case 6: // Donut
                    trigger.Actions.Add(new ActionDefinition
                    {
                        Type = "arena_view",
                        Gimmick = "inner_circle",
                        Callout = $"外周安置：{castName}",
                        Duration = 5.0,
                        ArenaRadius = 20.0,
                    });
                    break;
                case 3: // Cone
                    trigger.Actions.Add(new ActionDefinition
                    {
                        Type = "arena_view",
                        Gimmick = "cone",
                        Direction = "N",
                        FanDeg = 90,
                        Callout = $"扇形回避：{castName}",
                        Duration = 5.0,
                        ArenaRadius = 20.0,
                    });
                    break;
                case 4: // Line
                    trigger.Actions.Add(new ActionDefinition
                    {
                        Type = "arena_view",
                        Gimmick = "cone",
                        Direction = "N",
                        FanDeg = 30,
                        Callout = $"直線回避：{castName}",
                        Duration = 5.0,
                        ArenaRadius = 20.0,
                    });
                    break;
                default:
                    // 形状不明：TTS のみ（auto-visual で中央テキスト付くので最低限見える）
                    break;
            }
        }

        return trigger;
    }

    /// <summary>
    /// Lumina Action から (EffectRange, CastType, fromCaster) を取得。
    /// EffectRange=0 や AoE タイプでなければ null。
    /// </summary>
    private (float Radius, int CastType, bool FromCaster)? ResolveAoeFromLumina(uint actionId)
    {
        try
        {
            var sheet = _dataManager.GetExcelSheet<LuminaAction>();
            if (!sheet.TryGetRow(actionId, out var row))
            {
                return null;
            }
            var effectRange = (float)row.EffectRange;
            if (effectRange <= 0) return null;
            var castType = (int)row.CastType;
            // 5=PB / 3=cone / 4=line はキャスター中心
            var fromCaster = castType == 5 || castType == 3 || castType == 4 || castType == 6;
            return (effectRange, castType, fromCaster);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] AutoGenerator: Lumina lookup 失敗 (id={Id})", actionId);
            return null;
        }
    }

    private static bool TryParseCastId(string spec, out uint id)
    {
        id = 0;
        if (string.IsNullOrEmpty(spec)) return false;
        var s = spec;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.StartsWith("#")) s = s[1..];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out id);
    }

    public sealed record GenerationResult(
        IReadOnlyList<TriggerDefinition> Generated,
        IReadOnlyList<string> Skipped);
}
