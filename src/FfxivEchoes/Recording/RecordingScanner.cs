using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace FfxivEchoes.Recording;

/// <summary>
/// {ConfigDirectory}/recordings/{zone}/*.jsonl をスキャンして、
/// イベントを集計（観測回数や初回出現時刻）する。M8 の集計ビューに使う。
/// </summary>
public sealed class RecordingScanner
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;
    private readonly HashSet<string> _warnedOldRecordingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _warnGate = new();
    private readonly Dictionary<string, (string Sig, AggregatedEvents Events)> _aggCache = new();
    private readonly object _aggCacheGate = new();
    private readonly Dictionary<string, (string Sig, IReadOnlyList<string> Members)> _partyCache = new();
    private readonly object _partyCacheGate = new();
    private readonly Dictionary<string, (string Sig, SegmentedAggregate Seg)> _segmentCache = new();
    private readonly object _segmentCacheGate = new();
    // 戦闘中はキャッシュを固定する。録画ファイルは AutoFlush で毎フレーム mtime が変わり署名が
    // 変化するため、固定しないとフェーズ移行のたびに全録画を同期再読込してフレーム落ちする（FIX-03）。
    private volatile bool _combatCacheLocked;

    public RecordingScanner(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    /// <summary>
    /// 戦闘中キャッシュ固定の切り替え。true の間は <see cref="Aggregate"/> /
    /// <see cref="ListPartyMembers"/> が既存キャッシュをそのまま返し、ディスク I/O を行わない
    /// （キャッシュ未生成のときのみ一度だけ読み込む）。CombatStarted で true、CombatEnded /
    /// ZoneChanged で false にする想定（<see cref="RecordingWarmupService"/> が駆動）。
    /// </summary>
    public void SetCombatCacheLock(bool locked) => _combatCacheLocked = locked;

    public IReadOnlyList<string> ListZonesWithRecordings()
    {
        var root = Path.Combine(_pluginInterface.ConfigDirectory.FullName, "recordings");
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }
        try
        {
            return Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] recordings ディレクトリ列挙に失敗");
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<RecordingFileInfo> ListRecordings(string zoneName)
    {
        var dir = Path.Combine(_pluginInterface.ConfigDirectory.FullName, "recordings", SanitizeSegment(zoneName));
        if (!Directory.Exists(dir))
        {
            return Array.Empty<RecordingFileInfo>();
        }
        try
        {
            return Directory.EnumerateFiles(dir, "*.jsonl")
                .Select(p =>
                {
                    var fi = new FileInfo(p);
                    return new RecordingFileInfo(p, fi.Length, fi.LastWriteTimeUtc);
                })
                .OrderByDescending(r => r.LastModifiedUtc)
                .ToArray();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] recordings の列挙に失敗：{Zone}", zoneName);
            return Array.Empty<RecordingFileInfo>();
        }
    }

    /// <summary>
    /// 指定ゾーンのすべての jsonl からイベントを集計する。M8 の集計ビュー用。
    /// </summary>
    public AggregatedEvents Aggregate(string zoneName)
    {
        // 戦闘中はキャッシュ固定。既存キャッシュがあれば署名計算（= ディレクトリ走査の I/O）も
        // せずに即返す。フェーズ移行のたびの全録画再読込によるフレーム落ちを防ぐ（FIX-03）。
        if (_combatCacheLocked)
        {
            lock (_aggCacheGate)
            {
                if (_aggCache.TryGetValue(zoneName, out var locked))
                {
                    return locked.Events;
                }
            }
        }

        var recordings = ListRecordings(zoneName);
        var sig = ComputeRecordingSignature(recordings);
        lock (_aggCacheGate)
        {
            if (_aggCache.TryGetValue(zoneName, out var cached) && cached.Sig == sig)
            {
                return cached.Events;
            }
        }
        // CombatStart 時に複数サービス(NoteReminder / PredictedCastReminder / SyncOffset 等)が
        // 同じ zone を Aggregate するため、録画に変化が無ければ集計結果を再利用し、
        // 全ファイル再読込(戦闘開始直後の重さの主因)を避ける。
        var events = RecordingAggregationReader.AggregateFiles(
            recordings.Select(r => r.Path),
            OnAggregateWarning);
        lock (_aggCacheGate)
        {
            _aggCache[zoneName] = (sig, events);
        }
        return events;
    }

    /// <summary>
    /// 録画を開幕 cast でプルセグメント（前半フルプル / 後半頭出し練習プル 等）に自動分離した集計を返す。
    /// 分岐が検出できない（単群 / データ不足）ゾーンでは <see cref="Aggregate"/> 相当の全合算へ
    /// フォールバックするため、他コンテンツの挙動は変わらない。戦闘中は <see cref="Aggregate"/> と同じく
    /// キャッシュ固定（I/O ゼロ）。
    /// </summary>
    /// <remarks>
    /// 全ファイルの先頭 cast が一致する単群コンテンツ（例: 月の底）に加え、2 体が同時に異なる cast を
    /// 詠唱して毎回先頭 cast がバラけるコンテンツ（例: 暗闇の領域）も、有効グループが 1 つに満たず
    /// 分岐なし扱い → combined フォールバックになる（回帰なし）。
    /// </remarks>
    public SegmentedAggregate AggregateBySegment(string zoneName)
    {
        if (_combatCacheLocked)
        {
            lock (_segmentCacheGate)
            {
                if (_segmentCache.TryGetValue(zoneName, out var locked))
                {
                    return locked.Seg;
                }
            }
        }

        var recordings = ListRecordings(zoneName);
        var sig = ComputeRecordingSignature(recordings);
        lock (_segmentCacheGate)
        {
            if (_segmentCache.TryGetValue(zoneName, out var cached) && cached.Sig == sig)
            {
                return cached.Seg;
            }
        }

        // combined は Aggregate() の zone キャッシュを再利用（重複 I/O なし）。
        var combined = Aggregate(zoneName);
        BranchDetectionResult? branch = null;
        try
        {
            branch = RecordingBranchAnalyzer.Analyze(
                recordings.Select(r => r.Path),
                RecordingBranchAnalyzer.DefaultWindowSec,
                aggregateGroups: true);
        }
        catch (Exception ex)
        {
            // 解析失敗時は分離なし（combined フォールバック）。HUD は止めない。
            _log.Debug(ex, "[FfxivEchoes] AggregateBySegment: 分岐解析に失敗 zone={Zone}", zoneName);
        }

        var seg = RecordingSegmentBuilder.BuildSegmentedAggregate(branch, combined);
        lock (_segmentCacheGate)
        {
            _segmentCache[zoneName] = (sig, seg);
        }
        return seg;
    }

    /// <summary>
    /// 録画群の署名（件数 + 各ファイルの Size + 最終更新時刻）。録画が増減・更新されたら
    /// 署名が変わり、<see cref="Aggregate"/> のキャッシュが無効化される。
    /// </summary>
    public static string ComputeRecordingSignature(IReadOnlyList<RecordingFileInfo> recordings)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(recordings.Count).Append(';');
        // 順序非依存にするため Path でソート。Size と mtime の変化（録画追記・差し替え）も検出する。
        foreach (var r in recordings.OrderBy(r => r.Path, StringComparer.Ordinal))
        {
            sb.Append(r.Path).Append('|').Append(r.Size).Append('|')
              .Append(r.LastModifiedUtc.Ticks).Append(';');
        }
        return sb.ToString();
    }

    private void OnAggregateWarning(Exception ex, string path)
    {
        // 「古い録画」警告は同一パスにつき1回だけ。Aggregate は戦闘中・設定ウィンドウの描画で
        // 高頻度に呼ばれるため、旧バージョン録画の警告で dalamud.log が氾濫するのを防ぐ。
        if (ShouldSuppressOldRecordingWarning(ex, path, _warnedOldRecordingPaths, _warnGate))
        {
            return;
        }
        _log.Warning(ex, "[FfxivEchoes] 録画ファイル読み込みに失敗：{Path}", path);
    }

    /// <summary>
    /// 「古い録画フォーマット」警告（<see cref="InvalidDataException"/>）を同一パスにつき1回だけ
    /// 通すための判定。未記録なら記録して false（＝ログする）、記録済みなら true（＝抑制する）を返す。
    /// <see cref="InvalidDataException"/> 以外（IOException 等の実害ある失敗）は常に false（抑制しない）。
    /// </summary>
    public static bool ShouldSuppressOldRecordingWarning(
        Exception ex, string path, HashSet<string> warnedOldRecordingPaths, object gate)
    {
        if (ex is not InvalidDataException)
        {
            return false;
        }
        lock (gate)
        {
            // 初回は Add が true（記録して通す）、2 回目以降は false（抑制する）。
            return !warnedOldRecordingPaths.Add(path);
        }
    }

    /// <summary>
    /// 指定ゾーンの **全** 録画ファイルの meta 行から party メンバー名を **union** で抽出する。
    /// 集計ビューで「自分・PT のイベントを隠す」フィルタに使う。
    /// </summary>
    /// <remarks>
    /// 旧実装は最新 1 件しか読まなかったため、複数戦闘で PT メンバーが入れ替わると
    /// 過去戦闘の PC のスキル / cast 名がタイムラインに漏れる原因だった。全ファイルから
    /// 名前を union することで、過去 PT メンバーも漏れなくフィルタ対象に入れる。
    /// </remarks>
    public IReadOnlyList<string> ListPartyMembers(string zoneName)
    {
        // 戦闘中はキャッシュ固定（Aggregate と同様）。既存キャッシュがあれば I/O ゼロで返す。
        if (_combatCacheLocked)
        {
            lock (_partyCacheGate)
            {
                if (_partyCache.TryGetValue(zoneName, out var locked))
                {
                    return locked.Members;
                }
            }
        }

        var recordings = ListRecordings(zoneName);
        if (recordings.Count == 0) return Array.Empty<string>();

        // CombatStart で PredictedCastReminder が毎回呼ぶため、Aggregate と同様に zone 単位で
        // キャッシュし、全録画 meta 行の再読込（開幕の重さの一因）を避ける。
        var sig = ComputeRecordingSignature(recordings);
        lock (_partyCacheGate)
        {
            if (_partyCache.TryGetValue(zoneName, out var cached) && cached.Sig == sig)
            {
                return cached.Members;
            }
        }

        var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in recordings)
        {
            try
            {
                using var stream = RecordingFileIO.OpenReadShared(rec.Path);
                using var reader = new StreamReader(stream);
                var line = reader.ReadLine();
                if (string.IsNullOrEmpty(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("meta", out var meta) || !meta.GetBoolean()) continue;
                if (!root.TryGetProperty("party", out var partyArr) ||
                    partyArr.ValueKind != JsonValueKind.Array) continue;
                foreach (var member in partyArr.EnumerateArray())
                {
                    if (member.TryGetProperty("name", out var nameEl) &&
                        nameEl.ValueKind == JsonValueKind.String)
                    {
                        var n = nameEl.GetString();
                        if (!string.IsNullOrEmpty(n)) union.Add(n);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] meta party の読み出しに失敗：{Path}", rec.Path);
            }
        }
        var result = union.ToArray();
        lock (_partyCacheGate)
        {
            _partyCache[zoneName] = (sig, result);
        }
        return result;
    }

    /// <summary>
    /// recordings/ 配下の全 *.jsonl を一括で返す（ゾーン区切りなし）。
    /// アリーナ推定の fallback で「ゾーン名が見つからない」場合に使う。
    /// </summary>
    private IReadOnlyList<RecordingFileInfo> ListAllRecordingsFlat()
    {
        var root = Path.Combine(_pluginInterface.ConfigDirectory.FullName, "recordings");
        if (!Directory.Exists(root)) return Array.Empty<RecordingFileInfo>();
        try
        {
            return Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .Select(p =>
                {
                    var fi = new FileInfo(p);
                    return new RecordingFileInfo(p, fi.Length, fi.LastWriteTimeUtc);
                })
                .OrderByDescending(r => r.LastModifiedUtc)
                .ToArray();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] recordings 全列挙に失敗");
            return Array.Empty<RecordingFileInfo>();
        }
    }

    /// <summary>
    /// 録画の object_appear 座標から推定したアリーナ寸法。
    /// 中心は bounding box の中点、幅／奥行は X / Z の min-max 差分。
    /// </summary>
    public sealed record ArenaEstimate(
        double CenterX,
        double CenterZ,
        double Width,
        double Depth,
        double Radius,
        int SampleCount);

    /// <summary>
    /// アリーナ推定のデバッグ情報。録画 / 座標数 / どこで止まったか等を返して
    /// 「なぜ推定できないのか」をユーザーに見せられるようにする。
    /// </summary>
    public sealed record ArenaEstimateDiagnostics(
        string Zone,
        int RecordingsByZone,
        int RecordingsFlat,
        int ObjectAppearCount,
        int ValidSampleCount,
        string? FailureReason)
    {
        /// <summary>player_pos イベント数（戦闘中の自分位置サンプル）。0 = 旧録画 or キャプチャ未動作。</summary>
        public int PlayerPosCount { get; init; }

        /// <summary>採用したデータソース。player_pos 優先、無ければ object_appear。</summary>
        public string SourceUsed { get; init; } = "";
    }

    /// <summary>
    /// 指定ゾーンの録画ファイル全部から object_appear 座標を収集し、
    /// bounding box ベースでアリーナ寸法を推定。
    /// 戦闘 1 戦ぶんでも数十〜数百の出現座標が取れるので、極端な外れ値があっても
    /// 95 %タイル幅で丸める。
    /// </summary>
    public ArenaEstimate? EstimateArenaFromRecordings(string zoneName)
    {
        return EstimateArenaWithDiagnostics(zoneName).Estimate;
    }

    /// <summary>
    /// 推定結果に加えて「なぜ取れなかった／取れた」の診断情報を返すバージョン。
    /// UI の popup に「ファイル N 件 / 座標 M 件」を表示するために使う。
    /// </summary>
    public (ArenaEstimate? Estimate, ArenaEstimateDiagnostics Diag) EstimateArenaWithDiagnostics(string zoneName)
    {
        var recordingsByZone = ListRecordings(zoneName);
        _log.Information("[FfxivEchoes] アリーナ推定開始 zone={Zone} 録画 {N} 件", zoneName, recordingsByZone.Count);

        var recordings = recordingsByZone;
        var flatCount = 0;
        if (recordings.Count == 0)
        {
            var flat = ListAllRecordingsFlat();
            flatCount = flat.Count;
            recordings = flat;
            _log.Information("[FfxivEchoes] zone 一致無し、全録画 {N} 件で fallback 推定", flatCount);
            if (recordings.Count == 0)
            {
                return (null, new ArenaEstimateDiagnostics(zoneName, 0, 0, 0, 0,
                    "recordings/ ディレクトリに jsonl が 1 件も無い。/echoes record on で録画してください。"));
            }
        }

        // player_pos と object_appear を別々に集めて、player_pos が十分量あればそちらを優先採用。
        // プレイヤーは戦闘中に必ず端まで動かされるので、bbox がアリーナ床面とほぼ一致する。
        // object_appear はボス／add の出現座標で中央寄りに偏るため、bbox が小さくなる傾向がある。
        var playerXs = new List<double>();
        var playerZs = new List<double>();
        var objectXs = new List<double>();
        var objectZs = new List<double>();
        var totalObjectAppear = 0;
        var totalPlayerPos = 0;
        foreach (var rec in recordings)
        {
            try
            {
                using var stream = RecordingFileIO.OpenReadShared(rec.Path);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // 破損／途中切れ行はファイル全体を捨てず当該行のみスキップ（TryVoteNpcActionFromRecordingLine と同じ防御）。
                    using var doc = TryParseRecordingLine(line);
                    if (doc is null) continue;
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeEl)) continue;
                    var t = typeEl.GetString();
                    if (t != "object_appear" && t != "player_pos") continue;

                    if (!root.TryGetProperty("position", out var posEl) || posEl.ValueKind != JsonValueKind.Object) continue;
                    if (!posEl.TryGetProperty("x", out var xEl) || !posEl.TryGetProperty("z", out var zEl)) continue;
                    var x = xEl.GetDouble();
                    var z = zEl.GetDouble();
                    if (x == 0 && z == 0) continue; // sentinel

                    if (t == "player_pos")
                    {
                        totalPlayerPos++;
                        playerXs.Add(x);
                        playerZs.Add(z);
                    }
                    else
                    {
                        totalObjectAppear++;
                        objectXs.Add(x);
                        objectZs.Add(z);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] アリーナ推定中の読み込み失敗：{Path}", rec.Path);
            }
        }

        // データソース選択：player_pos が 50 件以上あればそれを優先（戦闘 25 秒分以上）。
        // 旧録画には player_pos が無いので、無ければ object_appear にフォールバック。
        List<double> xs;
        List<double> zs;
        string sourceUsed;
        const int PlayerPosMinForUse = 50;
        if (playerXs.Count >= PlayerPosMinForUse)
        {
            xs = playerXs;
            zs = playerZs;
            sourceUsed = "player_pos";
        }
        else if (objectXs.Count >= 1)
        {
            xs = objectXs;
            zs = objectZs;
            sourceUsed = playerXs.Count > 0
                ? $"object_appear (player_pos {playerXs.Count} 件は少なすぎ)"
                : "object_appear (旧録画)";
        }
        else
        {
            xs = playerXs; // たとえ少なくても player があればそれで
            zs = playerZs;
            sourceUsed = playerXs.Count > 0 ? "player_pos (少サンプル)" : "なし";
        }

        _log.Information(
            "[FfxivEchoes] アリーナ推定：player_pos={Pp} 件, object_appear={Oa} 件, 採用={Src} ({Use} 件)",
            totalPlayerPos, totalObjectAppear, sourceUsed, xs.Count);

        if (xs.Count < 1)
        {
            string reason;
            if (totalObjectAppear == 0 && totalPlayerPos == 0)
            {
                reason = $"録画ファイル {recordings.Count} 件はあるが、" +
                         "player_pos / object_appear イベントが 1 件も無い。" +
                         "プラグインを再読み込みして 1 戦闘してから再試行してください。";
            }
            else if (totalPlayerPos == 0)
            {
                reason = $"object_appear は {totalObjectAppear} 件あるが、すべて (0,0) 座標で位置情報無し。" +
                         "また player_pos イベントも 0 件（旧録画かキャプチャ未動作）。" +
                         "プラグインを再読み込みして 1 戦闘してください。";
            }
            else
            {
                reason = $"player_pos {totalPlayerPos} 件 / object_appear {totalObjectAppear} 件はあるが、すべて (0,0)。";
            }
            _log.Warning("[FfxivEchoes] アリーナ推定失敗: {Reason}", reason);
            return (null, new ArenaEstimateDiagnostics(
                zoneName, recordingsByZone.Count, flatCount, totalObjectAppear, 0, reason)
            {
                PlayerPosCount = totalPlayerPos,
                SourceUsed = sourceUsed,
            });
        }

        xs.Sort();
        zs.Sort();
        double Pct(IReadOnlyList<double> sorted, double p)
        {
            var i = (int)Math.Round((sorted.Count - 1) * p);
            i = Math.Clamp(i, 0, sorted.Count - 1);
            return sorted[i];
        }

        double xLow, xHigh, zLow, zHigh;
        if (xs.Count >= 50)
        {
            // 多サンプル時：99%タイルで外れ値除去（プレイヤーは床のほぼ端まで行ける想定なので狭めない）
            xLow = Pct(xs, 0.005); xHigh = Pct(xs, 0.995);
            zLow = Pct(zs, 0.005); zHigh = Pct(zs, 0.995);
        }
        else if (xs.Count >= 20)
        {
            xLow = Pct(xs, 0.025); xHigh = Pct(xs, 0.975);
            zLow = Pct(zs, 0.025); zHigh = Pct(zs, 0.975);
        }
        else
        {
            xLow = xs[0]; xHigh = xs[^1];
            zLow = zs[0]; zHigh = zs[^1];
        }

        var cx = (xLow + xHigh) * 0.5;
        var cz = (zLow + zHigh) * 0.5;
        // player_pos ベースは「実際にプレイヤーが踏んだ範囲」なので、外周まで踏み切れずに
        // やや内側で止まる可能性がある。少し余裕（2m）を持たせて床面想定に近づける。
        var marginM = string.Equals(sourceUsed, "player_pos", StringComparison.Ordinal) ? 2.0 : 0.0;
        var w = Math.Max(20.0, xHigh - xLow + marginM * 2);
        var d = Math.Max(20.0, zHigh - zLow + marginM * 2);
        var radius = Math.Sqrt(w * w + d * d) * 0.5;

        // 0.5 m 刻みに丸める
        double Snap(double v) => Math.Round(v * 2.0) * 0.5;
        var estimate = new ArenaEstimate(
            CenterX: Snap(cx),
            CenterZ: Snap(cz),
            Width: Snap(w),
            Depth: Snap(d),
            Radius: Snap(radius),
            SampleCount: xs.Count);
        return (estimate, new ArenaEstimateDiagnostics(
            zoneName, recordingsByZone.Count, flatCount, totalObjectAppear, xs.Count, null)
        {
            PlayerPosCount = totalPlayerPos,
            SourceUsed = sourceUsed,
        });
    }

    /// <summary>
    /// 指定ゾーンの全録画ファイルを削除する。recordings/{zone}/ ディレクトリごと消す。
    /// 戻り値は削除したファイル数。
    /// </summary>
    public int DeleteAllRecordings(string zoneName)
    {
        var dir = Path.Combine(_pluginInterface.ConfigDirectory.FullName, "recordings", SanitizeSegment(zoneName));
        if (!Directory.Exists(dir)) return 0;
        var deleted = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl"))
            {
                try { File.Delete(f); deleted++; }
                catch (Exception ex) { _log.Warning(ex, "[FfxivEchoes] 録画削除失敗：{Path}", f); }
            }
            // ディレクトリも残骸として消す（空なら）
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch { /* 残ってもよい */ }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] 録画ディレクトリ削除に失敗：{Zone}", zoneName);
        }
        _log.Information("[FfxivEchoes] 録画 {N} 件削除：{Zone}", deleted, zoneName);
        return deleted;
    }

    /// <summary>
    /// 指定ゾーンの録画から、特定 DataId（NPC 種類）が出現直後に実行した最頻のアクション ID を返す。
    /// 出現と cast_start/action_used の対応が録画されていない / マッチしない場合は null。
    /// </summary>
    /// <param name="zoneName">対象ゾーン名</param>
    /// <param name="dataId">NPC の DataId</param>
    /// <param name="maxDelaySec">出現から詠唱までの最大遅延（秒、既定 8 秒）</param>
    /// <returns>(actionId, observationCount) — actionId は 0x の 16 進文字列だが uint で返す</returns>
    public (uint ActionId, int ObservationCount)? FindNpcFirstActionAfterAppearance(
        string zoneName,
        uint dataId,
        double maxDelaySec = 8.0)
    {
        var candidates = FindNpcActionCandidatesAfterAppearance(zoneName, dataId, objectName: null, maxDelaySec);
        return candidates.Count == 0 ? null : candidates[0];
    }

    public IReadOnlyList<(uint ActionId, int ObservationCount)> FindNpcActionCandidatesAfterAppearance(
        string zoneName,
        uint dataId,
        string? objectName,
        double maxDelaySec = 8.0)
    {
        var recordings = ListRecordings(zoneName);
        if (recordings.Count == 0) return Array.Empty<(uint ActionId, int ObservationCount)>();

        // 各録画ファイル内で「該当 DataId の出現時刻」と「同じ object_id 由来の最初の action」を結ぶ。
        // 詠唱しないオブジェクト AoE も、action_used が取れていれば次回出現時に Lumina 形状で描ける。
        var actionVotes = new Dictionary<uint, int>();
        foreach (var rec in recordings)
        {
            try
            {
                ProcessNpcAppearancesInFile(rec.Path, dataId, objectName, maxDelaySec, actionVotes);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] NPC 初回詠唱学習に失敗：{Path}", rec.Path);
            }
        }
        return actionVotes
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value))
            .ToArray();
    }

    public (uint ActionId, int ObservationCount)? FindNpcFirstCastAction(
        string zoneName,
        uint dataId,
        double maxDelaySec = 8.0)
        => FindNpcFirstActionAfterAppearance(zoneName, dataId, maxDelaySec);

    private static void ProcessNpcAppearancesInFile(
        string path,
        uint targetDataId,
        string? targetObjectName,
        double maxDelaySec,
        Dictionary<uint, int> actionVotes)
    {
        // 1 パスでファイル全体を読み、object_id → 出現時刻 のマップを作りつつ、
        // cast_start / action_used を見たら対応する出現時刻と比較して窓内ならアクションをカウントする
        var npcAppeared = new Dictionary<uint, double>(); // object_id → first appear time
        using var stream = RecordingFileIO.OpenReadShared(path);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            TryVoteNpcActionFromRecordingLine(line, targetDataId, targetObjectName, maxDelaySec, npcAppeared, actionVotes);
        }
    }

    public static bool TryVoteNpcActionFromRecordingLine(
        string line,
        uint targetDataId,
        double maxDelaySec,
        IDictionary<uint, double> npcAppeared,
        IDictionary<uint, int> actionVotes)
        => TryVoteNpcActionFromRecordingLine(
            line,
            targetDataId,
            targetObjectName: null,
            maxDelaySec,
            npcAppeared,
            actionVotes);

    private static JsonDocument? TryParseRecordingLine(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool TryVoteNpcActionFromRecordingLine(
        string line,
        uint targetDataId,
        string? targetObjectName,
        double maxDelaySec,
        IDictionary<uint, double> npcAppeared,
        IDictionary<uint, int> actionVotes)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        // ライブ録画は AutoFlush 書き込み中で末尾行が途中切れになることがある。破損／途中切れ行は
        // 例外を投げず静かにスキップする（RecordingAggregationReader.AggregateFiles と同じ防御）。
        // さもないと NPC 初回詠唱学習が全録画スキャンのたびに JsonReaderException を投げ、
        // フレームスレッドを直撃して dalamud.log を例外で氾濫させる。
        using var doc = TryParseRecordingLine(line);
        if (doc is null) return false;
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeEl)) return false;
        var type = typeEl.GetString();
        var time = root.TryGetProperty("time", out var t) ? t.GetDouble() : 0;

        if (type == "object_appear")
        {
            if (!root.TryGetProperty("data_id", out var dEl) ||
                dEl.ValueKind != JsonValueKind.Number) return false;
            var dataId = dEl.GetUInt32();
            if (dataId != targetDataId) return false;
            if (!MatchesObjectName(root, targetObjectName)) return false;
            if (!root.TryGetProperty("object_id", out var oEl) ||
                oEl.ValueKind != JsonValueKind.Number) return false;

            if (root.TryGetProperty("entity_id", out var eEl) &&
                eEl.ValueKind == JsonValueKind.Number)
            {
                npcAppeared[eEl.GetUInt32()] = time;
            }
            else
            {
                npcAppeared[oEl.GetUInt32()] = time;
            }
            return true;
        }

        if (type != "cast_start" && type != "action_used")
        {
            return false;
        }

        if (type == "action_used" &&
            root.TryGetProperty("auto_attack", out var aaEl) &&
            aaEl.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        if (!root.TryGetProperty("source_id", out var sEl) ||
            sEl.ValueKind != JsonValueKind.Number) return false;
        var sourceId = sEl.GetUInt32();
        if (!npcAppeared.TryGetValue(sourceId, out var appearAt)) return false;
        if (time < appearAt || time - appearAt > maxDelaySec) return false;

        var idProperty = type == "cast_start" ? "cast_id" : "action_id";
        if (!root.TryGetProperty(idProperty, out var idEl)) return false;
        var actionIdStr = idEl.GetString();
        if (!TryParseHexId(actionIdStr, out var actionId)) return false;

        npcAppeared.Remove(sourceId);
        actionVotes.TryGetValue(actionId, out var count);
        actionVotes[actionId] = count + 1;
        return true;
    }

    private static bool MatchesObjectName(JsonElement root, string? targetObjectName)
    {
        if (string.IsNullOrWhiteSpace(targetObjectName))
        {
            return true;
        }

        if (!root.TryGetProperty("object_name", out var nameEl) ||
            nameEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var recorded = nameEl.GetString();
        if (string.IsNullOrWhiteSpace(recorded))
        {
            return false;
        }

        var expected = targetObjectName.Trim();
        var actual = recorded.Trim();
        return actual.Contains(expected, StringComparison.OrdinalIgnoreCase) ||
               expected.Contains(actual, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseHexId(string? value, out uint actionId)
    {
        actionId = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return uint.TryParse(
            s,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out actionId);
    }

    private static string SanitizeSegment(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "Unknown";
        }
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }
}

public readonly record struct EventKey(
    string Type,
    string? Id,
    string? Name,
    string? Source,
    string? Target);

public sealed record AggregatedEvent(
    EventKey Key,
    int Count,
    double FirstSeenSeconds)
{
    public IReadOnlyList<double> ObservedTimesSeconds { get; init; } = Array.Empty<double>();
    public IReadOnlyList<AggregatedOccurrence> Occurrences { get; init; } = Array.Empty<AggregatedOccurrence>();
    public bool IsPartySource { get; init; }
}

public sealed record AggregatedOccurrence(
    int Index,
    double RepresentativeTimeSeconds,
    int SeenCount,
    IReadOnlyList<double> ObservedTimesSeconds);

public sealed record AggregatedEvents(
    IReadOnlyList<AggregatedEvent> Events,
    int BattleCount,
    int TotalEventCount,
    int RecordingFileCount);

public sealed record RecordingFileInfo(string Path, long Size, DateTime LastModifiedUtc);
