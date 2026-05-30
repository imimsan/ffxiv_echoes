using System;
using System.Collections.Generic;
using System.Linq;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

public static class StrategyPlanResolver
{
    /// <summary>profile.PhaseArenaShapes から指定フェーズの設定を取得（無ければ null）。</summary>
    public static PhaseArenaSpec? GetPhaseSpec(StrategyProfile profile, string? phase)
    {
        if (string.IsNullOrEmpty(phase)) return null;
        return profile.PhaseArenaShapes.TryGetValue(phase, out var s) ? s : null;
    }

    public static StrategyProfile? SelectActiveProfile(TriggerFile file)
    {
        if (file.StrategyProfiles.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(file.ActiveStrategyProfileId))
        {
            var active = file.StrategyProfiles.FirstOrDefault(p =>
                p.Enabled &&
                string.Equals(p.Id, file.ActiveStrategyProfileId, StringComparison.OrdinalIgnoreCase));
            if (active is not null)
            {
                return active;
            }
        }

        return file.StrategyProfiles.FirstOrDefault(p => p.Enabled);
    }

    public static IReadOnlyList<TimelineNote> BuildTimelineNotes(TriggerFile file)
        => BuildTimelineNotes(file, branchActiveCheck: null, phaseActiveCheck: null);

    public static IReadOnlyList<TimelineNote> BuildTimelineNotes(
        TriggerFile file, Func<string?, bool>? branchActiveCheck)
        => BuildTimelineNotes(file, branchActiveCheck, phaseActiveCheck: null);

    /// <summary>
    /// <paramref name="branchActiveCheck"/> / <paramref name="phaseActiveCheck"/> オーバーロード：
    /// 分岐 (TimelineBranch) 未確定 / Rejected の mechanic、および過去フェーズの mechanic を
    /// 表示から除外する。各引数 null はそのフィルタ不使用 = 全 mechanic 表示。
    /// </summary>
    /// <param name="branchActiveCheck">
    /// (string? branchId) → bool。true なら表示、false なら除外。
    /// 通常は <see cref="BranchObserverService.IsActiveOrCommon"/> を渡す。
    /// </param>
    /// <param name="phaseActiveCheck">
    /// (string? phase) → bool。true なら表示、過去フェーズなら false で除外。
    /// 通常は <see cref="CurrentPhaseTracker.IsPhaseActive"/> を渡す。
    /// </param>
    public static IReadOnlyList<TimelineNote> BuildTimelineNotes(
        TriggerFile file,
        Func<string?, bool>? branchActiveCheck,
        Func<string?, bool>? phaseActiveCheck)
    {
        var profile = SelectActiveProfile(file);
        if (profile is null)
        {
            return Array.Empty<TimelineNote>();
        }

        return profile.Mechanics
            .Where(m => m.Enabled)
            .Where(m => branchActiveCheck is null || branchActiveCheck(m.BranchId))
            .Where(m => phaseActiveCheck is null || phaseActiveCheck(m.Phase))
            // object_appear 由来の mechanic は timeline 表示から除外。
            // 「ベヒーモス」「ケツァクワァトル」「脱出地点」「秘紋」等のボス名 / オブジェクト名が
            // 並んでタイムラインを汚染する問題への対策。これらは「いつ何を回避するか」という
            // timeline の用途とずれているため。手動 mechanic（SourceEventType=null）は通す。
            .Where(m => !string.Equals(m.SourceEventType, "object_appear", StringComparison.OrdinalIgnoreCase))
            .Select(m => BuildTimelineNote(profile, m))
            .ToArray();
    }

