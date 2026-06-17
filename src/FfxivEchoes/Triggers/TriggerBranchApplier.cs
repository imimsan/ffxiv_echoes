using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// <see cref="RecordingBranchAnalyzer.Analyze"/> の検出結果を <see cref="TriggerFile"/> に
/// 反映する純粋関数。新規 <see cref="TimelineBranch"/> エントリの追加と、既存 mechanic への
/// <see cref="MechanicStrategy.BranchId"/> 自動付与を担当する。
/// </summary>
/// <remarks>
/// <para>
/// **既存設定は絶対に上書きしない**：mechanic.BranchId が既に設定済みなら触らない。
/// branch id 重複チェックも必ず実施。
/// </para>
/// <para>
/// 純粋関数なので戦闘中でも安全に呼べる。実際の永続化は呼び元（UI）の Save ボタンに任せる。
/// </para>
/// </remarks>
public static class TriggerBranchApplier
{
    /// <summary>適用結果のサマリ。UI に「N 件追加 / M 件更新」と表示する用。</summary>
    public sealed record ApplyResult(
        int BranchesAdded,
        int MechanicBranchIdsAssigned,
        int MechanicsGenerated,
        IReadOnlyList<string> Diagnostics);

    /// <summary>
    /// 検出結果から選ばれた <see cref="BranchGroup"/> 群を <see cref="TriggerFile"/> に反映。
    /// </summary>
    /// <param name="workingCopy">編集中の TriggerFile（呼び元の dirty 管理に従う）</param>
    /// <param name="result">analyzer の出力</param>
    /// <param name="selectedGroups">UI でチェックされたグループ（通常 result.Groups と同一）</param>
    /// <param name="generateMechanics">
    /// true：<paramref name="fullAggregate"/> から <see cref="StrategyDraftGenerator"/> で
    /// 新規 mechanic を生成し、各々に branch_id を自動付与する。
    /// false（既定）：branches[] と既存 mechanic への branch_id 付与のみ。
    /// </param>
    /// <param name="fullAggregate">generateMechanics=true 時に参照するゾーン全体の集約。null なら生成しない。</param>
    /// <param name="partyMembers">党員名（StrategyDraftGenerator のフィルタ用）。</param>
    public static ApplyResult Apply(
        TriggerFile workingCopy,
        BranchDetectionResult result,
        IReadOnlyList<BranchGroup> selectedGroups,
        bool generateMechanics = false,
        AggregatedEvents? fullAggregate = null,
        IReadOnlySet<string>? partyMembers = null)
    {
        if (workingCopy is null) throw new ArgumentNullException(nameof(workingCopy));
        if (selectedGroups is null || selectedGroups.Count == 0)
        {
            return new ApplyResult(0, 0, 0, new[] { "適用対象のグループがありません。" });
        }

        var diagnostics = new List<string>();
        var addedBranches = new List<TimelineBranch>();

        // ── ステップ 1: TimelineBranch を生成 ────────────
        // 既存 branches と cast_id 重複しているものはスキップ（同一判定キャストで二重定義しない）
        var existingCastIds = new HashSet<string>(
            workingCopy.Branches
                .Where(b => b.Condition is not null && !string.IsNullOrEmpty(b.Condition.CastId))
                .Select(b => NormalizeCastId(b.Condition!.CastId!)),
            StringComparer.OrdinalIgnoreCase);

        var existingBranchIds = new HashSet<string>(
            workingCopy.Branches.Select(b => b.Id ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);

        var index = 1;
        foreach (var g in selectedGroups)
        {
            if (string.IsNullOrEmpty(g.FirstCastId))
            {
                diagnostics.Add($"スキップ：グループに cast_id がありません ({g.FirstCastName})");
                continue;
            }
            var normalizedId = NormalizeCastId(g.FirstCastId);
            if (existingCastIds.Contains(normalizedId))
            {
                diagnostics.Add($"スキップ：cast_id={g.FirstCastId} の branch は既に存在します");
                continue;
            }

            var branchId = MakeUniqueBranchId(g.FirstCastName, index, existingBranchIds);
            var label = $"パターン{index}（{(string.IsNullOrEmpty(g.FirstCastName) ? g.FirstCastId : g.FirstCastName)}）";

            var branch = new TimelineBranch
            {
                Id = branchId,
                Label = label,
                Condition = new BranchCondition
                {
                    Type = "first_cast",
                    CastId = g.FirstCastId,
                    CastName = g.FirstCastName,
                    WindowSec = RecordingBranchAnalyzer.DefaultWindowSec,
                },
            };
            workingCopy.Branches.Add(branch);
            addedBranches.Add(branch);
            existingCastIds.Add(normalizedId);
            existingBranchIds.Add(branchId);
            index++;
        }

        // ── ステップ 2: 既存 mechanic への branch_id 自動付与 ──────
        // 各 branch group の AggregatedEvents から「そのグループにのみ出る cast_id」を抽出
        // → 既存 mechanic の AttachedTo.CastId と照合 → 自動付与
        var assignedCount = 0;
        var profile = StrategyPlanResolver.SelectActiveProfile(workingCopy);

        // exclusivePerBranch を後段の generateMechanics でも使うので外出し
        var exclusivePerBranch = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (profile is not null && addedBranches.Count >= 2)
        {
            // 各グループの cast_id 集合
            var perGroupCasts = addedBranches
                .Select(b => new
                {
                    Branch = b,
                    CastIds = ExtractCastIds(b, selectedGroups),
                })
                .Where(x => x.CastIds.Count > 0)
                .ToArray();

            // 「そのグループにのみ出る cast_id」を計算（他グループにも出るものは共通扱い）
            for (var i = 0; i < perGroupCasts.Length; i++)
            {
                var mine = perGroupCasts[i].CastIds;
                var others = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var j = 0; j < perGroupCasts.Length; j++)
                {
                    if (i == j) continue;
                    foreach (var c in perGroupCasts[j].CastIds) others.Add(c);
                }
                var exclusive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in mine)
                {
                    if (!others.Contains(c)) exclusive.Add(c);
                }
                exclusivePerBranch[perGroupCasts[i].Branch.Id] = exclusive;
            }

            // 既存 mechanic を走査して、AttachedTo.CastId が「ある branch にのみ出る」なら付与
            foreach (var mech in profile.Mechanics)
            {
                if (!string.IsNullOrEmpty(mech.BranchId))
                {
                    // 既設定は上書きしない（最重要ルール）
                    continue;
                }
                if (mech.AttachedTo?.CastId is not { } castId || string.IsNullOrEmpty(castId))
                {
                    continue;
                }
                var normalizedMechCast = NormalizeCastId(castId);
                foreach (var (branchId, exclusiveCasts) in exclusivePerBranch)
                {
                    if (exclusiveCasts.Contains(normalizedMechCast))
                    {
                        mech.BranchId = branchId;
                        assignedCount++;
                        diagnostics.Add($"  - {mech.Label ?? mech.Id} → {branchId}");
                        break;
                    }
                }
            }
        }

