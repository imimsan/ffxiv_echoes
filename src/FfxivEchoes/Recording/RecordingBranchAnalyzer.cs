using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FfxivEchoes.Recording;

/// <summary>
/// 録画ファイル群を「先頭 N 秒以内に初めて分岐するキャスト」でクラスタリングして
/// タイムライン分岐を自動検出する純粋関数。
/// </summary>
/// <remarks>
/// <para>
/// 例：滅エヌオーで 9 戦の録画があり、5 戦が「エアロジャ」で始まり 4 戦が「フレア」で
/// 始まる場合、2 つの <see cref="BranchGroup"/> を返す。各グループの集約結果は
/// <see cref="RecordingAggregationReader.AggregateFiles"/> に該当ファイル群だけを渡して得る。
/// </para>
/// <para>
/// パフォーマンス：各ファイルは <see cref="StreamReader"/> で「最初の cast_start」が
/// 見つかるか <see cref="DefaultWindowSec"/> 秒を超えるまでだけ読む。20 ファイルで
/// 通常 1 秒以内に完了。
/// </para>
/// </remarks>
public static class RecordingBranchAnalyzer
{
    /// <summary>分岐判定の窓（戦闘開始からこの秒数以内のキャストを判定対象）。</summary>
    public const double DefaultWindowSec = 30.0;

    /// <summary>1 ファイルのみのグループは「外れ値」として除外。</summary>
    private const int MinGroupSize = 2;

    /// <summary>主要グループとして UI に出す上限。8 分岐級のギミックを落とさない。</summary>
    private const int MaxPrimaryGroups = 8;

    /// <summary>
    /// 録画ファイル群を解析して分岐を検出。
    /// </summary>
    /// <param name="paths">対象の jsonl ファイルパス。順序は問わない。</param>
    /// <param name="windowSec">先頭何秒以内のキャストを判定対象とするか。</param>
    /// <param name="aggregateGroups">true なら各 BranchGroup の AggregatedEvents も計算（重い）。</param>
    public static BranchDetectionResult Analyze(
        IEnumerable<string> paths,
        double windowSec = DefaultWindowSec,
        bool aggregateGroups = true)
    {
        // ── ステップ 1: 各ファイルの先頭 windowSec 秒内 cast_start 列を抽出 ─────
        var sequences = new List<FileCastSequence>();
        foreach (var path in paths)
        {
            var seq = TryReadCastSequence(path, windowSec);
            if (seq is not null)
            {
                sequences.Add(seq.Value);
            }
        }

        var total = sequences.Count;
        if (total <= 1)
        {
            return new BranchDetectionResult
            {
                TotalFilesScanned = total,
                NotEnoughData = true,
                DiagnosticsMessage = total == 0
                    ? "録画ファイルが見つかりません。/echoes record on で録画してから再試行してください。"
                    : "録画ファイルが 1 件のみです。分岐検出には 2 件以上必要です。",
            };
        }

        // ── ステップ 2: 先頭から見て最初に複数パターンへ割れる cast index を探す ───
        var maxCasts = sequences.Max(s => s.Casts.Count);
        CastGroup[] byId = Array.Empty<CastGroup>();
        var branchCastIndex = -1;
        for (var i = 0; i < maxCasts; i++)
        {
            var groupsAtIndex = BuildGroupsAtIndex(sequences, i);
            var validAtIndex = groupsAtIndex.Where(g => g.Files.Count >= MinGroupSize).ToArray();
            if (validAtIndex.Length >= 2)
            {
                byId = groupsAtIndex;
                branchCastIndex = i;
                break;
            }
        }

        if (branchCastIndex < 0)
        {
            var firstGroups = BuildGroupsAtIndex(sequences, 0);
            var firstValid = firstGroups.Where(g => g.Files.Count >= MinGroupSize).ToArray();
            var firstNoise = firstGroups.Where(g => g.Files.Count < MinGroupSize).ToArray();
            var validFileTotalForNoBranch = firstValid.Sum(g => g.Files.Count);

            return new BranchDetectionResult
            {
                TotalFilesScanned = total,
                NoBranchDetected = firstValid.Length == 1,
                NotEnoughData = firstValid.Length != 1,
                DiagnosticsMessage = firstValid.Length == 1
                    ? $"全 {validFileTotalForNoBranch} 件が同じ判定キャスト（{firstValid[0].CastName}）でした。" +
                      $"このコンテンツに分岐はないか、判定キャストが {windowSec:0} 秒以降に来る可能性があります。"
                    : "分岐候補が見つかりませんでした。同じパターンを 2 件以上録画してから再試行してください。",
                OutlierGroups = firstNoise
                    .Select(g => BuildGroup(g.CastId, g.CastName, g.Files, 0.0, aggregateGroups))
                    .ToArray(),
            };
        }

        // ── ステップ 3: 1 件のみグループ（ノイズ）と多数派を分離 ────
        var noiseGroups = byId.Where(g => g.Files.Count < MinGroupSize).ToArray();
        var validGroups = byId.Where(g => g.Files.Count >= MinGroupSize).ToArray();
        var validFileTotal = validGroups.Sum(g => g.Files.Count);

        if (validGroups.Length == 0)
        {
            return new BranchDetectionResult
            {
                TotalFilesScanned = total,
                NotEnoughData = true,
                DiagnosticsMessage = "全ファイルがそれぞれ 1 件ずつ別パターンでした。同じパターンを 2 件以上録画してから再試行してください。",
                OutlierGroups = noiseGroups
                    .Select(g => BuildGroup(g.CastId, g.CastName, g.Files, 0.0, aggregateGroups))
                    .ToArray(),
            };
        }

        if (validGroups.Length == 1)
        {
            return new BranchDetectionResult
            {
                TotalFilesScanned = total,
                NoBranchDetected = true,
                DiagnosticsMessage = $"全 {validFileTotal} 件が同じ最初のキャスト（{validGroups[0].CastName}）でした。" +
                    $"このコンテンツに分岐はないか、判定キャストが {windowSec:0} 秒以降に来る可能性があります。",
                OutlierGroups = noiseGroups
                    .Select(g => BuildGroup(g.CastId, g.CastName, g.Files, 0.0, aggregateGroups))
                    .ToArray(),
            };
        }

        // ── ステップ 4: 上位 8 件を主要、残りを Additional として返す ──
        var primary = validGroups
            .Take(MaxPrimaryGroups)
            .Select(g => BuildGroup(g.CastId, g.CastName, g.Files,
                (double)g.Files.Count / validFileTotal, aggregateGroups))
            .ToArray();
        var additional = validGroups
            .Skip(MaxPrimaryGroups)
            .Select(g => BuildGroup(g.CastId, g.CastName, g.Files,
                (double)g.Files.Count / validFileTotal, aggregateGroups))
            .ToArray();

        var discriminator = branchCastIndex == 0
            ? "最初のキャスト"
            : $"{branchCastIndex + 1} 番目のキャスト";
        var diag = additional.Length > 0
            ? $"{discriminator}で {validGroups.Length} 種類のパターンが検出されました。上位 {MaxPrimaryGroups} 件を採用、残り {additional.Length} 件は追加候補として保持しました。"
            : $"{discriminator}で {validGroups.Length} 種類のパターンが検出されました（合計 {validFileTotal} 件、外れ値 {noiseGroups.Length} 件）。";

        return new BranchDetectionResult
        {
            TotalFilesScanned = total,
            Groups = primary,
            AdditionalGroups = additional,
            OutlierGroups = noiseGroups
                .Select(g => BuildGroup(g.CastId, g.CastName, g.Files, 0.0, aggregateGroups))
                .ToArray(),
            DiagnosticsMessage = diag,
        };
    }