    public static TimelineNote BuildTimelineNote(StrategyProfile profile, MechanicStrategy mechanic)
    {
        return new TimelineNote
        {
            Id = $"strategy_{profile.Id}_{mechanic.Id}",
            Time = mechanic.Time ?? 0,
            Label = string.IsNullOrEmpty(mechanic.Label) ? mechanic.Id : mechanic.Label,
            Duration = mechanic.Duration,
            Role = mechanic.Role,
            Job = mechanic.Job,
            Color = mechanic.Color ?? "#F472B6",
            AttachedTo = mechanic.AttachedTo,
            Icons = new List<string> { "S" },
            AdvanceWarningSec = mechanic.AdvanceWarningSec,
            WarningText = mechanic.WarningText ?? mechanic.Callout ?? mechanic.Label,
            // Time 未設定（=0）の mechanic でも、複数出現するキャストの正しい回を選べるよう転写。
            OccurrenceIndex = mechanic.OccurrenceIndex ?? 0,
        };
    }

    public static IReadOnlyList<ActionDefinition> BuildReminderActions(
        StrategyProfile profile,
        MechanicStrategy mechanic)
    {
        return BuildReminderActions(profile, mechanic, AutoSafeCallPlanner.IsRaidWide);
    }

    public static IReadOnlyList<ActionDefinition> BuildReminderActions(
        TriggerFile? file,
        StrategyProfile profile,
        MechanicStrategy mechanic)
    {
        return BuildReminderActions(profile, mechanic, (id, name) => AutoSafeCallPlanner.IsRaidWide(file, id, name));
    }

    public static IReadOnlyList<ActionDefinition> BuildReminderActions(
        StrategyProfile profile,
        MechanicStrategy mechanic,
        Func<uint, string?, bool> isRaidWide)
    {
        var actions = new List<ActionDefinition>();
        var text = mechanic.WarningText ?? mechanic.Callout ?? mechanic.Label;
        if (!string.IsNullOrWhiteSpace(text))
        {
            actions.Add(new ActionDefinition
            {
                Type = "tts",
                Text = text,
            });
        }

        var positions = SelectPositions(profile, mechanic);
        var suppressMinimap =
            AutoSafeCallPlanner.ShouldSuppressMinimap(mechanic.AttachedTo, isRaidWide) ||
            mechanic.Triggers.Any(t => AutoSafeCallPlanner.ShouldSuppressMinimap(t.Match, isRaidWide));

        // DisableMinimap のときは arena_view を出さない（TTS / overlay のみ）
        if (!mechanic.DisableMinimap &&
            !suppressMinimap &&
            (!string.IsNullOrWhiteSpace(mechanic.Gimmick) ||
             positions.Count > 0 ||
             mechanic.SafeZone is not null ||
             mechanic.ObjectMarkers.Count > 0 ||
             mechanic.AoeZones.Count > 0))
        {
            // user_layout モード：ユーザーが地図エディタで描いたものをそのまま渡す。
            // 既存 gimmick も併用可能（既定の outer_ring 等の上にカスタム要素を重ねる）。
            actions.Add(new ActionDefinition
            {
                Type = "arena_view",
                Gimmick = string.IsNullOrWhiteSpace(mechanic.Gimmick) ? "user_layout" : mechanic.Gimmick,
                Callout = mechanic.Callout ?? mechanic.WarningText ?? mechanic.Label,
                Duration = mechanic.Duration ?? 5.0,
                SafeZone = mechanic.SafeZone,
                StrategyProfileId = profile.Id,
                MechanicId = mechanic.Id,
                StrategyPositions = positions,
                ObjectMarkers = mechanic.ObjectMarkers.Count > 0 ? new List<StrategyObjectMarker>(mechanic.ObjectMarkers) : null,
                AoeZones = mechanic.AoeZones.Count > 0 ? new List<StrategyAoeZone>(mechanic.AoeZones) : null,
                AoeSequence = mechanic.AoeSequence,
                PartyStatusHighlights = mechanic.PartyStatusHighlights.Count > 0
                    ? new List<StatusHighlightSpec>(mechanic.PartyStatusHighlights) : null,
                // 解決順：メカニクス override → フェーズ既定 → プロファイル既定
                ArenaShape = mechanic.ArenaShape ?? GetPhaseSpec(profile, mechanic.Phase)?.Shape ?? profile.ArenaShape,
                ArenaRadius = mechanic.ArenaRadius ?? GetPhaseSpec(profile, mechanic.Phase)?.Radius ?? profile.ArenaRadius,
                ArenaWidth = mechanic.ArenaWidth ?? GetPhaseSpec(profile, mechanic.Phase)?.Width ?? profile.ArenaWidth,
                ArenaDepth = mechanic.ArenaDepth ?? GetPhaseSpec(profile, mechanic.Phase)?.Depth ?? profile.ArenaDepth,
                ArenaCenterX = mechanic.ArenaCenterX ?? GetPhaseSpec(profile, mechanic.Phase)?.CenterX ?? profile.ArenaCenterX,
                ArenaCenterZ = mechanic.ArenaCenterZ ?? GetPhaseSpec(profile, mechanic.Phase)?.CenterZ ?? profile.ArenaCenterZ,
            });
        }

        return actions;
    }

