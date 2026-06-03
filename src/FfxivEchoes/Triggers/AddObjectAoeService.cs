using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Diagnostics;
using FfxivEchoes.Events;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Utils;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Triggers;

/// <summary>
/// add NPC が同時出現したことを検出して、その世界座標に AoE 範囲を即座に
/// ミニマップへ描画するサービス。
/// </summary>
/// <remarks>
/// 例：極ゾディアーク「ステュクス」発動時、ケラノウス・エイドロンが 4 体出現する。
/// それぞれが独立した AoE を出すパターンで、出現座標が AoE 範囲を確定させる。
/// 同一 DataId が短時間に出現したら、最後の出現から <see cref="StabilityDelaySec"/>
/// 待って一括で AoE 円群を描画する（最初の 2 体目で発火してしまうと、3-4 体目を取りこぼす）。
/// </remarks>
public sealed class AddObjectAoeService : IDisposable
{
    /// <summary>同時出現とみなす時間窓（秒）。</summary>
    private const double WindowSec = 1.5;

    /// <summary>最後の追加からこの時間ラジオサイレンスが続いたら確定して描画。
    /// 録画ログ実測：月の底パラデイグマ・ケツァクウァトルは出現後 82ms で即時アクション発動
    /// （cast バー無し）。82ms はゲーム 5 フレームでこれを予告として使うには遅延を限界まで
    /// 削る必要がある。OnAppear で同フレーム同時 publish される group は OnUpdate 同一フレーム
    /// で全 member が _groups に揃っているため、stability 待ちは不要。即時発火に切り替えて
    /// publish → 描画開始までを 1 フレーム (~17ms) に抑える。
    /// 段階的出現の group は最初の 1 体で発火し、後続は DedupSec の suppress で抑制される。</summary>
    private const double StabilityDelaySec = 0.0;

    /// <summary>同じ DataId の再発火を抑制する時間（秒）。</summary>
    private const double DedupSec = 6.0;

    /// <summary>このグループサイズ未満なら描画しない。</summary>
    private const int MinGroupSize = 2;

    private const double DefaultDisplayDurationSec = 14.0;
    private const double LiveObjectScanDurationSec = 8.0;
    private const double LiveObjectScanIntervalSec = 0.5;
    private const double PlaceholderCenterDistanceM = 0.75;
    private const int MaxLiveObjectAoeMembers = 8;

    private readonly IFramework _framework;
    private readonly IEventBus _bus;
    private readonly IMinimapSink _minimap;
    private readonly TriggerStore _store;
    private readonly SafeCallDictionary? _dictionary;
    private readonly RecordingScanner? _recordings;
    private readonly IActionLookup? _actionLookup;
    private readonly IObjectTable? _objectTable;
    private readonly IPluginLog _log;

    private readonly IDisposable _appearSub;
    private readonly IDisposable _disappearSub;
    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;
    private readonly IDisposable _zoneSub;
    private readonly IDisposable _castStartSub;
    private readonly IDisposable _castCompleteSub;
    private readonly IDisposable _actionUsedSub;

    // 戦闘開始前のオブジェクト出現で AoE を発火させない（ユーザー体感「START バナー前に AoE が出る」対策）。
    // ObjectCapture は CombatStartedEvent 時に _seen をクリアして全オブジェクトを再 publish するため、
    // 戦闘開始の瞬間にギミック用オブジェクトは「新規出現」として再到着する。pre-combat ではゲートし、
    // 戦闘開始後の resnap appear から受け付けて AoE を描画する。
    private bool _inCombat;

    // グループキーは「Name 優先、無ければ DataId 文字列」。同名なら DataId が違っても束ねる
    // （例：パラデイグマで 4 体出る add NPC は同名でも内部 DataId が個別の場合がある）。
    private readonly Dictionary<string, GroupState> _groups = new(StringComparer.OrdinalIgnoreCase);
    // CleanupExpiredGroups の毎フレーム new List を避ける再利用バッファ（NEW-08。OnUpdate の lock 内専用）。
    private readonly List<string> _deadGroupKeys = new();
    private readonly Dictionary<string, DateTimeOffset> _objectAoeSuppressUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LearnedAoe> _learnedAoeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LearnedAoe> _recordedActionAoeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _recordedActionAoeMisses = new(StringComparer.OrdinalIgnoreCase);
    // ステップ4（録画 NPC 初回詠唱学習）の miss/hold を負キャッシュ。これが無いと、解決できない
    // オブジェクトが出現／0.5 秒ライブスキャン窓ごとにゾーン内全録画をフルスキャン＋全行 Parse
    // し直し、フレームスレッドを直撃する。cacheKey 単位で記録し、_recordedActionAoeMisses と
    // 同じライフサイクル（ゾーン変更・トリガー再読込）でクリアする。Reset() では保持＝戦闘跨ぎで
    // 再スキャンしない（録画は戦闘中に増えないため結果は決定的）。
    private readonly HashSet<string> _firstCastLearnMisses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, bool> _playerActionCache = new();
    private readonly List<LiveScanWindow> _liveScanWindows = new();
    private readonly Dictionary<string, DateTimeOffset> _recentCastSourceNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private string _currentZone = "Unknown";

    private readonly record struct LearnedAoe(StrategyAoeZone Zone, string ShapeNote, string Source);

    public sealed record RecordedObjectActionCandidate(uint ActionId, int ObservationCount, string? ActionName);