    private static CastGroup[] BuildGroupsAtIndex(IReadOnlyList<FileCastSequence> sequences, int index)
    {
        return sequences
            .Where(s => s.Casts.Count > index)
            .Select(s => new { s.Path, Cast = s.Casts[index] })
            .GroupBy(x => x.Cast.CastId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var bestName = g
                    .GroupBy(x => x.Cast.CastName, StringComparer.Ordinal)
                    .OrderByDescending(nameG => nameG.Count())
                    .First()
                    .Key;
                return new CastGroup(
                    g.Key,
                    bestName,
                    g.Select(x => x.Path).ToArray());
            })
            .OrderByDescending(g => g.Files.Count)
            .ThenBy(g => g.CastId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static BranchGroup BuildGroup(
        string castId, string castName, IReadOnlyList<string> files,
        double confidence, bool aggregate)
    {
        AggregatedEvents? aggregatedEvents = null;
        if (aggregate && files.Count > 0)
        {
            try
            {
                aggregatedEvents = RecordingAggregationReader.AggregateFiles(files);
            }
            catch
            {
                // 集約失敗は致命的ではない。null のまま返して呼び出し側で再試行可。
            }
        }
        return new BranchGroup
        {
            FirstCastId = castId,
            FirstCastName = castName,
            FileCount = files.Count,
            FilePaths = files,
            Confidence = confidence,
            AggregatedEvents = aggregatedEvents,
        };
    }

    /// <summary>
    /// 1 ファイルから windowSec 以内の cast_start 列を取り出す。
    /// </summary>
    private static FileCastSequence? TryReadCastSequence(string path, double windowSec)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var casts = new List<CastSignature>();
        try
        {
            using var stream = RecordingFileIO.OpenReadShared(path);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("meta", out _)) continue;

                    var time = root.TryGetProperty("time", out var tp) ? tp.GetDouble() : 0;
                    if (time > windowSec) break; // 窓を超えた → 以後は判定対象外

                    if (!root.TryGetProperty("type", out var typeProp)) continue;
                    if (typeProp.GetString() != "cast_start") continue;

                    var castId = root.TryGetProperty("cast_id", out var ip)
                        ? (ip.ValueKind == JsonValueKind.String ? ip.GetString() : ip.ToString())
                        : null;
                    if (string.IsNullOrEmpty(castId)) continue;

                    var castName = root.TryGetProperty("cast_name", out var np)
                        ? (np.GetString() ?? string.Empty)
                        : string.Empty;
                    casts.Add(new CastSignature(castId, castName));
                }
            }
        }
        catch
        {
            // I/O 失敗 / 不正 jsonl → このファイルは判定不能、null
        }
        return casts.Count == 0 ? null : new FileCastSequence(path, casts.ToArray());
    }

    private readonly record struct CastGroup(string CastId, string CastName, IReadOnlyList<string> Files);
    private readonly record struct CastSignature(string CastId, string CastName);
    private readonly record struct FileCastSequence(string Path, IReadOnlyList<CastSignature> Casts);
}