    /// <param name="branchActiveCheck">
    /// (branchId) → bool。false の branch_id を持つ mechanic は除外する。
    /// </param>
    /// <returns>
    /// <c>BranchRejected</c> は「この予測に合致する mechanic は存在したが、すべて分岐で除外された」
    /// ことを表す。<c>Mechanic == null</c> でも「mechanic 未定義（=BranchRejected false）」と
    /// 「分岐棄却（=BranchRejected true）」を呼び出し側が区別できるようにするためのフラグ。
    /// </returns>
    public static (StrategyProfile? Profile, MechanicStrategy? Mechanic, bool BranchRejected) FindMechanicForPrediction(
        TriggerFile file,
        RecordingPrediction prediction,
        Func<string?, bool>? branchActiveCheck,
        double maxTimeDeltaSeconds = 15.0)
    {
        var profile = SelectActiveProfile(file);
        if (profile is null)
        {
            return (null, null, false);
        }

        var (mechanic, branchRejected) = FindBestMechanic(
            profile,
            prediction.CastId,
            prediction.Label,
            prediction.RelativeSeconds,
            maxTimeDeltaSeconds,
            branchActiveCheck);
        return (profile, mechanic, branchRejected);
    }

    public static (StrategyProfile? Profile, MechanicStrategy? Mechanic, bool BranchRejected) FindMechanicForPrediction(
        TriggerFile file,
        RecordingPrediction prediction,
        double maxTimeDeltaSeconds = 15.0)
        => FindMechanicForPrediction(file, prediction, branchActiveCheck: null, maxTimeDeltaSeconds);

    public static (StrategyProfile? Profile, MechanicStrategy? Mechanic) FindMechanicForCast(
        TriggerFile file,
        uint castActionId,
        string castActionName,
        Func<string?, bool>? branchActiveCheck,
        double? relativeSeconds = null,
        double maxTimeDeltaSeconds = 15.0)
    {
        var profile = SelectActiveProfile(file);
        if (profile is null)
        {
            return (null, null);
        }

        var (mechanic, _) = FindBestMechanic(
            profile,
            $"0x{castActionId:X}",
            castActionName,
            relativeSeconds,
            maxTimeDeltaSeconds,
            branchActiveCheck);
        return mechanic is null ? (profile, null) : (profile, mechanic);
    }

    public static (StrategyProfile? Profile, MechanicStrategy? Mechanic) FindMechanicForCast(
        TriggerFile file,
        uint castActionId,
        string castActionName,
        double? relativeSeconds = null,
        double maxTimeDeltaSeconds = 15.0)
        => FindMechanicForCast(file, castActionId, castActionName, branchActiveCheck: null, relativeSeconds, maxTimeDeltaSeconds);

    public static MechanicStrategy CreateMechanicDraft(RecordingTimelinePrediction prediction, string id)
        => CreateMechanicDraft(prediction, id, profile: null);