    private static string MakeGroupKey(uint dataId, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name)) return $"name:{name.Trim()}";
        return $"id:{dataId}";
    }

    public static string MakeObjectAoeSuppressKey(uint dataId, string? name)
        => MakeGroupKey(dataId, name);

    public static string MakeLiveObjectAoeSuppressKey(uint dataId, string? name)
        => MakeObjectAoeSuppressKey(dataId, name);

    private static string MakeCacheKey(uint dataId, string? name)
        => $"id:{dataId}:name:{(name ?? string.Empty).Trim()}";

    public AddObjectAoeService(
        IFramework framework,
        IEventBus bus,
        IMinimapSink minimap,
        TriggerStore store,
        SafeCallDictionary? dictionary,
        RecordingScanner? recordings,
        IActionLookup? actionLookup,
        IObjectTable? objectTable,
        IPluginLog log)
    {
        _framework = framework;
        _bus = bus;
        _minimap = minimap;
        _store = store;
        _dictionary = dictionary;
        _recordings = recordings;
        _actionLookup = actionLookup;
        _objectTable = objectTable;
        _log = log;

        _appearSub = bus.Subscribe<ObjectAppearedEvent>(OnAppear);
        // add が消滅したら suppress をクリア。これが無いと、同名 add が suppress 期間
        // (Max(DedupSec, duration)≒14秒) 内に再出現したとき AoE が描画されない
        // （絶コンテンツは同名ギミック NPC を複数フェーズで再利用する）。
        _disappearSub = bus.Subscribe<ObjectDisappearedEvent>(OnObjectDisappeared);
        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ => _inCombat = true);
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ =>
        {
            _inCombat = false;
            Reset();
        });
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(z =>
        {
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName;
            _inCombat = false;
            lock (_gate)
            {
                _learnedAoeCache.Clear();
                _recordedActionAoeCache.Clear();
                _recordedActionAoeMisses.Clear();
                _firstCastLearnMisses.Clear();
                _playerActionCache.Clear();
                _liveScanWindows.Clear();
                _recentCastSourceNames.Clear();
                _groups.Clear();
                _objectAoeSuppressUntil.Clear();
            }
        });
        _castStartSub = bus.Subscribe<CastStartedEvent>(OnCastStart);
        _castCompleteSub = bus.Subscribe<CastCompletedEvent>(OnCastComplete);
        _actionUsedSub = bus.Subscribe<ActionUsedEvent>(OnActionUsed);
        // トリガー JSON が再読込されたら学習済み AoE キャッシュを捨て、更新後の object_aoe_rules を
        // 反映させる（戦闘中のルール編集・無効化が即時効くようにする）。
        _store.Reloaded += OnTriggersReloaded;
        _framework.Update += OnUpdate;
    }

    private void OnTriggersReloaded()
    {
        lock (_gate)
        {
            _learnedAoeCache.Clear();
            _recordedActionAoeCache.Clear();
            _recordedActionAoeMisses.Clear();
            _firstCastLearnMisses.Clear();
        }
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _appearSub.Dispose();
        _disappearSub.Dispose();
        _combatStartSub.Dispose();
        _combatEndSub.Dispose();
        _zoneSub.Dispose();
        _castStartSub.Dispose();
        _castCompleteSub.Dispose();
        _actionUsedSub.Dispose();
        _store.Reloaded -= OnTriggersReloaded;
        Reset();
    }

    private void Reset()
    {
        lock (_gate)
        {
            _groups.Clear();
            _objectAoeSuppressUntil.Clear();
            _liveScanWindows.Clear();
            _recentCastSourceNames.Clear();
            _playerActionCache.Clear();
        }
    }

    private LearnedAoe? LookupAoe(uint dataId, string name)
    {
        var cacheKey = MakeCacheKey(dataId, name);
        var file = _store.GetByZone(_currentZone);
        var arena = AutoAoeDisplayPolicy.ResolveArena(file);

        // 1. 攻略登録のコンテンツ別 object AoE ルール（最優先）。
        if (ObjectAoeRuleResolver.TryResolve(file, dataId, name, arena, out var ruleMatch))
        {
            var v = new LearnedAoe(ruleMatch.Zone, ruleMatch.ShapeNote, ruleMatch.Source);
            lock (_gate) _learnedAoeCache[cacheKey] = v;
            return v;
        }

        lock (_gate)
        {
            if (_learnedAoeCache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        // 2. 辞書（手動 override / 既知パターン）。
        try
        {
            if (TryBuildDictionaryAoe(name, arena, out var dictionaryZone))
            {
                PersistLearnedObjectRule(file, dataId, name, dictionaryZone, "dictionary");
                var v = new LearnedAoe(
                    dictionaryZone,
                    ObjectAoeRuleResolver.ShapeNoteFor(dictionaryZone.Shape),
                    "dictionary");
                lock (_gate) _learnedAoeCache[cacheKey] = v;
                return v;
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[FfxivEchoes] AddObjectAoe: 辞書 lookup 失敗 name={Name}", name);
        }

        // 3. 録画済み action_used から「このオブジェクト名が実行する AoE」を学習。
        // 月の底/パラデイグマ系は、ギミック用オブジェクトが戦闘開始時点で ObjectTable に
        // 存在しており、object_appear は実際の出現タイミングでは来ない。この経路で救う。
        try
        {
            if (TryBuildRecordedActionAoe(name, arena, out var recordedActionZone))
            {
                PersistLearnedObjectRule(file, dataId, name, recordedActionZone, "recording_action");
                var v = new LearnedAoe(
                    recordedActionZone,
                    ObjectAoeRuleResolver.ShapeNoteFor(recordedActionZone.Shape),
                    "recording_action");
                lock (_gate) _learnedAoeCache[cacheKey] = v;
                return v;
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[FfxivEchoes] AddObjectAoe: 録画 action lookup 失敗 name={Name}", name);
        }

        // 4. 録画から「この NPC の出現直後のアクション」を学習し、action lookup で半径＋形状を引く。
        if (_recordings is not null && _actionLookup is not null && dataId != 0)
        {
            lock (_gate)
            {
                // 既に「学習できない」と判明済みなら全録画フルスキャンを再実行しない。
                if (_firstCastLearnMisses.Contains(cacheKey)) return null;
            }
            try
            {
                var candidates = _recordings.FindNpcActionCandidatesAfterAppearance(_currentZone, dataId, name);
                if (SelectRecordingActionCandidate(candidates) is { } found)
                {
                    var aoe = AoeResolver.Resolve(_actionLookup, found.ActionId, _log);
                    if (aoe is not null && aoe.Radius > 0)
                    {
                        var zone = CreateAoeZoneFromResolvedAction(aoe, name);
                        PersistLearnedObjectRule(file, dataId, name, zone, "recording");
                        var v = new LearnedAoe(
                            zone,
                            ObjectAoeRuleResolver.ShapeNoteFor(zone.Shape),
                            "recording");
                        lock (_gate) _learnedAoeCache[cacheKey] = v;
                        _log.Information(
                            "[FfxivEchoes] AddObjectAoe 学習: {Name} (dataId={DataId}) → action 0x{ActionId:X4} radius={R}m castType={Ct}",
                            name, dataId, found.ActionId, aoe.Radius, aoe.CastType);
                        return v;
                    }
                }
                else if (candidates.Count > 1)
                {
                    _log.Information(
                        "[FfxivEchoes] AddObjectAoe 学習保留: {Name} dataId={DataId} は複数 action 候補があるため自動固定しません ({Count} candidates)",
                        name, dataId, candidates.Count);
                }

                // 候補なし／自動固定保留に至った。結果は決定的なので負キャッシュし、出現や
                // 0.5 秒ライブスキャン窓ごとの全録画フルスキャン再実行を止める。
                lock (_gate)
                {
                    _firstCastLearnMisses.Add(cacheKey);
                }
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "[FfxivEchoes] AddObjectAoe: 録画学習失敗 name={Name}", name);
            }
        }

        return null;
    }

    private bool TryBuildRecordedActionAoe(
        string name,
        AutoAoeArenaConfig arena,
        out StrategyAoeZone zone)
    {
        zone = default!;
        if (_recordings is null || _actionLookup is null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (!ObjectAoeRuleResolver.ShouldPersistLearnedObjectRule(
                _store.GetByZone(_currentZone),
                name,
                "recording_action"))
        {
            lock (_gate)
            {
                _recordedActionAoeMisses.Add(name);
            }
            _log.Information(
                "[FfxivEchoes] AddObjectAoe: {Name} は既知の発動元 actor のため recording_action 学習を無視します",
                name);
            return false;
        }

        lock (_gate)
        {
            if (_recordedActionAoeCache.TryGetValue(name, out var cached))
            {
                zone = cached.Zone;
                return true;
            }
            if (_recordedActionAoeMisses.Contains(name))
            {
                return false;
            }
        }

        AggregatedEvents agg;
        try
        {
            agg = _recordings.Aggregate(_currentZone);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[FfxivEchoes] AddObjectAoe: 録画 aggregate 失敗 zone={Zone}", _currentZone);
            return false;
        }

        foreach (var candidate in SelectRecordedObjectActionCandidates(agg, name))
        {
            if (AutoSafeCallPlanner.IsRaidWide(_store.GetByZone(_currentZone), candidate.ActionId, candidate.ActionName))
            {
                continue;
            }

            var aoe = AoeResolver.Resolve(_actionLookup, candidate.ActionId, _log);
            if (aoe is null || aoe.Radius <= 0)
            {
                continue;
            }

            zone = CreateAoeZoneFromResolvedAction(aoe, name);
            zone.DurationSec ??= DefaultDisplayDurationSec;
            var learned = new LearnedAoe(
                zone,
                ObjectAoeRuleResolver.ShapeNoteFor(zone.Shape),
                "recording_action");
            lock (_gate)
            {
                _recordedActionAoeCache[name] = learned;
            }
            _log.Information(
                "[FfxivEchoes] AddObjectAoe 録画 action 学習: {Name} → action 0x{ActionId:X4} ({ActionName})",
                name, candidate.ActionId, candidate.ActionName ?? "?");
            return true;
        }

        lock (_gate)
        {
            _recordedActionAoeMisses.Add(name);
        }
        return false;
    }

    public static IReadOnlyList<RecordedObjectActionCandidate> SelectRecordedObjectActionCandidates(
        AggregatedEvents? aggregate,
        string objectName,
        string? excludedSourceName = null)
    {
        if (aggregate is null || string.IsNullOrWhiteSpace(objectName))
        {
            return Array.Empty<RecordedObjectActionCandidate>();
        }

        var name = objectName.Trim();
        var excluded = excludedSourceName?.Trim();
        return aggregate.Events
            .Where(ev => string.Equals(ev.Key.Type, "action_used", StringComparison.OrdinalIgnoreCase))
            .Where(ev => !string.IsNullOrWhiteSpace(ev.Key.Source))
            .Where(ev => string.IsNullOrWhiteSpace(excluded) ||
                         !string.Equals(ev.Key.Source, excluded, StringComparison.OrdinalIgnoreCase))
            .Where(ev => MatchesObjectSource(ev.Key.Source, name))
            .Select(ev => AoeResolver.TryParseCastId(ev.Key.Id ?? string.Empty, out var actionId)
                ? new RecordedObjectActionCandidate(actionId, ev.Count, ev.Key.Name)
                : null)
            .Where(candidate => candidate is not null)
            .Cast<RecordedObjectActionCandidate>()
            .OrderByDescending(candidate => candidate.ObservationCount)
            .ThenBy(candidate => candidate.ActionId)
            .ToArray();
    }

    /// <summary>
    /// 録画 action_used の Source 名がオブジェクト名に一致するか。完全一致は常に許可。
    /// 部分一致は両者が 3 文字以上のときのみ（双方向）。短い名前（例 "炎"）が無関係な長い名前に
    /// 誤マッチして AoE 形状をでたらめに学習する事故を防ぐ（RES-04）。
    /// </summary>
    public static bool MatchesObjectSource(string? recordedSource, string? objectName)
    {
        if (string.IsNullOrWhiteSpace(recordedSource) || string.IsNullOrWhiteSpace(objectName))
        {
            return false;
        }
        if (string.Equals(recordedSource, objectName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        const int MinPartialLen = 3;
        if (recordedSource.Length < MinPartialLen || objectName.Length < MinPartialLen)
        {
            return false;
        }
        return recordedSource.Contains(objectName, StringComparison.OrdinalIgnoreCase)
            || objectName.Contains(recordedSource, StringComparison.OrdinalIgnoreCase);
    }

    private static (uint ActionId, int ObservationCount)? SelectRecordingActionCandidate(
        IReadOnlyList<(uint ActionId, int ObservationCount)> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var top = candidates[0];
        var second = candidates[1];
        return top.ObservationCount >= second.ObservationCount * 2
            ? top
            : null;
    }

    private bool TryBuildDictionaryAoe(
        string name,
        AutoAoeArenaConfig arena,
        out StrategyAoeZone zone)
    {
        zone = default!;
        var hit = _dictionary?.Lookup(0, name);
        if (hit is null || hit.RaidWide)
        {
            return false;
        }

        var safeCall = new AutoSafeCall(
            hit.Gimmick,
            hit.Callout ?? name,
            hit.Tts ?? hit.Callout ?? name,
            "#FF6464",
            IsEstimate: false,
            hit.FanDeg);
        if (KnownAoeGeometry.TryCreate(safeCall, hit.AoeRadiusM, arena, out var spec))
        {
            zone = ObjectAoeRuleResolver.BuildZoneFromKnownGeometry(spec, name);
            return true;
        }

        if (hit.AoeRadiusM is { } radius && radius > 0)
        {
            zone = new StrategyAoeZone
            {
                Label = name,
                Shape = "circle",
                Anchor = "each_matched_object",
                RadiusM = radius,
                Color = "#FF6464",
                IsDanger = true,
                LiveFloorPaint = true,
                SuppressAutoAoe = true,
            };
            return true;
        }

        return false;
    }

    private void PersistLearnedObjectRule(
        TriggerFile? file,
        uint dataId,
        string name,
        StrategyAoeZone zone,
        string source)
    {
        if (file is null || string.IsNullOrWhiteSpace(_currentZone))
        {
            return;
        }

        var profile = StrategyPlanResolver.SelectActiveProfile(file) ?? file.StrategyProfiles.FirstOrDefault();
        if (profile is null)
        {
            return;
        }

        if (!ObjectAoeRuleResolver.ShouldPersistLearnedObjectRule(file, name, source))
        {
            _log.Information(
                "[FfxivEchoes] AddObjectAoe: {Name} は既知の発動元 actor のため object AoE ルール保存をスキップします source={Source}",
                name, source);
            return;
        }

        // ユーザーが手動で無効化したルールも「同一オブジェクトに対するルールは既にある」と見なす。
        // Matches を使うと Enabled=false のルールを「存在しない」扱いしてしまい、
        // recording_action 学習のたびに新規 active ルールが量産される（ユーザー体感「無効化したのに AoE がまた出る」）。
        if (profile.ObjectAoeRules.Any(rule => ObjectAoeRuleResolver.MatchesIgnoringEnabled(rule, dataId, name)))
        {
            return;
        }

        // 名前ベースの強化重複防止：同名 (object_name 一致) のルールが既存なら、DataId 違いでも
        // 重複扱いで skip する。
        // 動機：月の底のゾディアーク add (data_id=9020) が変身演出で actor.Name が
        // 「ケツァクウァトル」に変わり、ケラノウス・エイドロン (0x67E1) を発動。これを学習すると
        // dataId=9020 で PersistLearnedObjectRule が呼ばれ、既存ルール (DataId=14388 = 本物の
        // ケツァクウァトル本体) と MatchesIgnoringEnabled で DataId mismatch と判定 →
        // 重複扱いされず新規ルール (data_id 省略、半径 15m) が量産され、変身体 4 体に
        // 誤発火して画面中央に正体不明のドーナツが出る regression が起きていた。
        // 名前一致を優先することで、同名 actor の DataId バリエーションを 1 ルールに統合する。
        var nameTrimmed = name?.Trim();
        if (!string.IsNullOrEmpty(nameTrimmed))
        {
            if (profile.ObjectAoeRules.Any(rule =>
                !string.IsNullOrWhiteSpace(rule.ObjectName) &&
                string.Equals(rule.ObjectName!.Trim(), nameTrimmed, StringComparison.OrdinalIgnoreCase)))
            {
                _log.Information(
                    "[FfxivEchoes] AddObjectAoe: 同名ルール既存のため学習スキップ name={Name} learnedDataId={DataId}",
                    nameTrimmed, dataId);
                return;
            }
        }

        var rule = ObjectAoeRuleResolver.CreateLearnedRule(dataId, name, zone, source);
        profile.ObjectAoeRules.Add(rule);
        try
        {
            _store.SaveZone(_currentZone, file);
            _log.Information(
                "[FfxivEchoes] AddObjectAoe rule saved: {Name} dataId={DataId} shape={Shape} radius={Radius} source={Source}",
                name, dataId, rule.Shape, rule.RadiusM, source);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] AddObjectAoe rule 保存失敗 name={Name}", name);
        }
    }

    private void OnAppear(ObjectAppearedEvent ev)
    {
        // 戦闘開始前のオブジェクト出現は無視。pre-combat でゾーン入場時に既に object table に
        // 存在しているギミック用 NPC（ケツァクウァトル ×4 など）で AoE が早出ししないようにする。
        // 戦闘開始時に ObjectCapture が _seen をクリア → 全オブジェクトを再 publish するため、
        // 戦闘開始直後の resnap appear が改めてここに届く（その時点で _inCombat=true）。
        if (!_inCombat)
        {
            return;
        }

        // DataId が 0 でも Name があれば追跡する（FFXIV ではダミー DataId の add がある）。
        if (ev.DataId == 0 && string.IsNullOrWhiteSpace(ev.ObjectName))
        {
            return;
        }

        var file = _store.GetByZone(_currentZone);
        var arena = AutoAoeDisplayPolicy.ResolveArena(file);
        if (!IsUsableObjectAoePosition(ev.Position, arena.LockedArenaCenter, arena.ArenaRadius))
        {
            return;
        }

        var key = MakeGroupKey(ev.DataId, ev.ObjectName);
        var now = ev.Timestamp;
        lock (_gate)
        {
            CleanupExpiredGroups(now);

            if (!_groups.TryGetValue(key, out var group))
            {
                group = new GroupState(key, ev.DataId, ev.ObjectName);
                _groups[key] = group;
            }
            group.Add(ev.DataId, ev.EntityId ?? ev.ObjectId, ev.Position, now, ev.ObjectName);
        }

        // 単体オブジェクト AoE は「学習済みか」を見てから発火可否を決める。
        // 録画から初めて学習するケースでは cache がまだ空なので、出現時点で温めておく。
        _ = LookupAoe(ev.DataId, ev.ObjectName);
    }

    private void OnObjectDisappeared(ObjectDisappearedEvent ev)
    {
        // 消滅した add の suppress を解除し、同名 add が再出現したときに AoE が再び描画されるようにする。
        // ObjectDisappearedEvent は DataId を持たないため名前ベースのキーで照合する
        // （OnAppear / FireGroup の suppress キーも名前があれば "name:{name}" 形式で一致する）。
        if (string.IsNullOrWhiteSpace(ev.ObjectName))
        {
            return;
        }
        var key = MakeGroupKey(0, ev.ObjectName);
        lock (_gate)
        {
            _objectAoeSuppressUntil.Remove(key);
        }
    }

    private void OnCastStart(CastStartedEvent ev)
    {
        var file = _store.GetByZone(_currentZone);
        if (!AutoAoeDisplayPolicy.IsEnabled(file)) return;
        if (AutoSafeCallPlanner.IsRaidWide(file, ev.CastActionId, ev.CastActionName)) return;

        RememberRecentCastSource(ev.SourceName, ev.Timestamp);
        ScheduleLiveObjectScan(ev.SourceName, ev.Timestamp, Math.Max(LiveObjectScanDurationSec, ev.CastTime + 8.0));
    }

    private void OnCastComplete(CastCompletedEvent ev)
    {
        var file = _store.GetByZone(_currentZone);
        if (!AutoAoeDisplayPolicy.IsEnabled(file)) return;
        if (AutoSafeCallPlanner.IsRaidWide(file, ev.CastActionId, ev.CastActionName)) return;

        ScheduleLiveObjectScan(ev.SourceName, ev.Timestamp, 8.0);
    }

    private void OnActionUsed(ActionUsedEvent ev)
    {
        var file = _store.GetByZone(_currentZone);
        if (!AutoAoeDisplayPolicy.IsEnabled(file)) return;
        if (_actionLookup is null || _objectTable is null) return;
        var isRaidWide = AutoSafeCallPlanner.IsRaidWide(file, ev.ActionId, ev.ActionName);
        if (isRaidWide) return;

        var src = _objectTable.FindByEntityOrObjectId(ev.SourceId);
        var actionIsPlayerAction = IsPlayerAction(ev.ActionId) ||
                                   PcSkillNameFilter.LooksLikePcSkillOrPet(ev.ActionName, "action_used");
        var sourceIsPlayerOwned = ev.IsPlayer || actionIsPlayerAction || IsPlayerOwnedSource(src);

        var name = string.IsNullOrWhiteSpace(ev.SourceName)
            ? src?.Name.TextValue ?? string.Empty
            : ev.SourceName;
        var recentCastSource = !string.IsNullOrWhiteSpace(name) && IsRecentCastSource(name, ev.Timestamp);
        if (!recentCastSource &&
            !string.IsNullOrWhiteSpace(name) &&
            ShouldScheduleLiveObjectScanForInstantAction(
                ev,
                AutoAoeDisplayPolicy.IsEnabled(file),
                sourceIsPlayerOwned,
                isRaidWide,
                actionIsPlayerAction))
        {
            ScheduleLiveObjectScan(name, ev.Timestamp, LiveObjectScanDurationSec);
        }

        if (!sourceIsPlayerOwned && !ev.IsAutoAttack && !isRaidWide && !string.IsNullOrWhiteSpace(name))
        {
            RememberRecentCastSource(name, ev.Timestamp);
        }

        if (ev.IsAutoAttack) return;
        if (sourceIsPlayerOwned) return;
        if (src is null) return;
        if (string.IsNullOrWhiteSpace(name)) return;
        if (recentCastSource) return;
        if (src is IBattleNpc)
        {
            return;
        }
        if (!ObjectAoeRuleResolver.ShouldPersistLearnedObjectRule(file, name, "recording_action"))
        {
            _log.Information(
                "[FfxivEchoes] AddObjectAoe: {Name} は既知の発動元 actor のため action_used 由来の object AoE 学習を保存しません",
                name);
            return;
        }

        var aoe = AoeResolver.Resolve(_actionLookup, ev.ActionId, _log);
        if (aoe is null || aoe.Radius <= 0) return;

        var zone = CreateAoeZoneFromResolvedAction(aoe, name, src.HitboxRadius);
        zone.DurationSec ??= DefaultDisplayDurationSec;
        PersistLearnedObjectRule(file, src.BaseId, name, zone, "recording_action");
        lock (_gate)
        {
            _recordedActionAoeCache[name] = new LearnedAoe(
                zone,
                ObjectAoeRuleResolver.ShapeNoteFor(zone.Shape),
                "recording_action");
            _recordedActionAoeMisses.Remove(name);
        }
    }

    public static bool ShouldScheduleLiveObjectScanForInstantAction(
        ActionUsedEvent ev,
        bool autoAoeEnabled,
        bool sourceIsPlayerOwned,
        bool isRaidWide,
        bool actionIsPlayerAction = false)
    {
        return autoAoeEnabled &&
               !ev.IsAutoAttack &&
               !ev.IsPlayer &&
               !sourceIsPlayerOwned &&
               !actionIsPlayerAction &&
               !isRaidWide;
    }

    public static bool ShouldDrawLiveObjectGroup(int memberCount)
    {
        return memberCount > 0 && memberCount <= MaxLiveObjectAoeMembers;
    }

    private bool IsPlayerOwnedSource(IGameObject? obj)
    {
        if (obj is null)
        {
            return false;
        }

        if (obj.ObjectKind == ObjectKind.Pc)
        {
            return true;
        }

        if (obj is IBattleNpc bnpc && bnpc.OwnerId != 0)
        {
            var owner = _objectTable?.FindByEntityOrObjectId(bnpc.OwnerId);
            return owner is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter;
        }

        return false;
    }

    private bool IsPlayerAction(uint actionId)
    {
        if (_actionLookup is null || actionId == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_playerActionCache.TryGetValue(actionId, out var cached))
            {
                return cached;
            }
        }

        var result = false;
        try
        {
            var geom = _actionLookup.TryGet(actionId);
            if (geom is not null)
            {
                result = geom.IsPlayerAction;
            }
        }
        catch (Exception ex)
        {
            _log.Verbose(ex, "[FfxivEchoes] AddObjectAoe: player action 判定失敗 id={Id}", actionId);
        }

        lock (_gate)
        {
            _playerActionCache[actionId] = result;
        }
        return result;
    }

    private void RememberRecentCastSource(string? sourceName, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return;
        }

        lock (_gate)
        {
            _recentCastSourceNames[sourceName.Trim()] = now.AddSeconds(20);
        }
    }

    private bool IsRecentCastSource(string sourceName, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var key in _recentCastSourceNames
                         .Where(kv => kv.Value <= now)
                         .Select(kv => kv.Key)
                         .ToArray())
            {
                _recentCastSourceNames.Remove(key);
            }

            return _recentCastSourceNames.TryGetValue(sourceName.Trim(), out var expiresAt) &&
                   expiresAt > now;
        }
    }

    private void ScheduleLiveObjectScan(string sourceName, DateTimeOffset now, double durationSec)
    {
        if (_objectTable is null)
        {
            return;
        }

        var excluded = string.IsNullOrWhiteSpace(sourceName) ? string.Empty : sourceName.Trim();
        lock (_gate)
        {
            _liveScanWindows.Add(new LiveScanWindow(
                excluded,
                now.AddSeconds(Math.Max(1.0, durationSec)),
                now));
        }
    }

    private void OnUpdate(IFramework _)
    {
        try
        {
            OnUpdateCore();
        }
        catch (Exception ex)
        {
            FrameErrorThrottle.Report(_log, ex, "AddObjectAoeService.OnUpdate");
        }
    }

    private void OnUpdateCore()
    {
        var now = DateTimeOffset.UtcNow;
        var file = _store.GetByZone(_currentZone);
        List<GroupState>? toFire = null;

        lock (_gate)
        {
            CleanupExpiredGroups(now);
            if (!AutoAoeDisplayPolicy.IsEnabled(file))
            {
                _groups.Clear();
                return;
            }

            foreach (var (key, group) in _groups)
            {
                // 発火条件：複数体（既定 ≥2）、または学習済みの単発 NPC 1 体でも発火
                var hasLearned = HasLearnedAoeUnsafe(file, group.DataId, group.Name);
                if (!AutoAoeDisplayPolicy.ShouldDrawObjectGroup(
                        file,
                        group.Members.Count,
                        hasLearned,
                        MinGroupSize))
                {
                    continue;
                }
                if ((now - group.LastAddAt).TotalSeconds < StabilityDelaySec) continue;

                if (_objectAoeSuppressUntil.TryGetValue(key, out var suppressUntil) &&
                    IsObjectAoeSuppressed(now, suppressUntil))
                {
                    continue;
                }
                toFire ??= new List<GroupState>();
                toFire.Add(group);
                group.MarkFired();
            }
        }

        if (toFire is not null)
        {
            foreach (var group in toFire)
            {
                try
                {
                    FireGroup(group);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[FfxivEchoes] AddObjectAoe: 描画失敗 key={Key}", group.Key);
                }
            }
        }

        FirePendingLiveObjectScans(now);
    }

    private void FirePendingLiveObjectScans(DateTimeOffset now)
    {
        // pre-combat 防御線：LiveScan は OnCastStart/OnCastComplete/OnActionUsed 起点で予約されるが、
        // ボスや add の pre-combat キャスト・action でスケジュールされた window が走ると、
        // OnAppear をゲートしていても TryFireLiveObjectSnapshot で AoE が出てしまう。発火は戦闘中限定にする。
        if (!_inCombat)
        {
            return;
        }

        if (_objectTable is null)
        {
            return;
        }

        List<LiveScanWindow>? due = null;
        lock (_gate)
        {
            _liveScanWindows.RemoveAll(w => w.ExpiresAt <= now);
            foreach (var window in _liveScanWindows)
            {
                if (window.NextScanAt > now)
                {
                    continue;
                }
                window.NextScanAt = now.AddSeconds(LiveObjectScanIntervalSec);
                due ??= new List<LiveScanWindow>();
                due.Add(window);
            }
        }

        if (due is null)
        {
            return;
        }

        foreach (var window in due)
        {
            // toFire パス（FireGroup）と保護水準を対称にする。LiveScan は ObjectTable を走査するため、
            // 絶コンテンツの大量 add 出現/消滅でアクター解放と重なると例外が出うる。
            // 例外境界が無いと IFramework.Update 経由で Dalamud フレームループに伝播する。
            try
            {
                TryFireLiveObjectSnapshot(window, now);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] AddObjectAoe: LiveScan 例外 excludedSource={Source}", window.ExcludedSourceName);
            }
        }
    }

    private void TryFireLiveObjectSnapshot(LiveScanWindow window, DateTimeOffset now)
    {
        var file = _store.GetByZone(_currentZone);
        if (!AutoAoeDisplayPolicy.IsEnabled(file) || _objectTable is null)
        {
            return;
        }

        var arena = AutoAoeDisplayPolicy.ResolveArena(file);
        var groups = new Dictionary<string, List<Member>>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in _objectTable)
        {
            if (obj.ObjectKind == ObjectKind.Pc)
            {
                continue;
            }

            var name = obj.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(window.ExcludedSourceName) &&
                string.Equals(name, window.ExcludedSourceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsRecentCastSource(name, now))
            {
                continue;
            }

            var pos = new Vector3(obj.Position.X, obj.Position.Y, obj.Position.Z);

            // FFXIV: actor が ObjectTable に登録された直後（戦闘開始の resnap 等）は
            // 位置が実位置に初期化されておらず、ゾーン共通の placeholder 座標 (100, *, 100)
            // が返る。ObjectCapture.OnUpdate ではこれを skip して ObjectAppearedEvent を
            // 発行しないが、TryFireLiveObjectSnapshot は ObjectTable を直接走査するため
            // 同じフィルタを通らず、placeholder 位置の actor が発火対象になる。
            // 月の底のケツァクウァトル 4 体が time=32 出現より前のパラデイグマ詠唱中
            // (戦闘 time≒18s) に「AddObjectAoe fired: ケツァクウァトル ×4 zones=4」が
            // 発火し、4 体すべて (100, 100) placeholder で誤描画される regression の原因。
            // ObjectCapture と同じ判定を借りて placeholder actor を skip する。
            if (Capture.ObjectCapture.IsUninitializedPlaceholderPosition(pos))
            {
                continue;
            }

            if (!IsUsableObjectAoePosition(pos, arena.LockedArenaCenter, arena.ArenaRadius))
            {
                continue;
            }

            var learned = LookupAoe(obj.BaseId, name);
            if (learned is null)
            {
                continue;
            }

            if (!groups.TryGetValue(name, out var list))
            {
                list = new List<Member>();
                groups.Add(name, list);
            }
            list.Add(new Member(obj.BaseId, obj.EntityId, name, pos, now));
        }

        foreach (var (name, members) in groups)
        {
            if (!ShouldDrawLiveObjectGroup(members.Count))
            {
                _log.Debug(
                    "[FfxivEchoes] AddObjectAoe live scan skip oversized group: {Name} ×{Count}",
                    name, members.Count);
                continue;
            }

            var key = MakeLiveObjectAoeSuppressKey(members[0].DataId, name);
            lock (_gate)
            {
                if (_objectAoeSuppressUntil.TryGetValue(key, out var suppressUntil) &&
                    IsObjectAoeSuppressed(now, suppressUntil))
                {
                    continue;
                }
            }

            var group = new GroupState(key, members[0].DataId, name);
            foreach (var member in members)
            {
                group.Add(member.DataId, member.EntityId, member.Pos, now, member.Name);
            }
            FireGroup(group);
        }
    }

    /// <summary>
    /// グループキーごと（≒ NPC 名）に「Lumina/録画/辞書から半径が分かっているか」をチェック。
    /// 1 体のみでも発火させてよいかの判定に使う。lock 済前提。
    /// </summary>
    private bool HasLearnedAoeUnsafe(TriggerFile? file, uint dataId, string name)
    {
        if (ObjectAoeRuleResolver.TryResolve(file, dataId, name, AutoAoeDisplayPolicy.ResolveArena(file), out _))
        {
            return true;
        }

        if (_learnedAoeCache.TryGetValue(MakeCacheKey(dataId, name), out _) ||
            _learnedAoeCache.TryGetValue(MakeCacheKey(0, name), out _))
        {
            return true;
        }

        // 辞書 override があれば学習済み扱い
        try
        {
            if (TryBuildDictionaryAoe(name, AutoAoeDisplayPolicy.ResolveArena(file), out _)) return true;
        }
        catch { /* lookup 失敗は無視 */ }
        return false;
    }

    private void CleanupExpiredGroups(DateTimeOffset now)
    {
        // 毎フレーム（OnUpdate の lock 内）呼ばれるため new List を避けて再利用バッファを使う（NEW-08）。
        _deadGroupKeys.Clear();
        foreach (var (key, group) in _groups)
        {
            if (group.IsFired) { _deadGroupKeys.Add(key); continue; }
            if ((now - group.FirstAddAt).TotalSeconds > WindowSec)
            {
                _deadGroupKeys.Add(key);
            }
        }
        foreach (var k in _deadGroupKeys)
        {
            _groups.Remove(k);
        }
    }

    public static DateTimeOffset ComputeObjectAoeSuppressUntil(
        DateTimeOffset firedAt,
        double displayDurationSec)
    {
        var duration = displayDurationSec > 0 ? displayDurationSec : DefaultDisplayDurationSec;
        return firedAt.AddSeconds(Math.Max(DedupSec, duration));
    }

    public static bool IsObjectAoeSuppressed(DateTimeOffset now, DateTimeOffset? suppressUntil)
    {
        return suppressUntil is { } until && until > now;
    }

    private void FireGroup(GroupState group)
    {
        var file = _store.GetByZone(_currentZone);
        var arena = AutoAoeDisplayPolicy.ResolveArena(file);
        var zones = new List<StrategyAoeZone>(group.Members.Count);
        var shapeNotes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in group.Members)
        {
            var memberName = string.IsNullOrWhiteSpace(member.Name) ? group.Name : member.Name;
            var learned = LookupAoe(member.DataId, memberName);
            if (learned is null)
            {
                continue;
            }

            // 全体攻撃ガード：学習済み zone の半径がアリーナ半径とほぼ同じ以上なら、
            // 4 体出現時に各位置中心で巨大ドーナツが重なって「中央に正体不明の安置エリア」
            // が出る現象（ユーザー報告「画面中央にケツァクウァトル AoE が表示される」）を防ぐ。
            // AutoTelegraphService / ActorTrackedAoeService / MinimapWindow の 0.9 ガードと
            // 揃える。半径不明 (0 or null) の zone は通す（既存挙動）。
            if (learned.Value.Zone.RadiusM is { } r && r > 0 &&
                arena.ArenaRadius > 0 && r >= arena.ArenaRadius * 0.9)
            {
                _log.Information(
                    "[FfxivEchoes] AddObjectAoe: skip oversized {Name} r={R}m arena={A}m",
                    memberName, r, arena.ArenaRadius);
                continue;
            }

            // OnAppear 時点の position は出現直後の placeholder / 仮位置の場合があるため、
            // 発火タイミングで ObjectTable から actor の現在位置を取り直す。
            // 例：ゾディアーク add (data_id=9020) は (100, 0, 79) で出現後にケツァクウァトル化
            // して別位置でアクション発動するため、出現時の position だと AoE が中央に集中誤描画される。
            var actor = _objectTable?.SearchByEntityId(member.EntityId);
            var livePos = actor is not null
                ? new Vector3(actor.Position.X, actor.Position.Y, actor.Position.Z)
                : member.Pos;
            if (!IsUsableObjectAoePosition(livePos, arena.LockedArenaCenter, arena.ArenaRadius))
            {
                continue;
            }

            zones.Add(BuildStaticAoeZone(learned.Value.Zone, livePos, arena.LockedArenaCenter));
            shapeNotes.Add(learned.Value.ShapeNote);
        }

        if (!AutoAoeDisplayPolicy.ShouldDrawObjectGroup(
                file,
                zones.Count,
                hasLearnedAoe: zones.Count > 0,
                minGroupSize: MinGroupSize))
        {
            _log.Information(
                "[FfxivEchoes] AddObjectAoe: {Name} ×{Count} — 半径/形状未学習のため描画しません",
                group.Name, group.Members.Count);
            return;
        }

        if (!ShouldDrawLiveObjectGroup(zones.Count))
        {
            _log.Information(
                "[FfxivEchoes] AddObjectAoe: {Name} ×{Count} — 同名候補が多すぎるため描画しません",
                group.Name, zones.Count);
            return;
        }

        var positions = group.Members.Select(m => m.Pos).ToArray();
        var shapeNote = shapeNotes.Count == 1 ? shapeNotes.First() : "AoE";
        var callout = zones.Count >= 3
            ? $"{group.Name} ×{zones.Count} {shapeNote}"
            : $"{group.Name} {shapeNote}";
        var duration = zones
            .Select(z => z.DurationSec ?? DefaultDisplayDurationSec)
            .DefaultIfEmpty(DefaultDisplayDurationSec)
            .Max();

        var action = new ActionDefinition
        {
            Type = "arena_view",
            Callout = callout,
            Duration = duration,
            ArenaRadius = arena.ArenaRadius,
            ArenaShape = arena.ArenaShape,
            ArenaWidth = arena.ArenaWidth,
            ArenaDepth = arena.ArenaDepth,
            ArenaCenterX = arena.LockedArenaCenter?.X,
            ArenaCenterZ = arena.LockedArenaCenter?.Z,
            AoeZones = zones,
        };
        var firedAt = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _objectAoeSuppressUntil[group.Key] = ComputeObjectAoeSuppressUntil(firedAt, duration);
        }

        _bus.Publish(new TriggerFiredEvent(
            Timestamp: firedAt,
            Zone: _currentZone,
            TriggerId: $"__auto_object_aoe_{group.Key}",
            TriggerName: callout,
            Actions: new[] { action },
            SourceEvent: new ObjectGroupAppearedEvent(
                firedAt,
                group.Name,
                group.DataId,
                positions.Length,
                positions)));

        _log.Information(
            "[FfxivEchoes] AddObjectAoe fired: {Name} ×{Count} zones={Zones}",
            group.Name, positions.Length, zones.Count);
    }

    public static bool TryBuildUnknownObjectFallbackAoeZone(
        AutoAoeArenaConfig arena,
        out StrategyAoeZone? zone)
    {
        _ = arena;
        zone = null;
        return false;
    }

    public static bool ShouldUseFallbackCohort(
        TriggerFile? file,
        int objectCount,
        bool namedGroupWillFire,
        double secondsSinceCast)
    {
        // Splatoon と同じく、表示する Element は actor/cast/object effect などの
        // 根拠に紐づける。短時間に出た「別名オブジェクトの集合」だけから巨大な
        // 推定直線を作ると、背景オブジェクトや演出用オブジェクトを誤検出する。
        _ = file;
        _ = objectCount;
        _ = namedGroupWillFire;
        _ = secondsSinceCast;
        return false;
    }

    /// <summary>アリーナ半径に対する「外側 placeholder」判定の係数。</summary>
    /// <remarks>
    /// 月の底ゾディアーク add の戦闘開始時 placeholder 位置 (100, 0, 79) はアリーナ中心
    /// (100.7, 102.1) から ~23m 離れる（アリーナ半径 20m 想定）。これを「マップ外」とみなし
    /// 描画から除外する。1.5 倍は「ロケーション端のオブジェクトは正常」を確保する余裕。
    /// </remarks>
    private const double OutOfArenaPlaceholderMultiplier = 1.5;

    public static bool IsUsableObjectAoePosition(
        Vector3 worldPosition,
        Vector3? lockedCenter,
        double? arenaRadiusM = null)
    {
        if (MathF.Abs(worldPosition.X) < 0.001f &&
            MathF.Abs(worldPosition.Z) < 0.001f)
        {
            return false;
        }

        if (lockedCenter is { } center)
        {
            var dx = worldPosition.X - center.X;
            var dz = worldPosition.Z - center.Z;
            var dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist < PlaceholderCenterDistanceM)
            {
                return false;
            }

            // アリーナ半径が分かっている場合、明らかに外側（半径 × 1.5 を超える）の
            // 座標は「初期 placeholder のまま actor が未着座」とみなして除外する。
            // 月の底ゾディアーク add の戦闘開始時 (100,79) はここで弾かれる。
            if (arenaRadiusM is { } radius && radius > 0 &&
                dist > radius * OutOfArenaPlaceholderMultiplier)
            {
                return false;
            }
        }

        return true;
    }

    public static StrategyAoeZone CreateAoeZoneFromResolvedAction(
        AoeResolver.AoeInfo aoe,
        string? label,
        float sourceHitboxRadius = 0f)
    {
        var radius = AoeResolver.EffectiveRadius(aoe, sourceHitboxRadius);
        var zone = new StrategyAoeZone
        {
            Label = label,
            Shape = ShapeForResolvedAoe(aoe),
            Anchor = "each_matched_object",
            RadiusM = radius,
            IsDanger = true,
            Color = "#FF6464",
            LiveFloorPaint = true,
            SuppressAutoAoe = true,
            IncludeHitbox = aoe.IncludeCasterHitbox,
        };

        if (AoeResolver.IsDonutShape(aoe.CastType, aoe.OmenId))
        {
            zone.InnerRadiusM = radius * AoeResolver.DonutInnerRatio(aoe.OmenId);
        }
        else if (aoe.CastType is 3 or 13)
        {
            zone.FanDeg = 90;
        }
        else if (aoe.CastType is 4 or 12)
        {
            zone.HalfWidthM = AoeGeometryPolicy.DefaultLineHalfWidthM;
        }

        return zone;
    }

    public static StrategyAoeZone BuildStaticAoeZone(
        StrategyAoeZone template,
        Vector3 worldPosition,
        Vector3? lockedCenter)
    {
        var center = lockedCenter ?? Vector3.Zero;
        var x = worldPosition.X - center.X + (float)template.X;
        var z = worldPosition.Z - center.Z + (float)template.Z;
        var rotationDeg = template.RotationDeg;
        if (rotationDeg is null && ShouldPointToArenaCenter(template.Shape))
        {
            rotationDeg = AoeGeometryPolicy.RotationDegTowardsArenaCenter(x, z);
        }

        return new StrategyAoeZone
        {
            Id = template.Id,
            Label = template.Label,
            Shape = template.Shape,
            X = x,
            Z = z,
            RadiusM = template.RadiusM,
            InnerRadiusM = template.InnerRadiusM,
            RotationDeg = rotationDeg,
            FanDeg = template.FanDeg,
            HalfWidthM = template.HalfWidthM,
            IncludeHitbox = template.IncludeHitbox,
            RotationOffsetDeg = template.RotationOffsetDeg,
            Color = template.Color,
            IsDanger = template.IsDanger,
            Anchor = "static",
            AnchorWaymark = template.AnchorWaymark,
            ActorMatcher = template.ActorMatcher,
            StateFilter = template.StateFilter,
            RotationSource = template.RotationSource,
            LiveFloorPaint = template.LiveFloorPaint,
            DurationSec = template.DurationSec,
            SuppressAutoAoe = template.SuppressAutoAoe,
        };
    }

    private static bool ShouldPointToArenaCenter(string? shape)
    {
        return ObjectAoeRuleResolver.NormalizeShape(shape) switch
        {
            "cone" or "rect" or "line" or "cross" or "half_plane" or "donut_cone" => true,
            _ => false,
        };
    }

    private static string ShapeForResolvedAoe(AoeResolver.AoeInfo aoe)
    {
        if (AoeResolver.IsDonutShape(aoe.CastType, aoe.OmenId))
        {
            return "donut";
        }

        if (string.Equals(AoeResolver.GuessGimmickByOmen(aoe.OmenId), "half_plane", StringComparison.OrdinalIgnoreCase))
        {
            return "half_plane";
        }

        return ShapeForCastType(aoe.CastType);
    }

    private static string ShapeForCastType(int castType)
    {
        if (AoeResolver.IsDonutShape(castType, 0))
        {
            return "donut";
        }

        return castType switch
        {
            3 or 13 => "cone",
            4 or 12 => "rect",
            11 => "cross",
            _ => "circle",
        };
    }

    private sealed class GroupState
    {
        public string Key { get; }
        public uint DataId { get; }
        public string Name { get; private set; }
        public DateTimeOffset FirstAddAt { get; private set; }
        public DateTimeOffset LastAddAt { get; private set; }
        public bool IsFired { get; private set; }
        public List<Member> Members { get; } = new();

        public GroupState(string key, uint dataId, string name)
        {
            Key = key;
            DataId = dataId;
            Name = name;
        }

        public void Add(uint dataId, uint entityId, Vector3 pos, DateTimeOffset now, string name)
        {
            if (Members.Count == 0) FirstAddAt = now;
            LastAddAt = now;
            if (!string.IsNullOrEmpty(name)) Name = name;
            Members.Add(new Member(dataId, entityId, name, pos, now));
        }

        public void MarkFired() => IsFired = true;
    }

    private readonly record struct Member(uint DataId, uint EntityId, string Name, Vector3 Pos, DateTimeOffset Time);

    private sealed class LiveScanWindow
    {
        public LiveScanWindow(string excludedSourceName, DateTimeOffset expiresAt, DateTimeOffset nextScanAt)
        {
            ExcludedSourceName = excludedSourceName;
            ExpiresAt = expiresAt;
            NextScanAt = nextScanAt;
        }

        public string ExcludedSourceName { get; }
        public DateTimeOffset ExpiresAt { get; }
        public DateTimeOffset NextScanAt { get; set; }
    }
}