        // ── ステップ 3: generateMechanics=true なら新規 mechanic を自動生成 ──────
        // 全集約から StrategyDraftGenerator で生成し、各 mechanic の cast_id が
        // 特定 branch のみに出るなら自動的に branch_id を付与。共通キャストは branch_id=null。
        var generatedCount = 0;
        if (generateMechanics && fullAggregate is not null && profile is not null)
        {
            var draftResult = StrategyDraftGenerator.Generate(fullAggregate, profile, partyMembers);
            foreach (var mech in draftResult.Generated)
            {
                // cast_id ベースで branch_id を決定（exclusivePerBranch を再利用）
                if (mech.AttachedTo?.CastId is { } cid && !string.IsNullOrEmpty(cid))
                {
                    var normalizedCast = NormalizeCastId(cid);
                    foreach (var (branchId, exclusiveCasts) in exclusivePerBranch)
                    {
                        if (exclusiveCasts.Contains(normalizedCast))
                        {
                            mech.BranchId = branchId;
                            break;
                        }
                    }
                }
                profile.Mechanics.Add(mech);
                generatedCount++;
            }
            if (generatedCount > 0)
            {
                diagnostics.Add($"録画から新規 mechanic {generatedCount} 件を生成（branch_id は自動付与）");
            }
            if (draftResult.Skipped.Count > 0)
            {
                diagnostics.Add($"  既存と重複した {draftResult.Skipped.Count} 件はスキップ");
            }
        }

        if (addedBranches.Count == 0)
        {
            diagnostics.Insert(0, "新規分岐は追加されませんでした（既存と重複）。");
        }
        else
        {
            var summary = generatedCount > 0
                ? $"分岐 {addedBranches.Count} 件追加、新規 mechanic {generatedCount} 件、既存 mechanic {assignedCount} 件に branch_id 付与"
                : $"分岐 {addedBranches.Count} 件追加、既存 mechanic {assignedCount} 件に branch_id 付与";
            diagnostics.Insert(0, summary);
        }

        return new ApplyResult(addedBranches.Count, assignedCount, generatedCount, diagnostics);
    }

    /// <summary>
    /// グループ内の AggregatedEvents から cast_id 一覧を取り出す（normalize 済み）。
    /// </summary>
    private static HashSet<string> ExtractCastIds(TimelineBranch branch, IReadOnlyList<BranchGroup> groups)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // branch は selectedGroups の対応するグループの cast_id 集合を必要とする
        var matched = groups.FirstOrDefault(g =>
            string.Equals(NormalizeCastId(g.FirstCastId), NormalizeCastId(branch.Condition?.CastId ?? ""),
                StringComparison.OrdinalIgnoreCase));
        if (matched?.AggregatedEvents is null) return set;

        foreach (var ev in matched.AggregatedEvents.Events)
        {
            if (string.Equals(ev.Key.Type, "cast_start", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(ev.Key.Id))
            {
                set.Add(NormalizeCastId(ev.Key.Id));
            }
        }
        return set;
    }

    /// <summary>"0xABCD" / "0XABCD" / "ABCD" / 数値文字列 を統一表記に正規化。</summary>
    private static string NormalizeCastId(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        // 16 進数として解釈できれば 16 進大文字に統一、できなければ 10 進数文字列
        if (uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            return $"0x{hex:X}";
        }
        return s;
    }

    /// <summary>
    /// branch id の生成。既存と衝突しないようにする。
    /// </summary>
    private static string MakeUniqueBranchId(string castName, int index, HashSet<string> existingIds)
    {
        var sanitized = Sanitize(castName);
        var baseId = string.IsNullOrEmpty(sanitized)
            ? $"pattern_{index}"
            : $"pattern_{sanitized}";
        var id = baseId;
        var n = 1;
        while (existingIds.Contains(id))
        {
            n++;
            id = $"{baseId}_{n}";
        }
        return id;
    }

    /// <summary>
    /// 識別子化：英数字以外をアンダースコアに、連続を 1 つに、両端トリム。
    /// </summary>
    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var chars = value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        var s = new string(chars).Trim('_');
        while (s.Contains("__", StringComparison.Ordinal))
        {
            s = s.Replace("__", "_", StringComparison.Ordinal);
        }
        return s;
    }
}