    /// <summary>
    /// 録画予測から MechanicStrategy 下書きを 1 件生成する。
    /// </summary>
    /// <param name="profile">
    /// 渡されたら、プロファイルのアリーナ形状／寸法／中心を mechanic 側にコピーして下書きを self-contained にする。
    /// 後段の cascade（mechanic ?? phase ?? profile）に頼らず、ここで「決まった形」が入っている方が
    /// 「正方形のプロファイルなのに mechanic マップが円形」みたいな見え方の事故が起きにくい。
    /// </param>
    public static MechanicStrategy CreateMechanicDraft(
        RecordingTimelinePrediction prediction,
        string id,
        StrategyProfile? profile)
    {
        var mech = new MechanicStrategy
        {
            Id = id,
            Label = prediction.Label,
            Time = prediction.RelativeSeconds,
            AdvanceWarningSec = 5.0,
            WarningText = prediction.Label,
            Callout = prediction.Label,
            AttachedTo = BuildMatch(prediction),
            Color = prediction.Confidence >= 0.75 ? "#F472B6" : "#FBBF24",
            SourceEventType = prediction.EventType,
            ObservedCount = prediction.ObservedCount,
            OccurrenceSeenCount = prediction.OccurrenceSeenCount,
            Confidence = prediction.Confidence,
            TimeJitterSeconds = prediction.TimeJitterSeconds,
            OccurrenceIndex = prediction.OccurrenceIndex,
        };

        if (profile is not null)
        {
            mech.ArenaShape = profile.ArenaShape;
            mech.ArenaRadius = profile.ArenaRadius;
            mech.ArenaWidth = profile.ArenaWidth;
            mech.ArenaDepth = profile.ArenaDepth;
            mech.ArenaCenterX = profile.ArenaCenterX;
            mech.ArenaCenterZ = profile.ArenaCenterZ;
        }

        return mech;
    }

    /// <summary>
    /// メカニクスをディープコピーする。ランダムギミック分岐の「片方を作って → 複製 → 形状違い」
    /// パターンに使う。<see cref="MechanicStrategy.SpreadPositions"/> /
    /// <see cref="MechanicStrategy.ObjectMarkers"/> / <see cref="MechanicStrategy.AoeZones"/> /
    /// <see cref="MechanicStrategy.Triggers"/> も別 List として複製する。
    /// </summary>
    public static MechanicStrategy CloneMechanic(MechanicStrategy src)
    {
        var copy = new MechanicStrategy
        {
            Id = src.Id,
            Label = src.Label,
            Phase = src.Phase,
            Enabled = src.Enabled,
            DisableMinimap = src.DisableMinimap,
            Time = src.Time,
            AdvanceWarningSec = src.AdvanceWarningSec,
            Duration = src.Duration,
            Color = src.Color,
            Callout = src.Callout,
            WarningText = src.WarningText,
            Gimmick = src.Gimmick,
            ArenaShape = src.ArenaShape,
            ArenaRadius = src.ArenaRadius,
            ArenaWidth = src.ArenaWidth,
            ArenaDepth = src.ArenaDepth,
            ArenaCenterX = src.ArenaCenterX,
            ArenaCenterZ = src.ArenaCenterZ,
            Role = src.Role,
            Job = src.Job,
            SafeZone = src.SafeZone,
            AttachedTo = src.AttachedTo,
            SourceEventType = src.SourceEventType,
            ObservedCount = src.ObservedCount,
            OccurrenceSeenCount = src.OccurrenceSeenCount,
            OccurrenceIndex = src.OccurrenceIndex,
            Confidence = src.Confidence,
            TimeJitterSeconds = src.TimeJitterSeconds,
        };
        // 子コレクションは値コピー（参照の使い回しを避けて編集を独立させる）
        foreach (var sp in src.SpreadPositions)
        {
            copy.SpreadPositions.Add(new StrategyPosition
            {
                Slot = sp.Slot, Label = sp.Label, Role = sp.Role, Job = sp.Job,
                X = sp.X, Z = sp.Z, Color = sp.Color,
            });
        }
        foreach (var mk in src.ObjectMarkers)
        {
            copy.ObjectMarkers.Add(new StrategyObjectMarker
            {
                Id = mk.Id, Label = mk.Label, X = mk.X, Z = mk.Z,
                Color = mk.Color, Shape = mk.Shape, Note = mk.Note, Waymark = mk.Waymark,
            });
        }
        foreach (var z in src.AoeZones)
        {
            copy.AoeZones.Add(new StrategyAoeZone
            {
                Id = z.Id, Label = z.Label, Shape = z.Shape,
                X = z.X, Z = z.Z,
                RadiusM = z.RadiusM, InnerRadiusM = z.InnerRadiusM,
                RotationDeg = z.RotationDeg, FanDeg = z.FanDeg, HalfWidthM = z.HalfWidthM,
                Color = z.Color, IsDanger = z.IsDanger,
                Anchor = z.Anchor, AnchorWaymark = z.AnchorWaymark,
                // Phase 4 追加：actor 追跡 / フィルタ / 連鎖配線用フィールド
                ActorMatcher = z.ActorMatcher,
                StateFilter = z.StateFilter,
                RotationSource = z.RotationSource,
                LiveFloorPaint = z.LiveFloorPaint,
                DurationSec = z.DurationSec,
                SuppressAutoAoe = z.SuppressAutoAoe,
            });
        }
        if (src.AoeSequence is { } seq)
        {
            // 連鎖シーケンスもディープコピー（編集独立性）
            copy.AoeSequence = new AoeSequence
            {
                Id = seq.Id,
                CancelOnCastCancel = seq.CancelOnCastCancel,
                Steps = new System.Collections.Generic.List<AoeSequenceStep>(seq.Steps.Count),
            };
            foreach (var step in seq.Steps)
            {
                var clonedZones = new System.Collections.Generic.List<StrategyAoeZone>(step.Zones.Count);
                foreach (var sz in step.Zones)
                {
                    clonedZones.Add(new StrategyAoeZone
                    {
                        Id = sz.Id, Label = sz.Label, Shape = sz.Shape,
                        X = sz.X, Z = sz.Z, RadiusM = sz.RadiusM, InnerRadiusM = sz.InnerRadiusM,
                        RotationDeg = sz.RotationDeg, FanDeg = sz.FanDeg, HalfWidthM = sz.HalfWidthM,
                        Color = sz.Color, IsDanger = sz.IsDanger,
                        Anchor = sz.Anchor, AnchorWaymark = sz.AnchorWaymark,
                        ActorMatcher = sz.ActorMatcher, StateFilter = sz.StateFilter,
                        RotationSource = sz.RotationSource, LiveFloorPaint = sz.LiveFloorPaint,
                        DurationSec = sz.DurationSec, SuppressAutoAoe = sz.SuppressAutoAoe,
                    });
                }
                copy.AoeSequence.Steps.Add(new AoeSequenceStep
                {
                    DelaySec = step.DelaySec,
                    DurationSec = step.DurationSec,
                    Label = step.Label,
                    Zones = clonedZones,
                });
            }
        }
        foreach (var t in src.Triggers)
        {
            copy.Triggers.Add(new MechanicTrigger
            {
                Type = t.Type, Match = t.Match,
                ActorName = t.ActorName, ActorDataId = t.ActorDataId,
                FacingDeg = t.FacingDeg, FacingToleranceDeg = t.FacingToleranceDeg,
                HpPctBelow = t.HpPctBelow, HpPctAbove = t.HpPctAbove,
                Phase = t.Phase,
                ObjectCountMin = t.ObjectCountMin, ObjectCountMax = t.ObjectCountMax,
                ObjectWindowSec = t.ObjectWindowSec,
                DedupSec = t.DedupSec,
            });
        }
        foreach (var h in src.PartyStatusHighlights)
        {
            copy.PartyStatusHighlights.Add(new StatusHighlightSpec
            {
                StatusId = h.StatusId, StatusName = h.StatusName,
                Color = h.Color, Badge = h.Badge,
            });
        }
        return copy;
    }

    private static MatchCondition? BuildMatch(RecordingTimelinePrediction prediction)
    {
        return prediction.EventType switch
        {
            "cast_start" => new MatchCondition
            {
                CastId = EmptyToNull(prediction.Id),
                CastName = prediction.Label,
                Source = EmptyToNull(prediction.Source),
            },
            "action_used" or "auto_attack" => new MatchCondition
            {
                ActionId = EmptyToNull(prediction.Id),
                ActionName = prediction.Label,
                Source = EmptyToNull(prediction.Source),
            },
            "status_gain" or "status_update" => new MatchCondition
            {
                StatusId = uint.TryParse(prediction.Id, out var statusId) ? statusId : null,
                StatusName = prediction.Label,
                Target = string.IsNullOrWhiteSpace(prediction.Target)
                    ? null
                    : new TargetSpec(new List<string> { prediction.Target }),
            },
            "object_appear" or "object_disappear" => new MatchCondition
            {
                Actor = prediction.Label,
            },
            _ => null,
        };
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <returns>
    /// 最良の合致 mechanic（無ければ null）と、<c>BranchRejected</c>（=合致する候補は
    /// 存在したが、すべて <paramref name="branchActiveCheck"/> で除外され、有効な mechanic が
    /// 1 件も残らなかった）。「mechanic 未定義」と「分岐棄却」を区別するために使う。
    /// </returns>
    private static (MechanicStrategy? Mechanic, bool BranchRejected) FindBestMechanic(
        StrategyProfile profile,
        string castId,
        string castName,
        double? relativeSeconds,
        double maxTimeDeltaSeconds,
        Func<string?, bool>? branchActiveCheck)
    {
        MechanicStrategy? best = null;
        var bestDistance = double.MaxValue;
        var branchRejected = false;

        foreach (var mechanic in profile.Mechanics)
        {
            if (!mechanic.Enabled || mechanic.AttachedTo is not { } match)
            {
                continue;
            }

            if (!MatchesCast(match, castId, castName))
            {
                continue;
            }

            var distance = relativeSeconds is not null && mechanic.Time is not null
                ? Math.Abs(mechanic.Time.Value - relativeSeconds.Value)
                : 0;
            if (relativeSeconds is not null &&
                mechanic.Time is not null &&
                distance > maxTimeDeltaSeconds)
            {
                continue;
            }

            // ここまで来た = cast / 時刻的にこの予測に合致する候補。
            // 分岐で除外された場合だけ branchRejected を立て、active な候補が
            // 1 件でもあればそちらを優先する。
            if (branchActiveCheck is not null && !branchActiveCheck(mechanic.BranchId))
            {
                branchRejected = true;
                continue;
            }

            if (distance < bestDistance)
            {
                best = mechanic;
                bestDistance = distance;
            }
        }

        return (best, best is null && branchRejected);
    }

    private static bool MatchesCast(MatchCondition match, string castId, string castName)
    {
        if (!string.IsNullOrEmpty(match.CastId) &&
            !CastIdsEqual(match.CastId, castId))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(match.CastName) &&
            !string.Equals(match.CastName, castName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrEmpty(match.CastId) || !string.IsNullOrEmpty(match.CastName);
    }

    private static bool CastIdsEqual(string left, string right)
    {
        return TryParseHexId(left, out var leftId) && TryParseHexId(right, out var rightId)
            ? leftId == rightId
            : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseHexId(string value, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return uint.TryParse(
            text,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out id);
    }

    private static List<StrategyPosition> SelectPositions(StrategyProfile profile, MechanicStrategy mechanic)
    {
        // 1. メカニクス専用 SpreadPositions が指定されていれば最優先（ギミック単位の上書き）
        if (mechanic.SpreadPositions.Count > 0)
        {
            return mechanic.SpreadPositions.ToList();
        }

        // 2. positions slot list が指定されていればプロファイルからその slot だけ抽出
        if (mechanic.Positions.Count > 0)
        {
            var wanted = new HashSet<string>(mechanic.Positions, StringComparer.OrdinalIgnoreCase);
            return profile.SpreadPositions
                .Where(p => !string.IsNullOrEmpty(p.Slot) && wanted.Contains(p.Slot))
                .ToList();
        }

        // 3. 散開ギミックならプロファイル全体を継承、それ以外は何も出さない
        return string.Equals(mechanic.Gimmick, "scatter", StringComparison.OrdinalIgnoreCase)
            ? profile.SpreadPositions.ToList()
            : new List<StrategyPosition>();
    }
}
