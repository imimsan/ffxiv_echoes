using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Utils;
using Lumina.Excel.Sheets;

namespace FfxivEchoes.Capture;

/// <summary>
/// 毎フレーム <see cref="IObjectTable"/> をポーリングして、各 BattleChara のステータス変化を検出する。
/// 新規付与 → <see cref="StatusGainedEvent"/>、消失 → <see cref="StatusLostEvent"/>、
/// スタック数や残時間の変化 → <see cref="StatusUpdatedEvent"/>。
/// </summary>
public sealed class StatusCapture : IDisposable
{
    /// <summary>残時間の更新で StatusUpdated を発火する閾値（秒）。
    /// 細かすぎる発火を抑えつつ、長デバフ／短デバフの分岐に必要な解像度を確保する。</summary>
    private const float RemainingTimeUpdateThresholdSeconds = 0.5f;

    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly IDataManager _dataManager;
    private readonly IEventBus _bus;
    private readonly IPluginLog _log;
    private readonly LuminaPcDetector _pcDetector;

    private readonly Dictionary<ulong, ActorStatuses> _states = new();
    private readonly Dictionary<uint, string> _statusNameCache = new();
    // 毎フレームの new HashSet/List を避けるため再利用（PERF-01/02：add 大量出現時の GC 削減）。
    private readonly HashSet<ulong> _seenActors = new();
    private readonly List<ulong> _staleActors = new();

    public StatusCapture(
        IFramework framework, IObjectTable objectTable, IDataManager dataManager,
        IEventBus bus, IPluginLog log,
        LuminaPcDetector pcDetector)
    {
        // pcDetector は必須。null だと IsPlayer=false 一律で旧バグ（PC スキルが全部録画に残る）が
        // 復活する。Plugin.cs の DI 順序保証と合わせて null 注入を起動時にクラッシュさせる。
        _framework = framework;
        _objectTable = objectTable;
        _dataManager = dataManager;
        _bus = bus;
        _log = log;
        _pcDetector = pcDetector ?? throw new ArgumentNullException(nameof(pcDetector));

        _framework.Update += OnUpdate;
    }

    /// <summary>
    /// Status ID と SourceId から PC 由来かを判定。
    /// 1) Lumina ClassJobCategory が PC 専用 → true（決定論）
    /// 2) source actor が IPlayerCharacter or PC ペット → true（PC が付与した buff/debuff）
    /// </summary>
    /// <remarks>
    /// 罠：ここで「target=PC なら true」を入れてはいけない。
    /// タンクバスター系の debuff（Damage Down / Vulnerability Up / Sludge 等）はボスが PC タンク
    /// に付けるので target=PC, source=Boss の組み合わせになる。target=PC で true を返すと、
    /// この種のボスデバフが BattleRecorder で IsPlayer=true 扱いになって録画から消え、
    /// 攻略 mechanic 候補に上がらなくなる。Source 側の判定だけを信頼する。
    /// </remarks>
    private bool IsPlayerStatus(uint statusId, uint sourceId)
    {
        if (_pcDetector.IsPlayerStatus(statusId)) return true;
        // source = PC（PC が他者に付与した buff/debuff）
        // sourceId は EntityId 空間（uint）。SearchById(uint) のオーバーロードで EntityId 検索する。
        // edge case：source actor が despawn 直後で ObjectTable に居ない場合、SearchById は null を返す。
        // → IsPlayerOrPlayerOwned(null) = false → IsPlayer=false で扱う（録画される、安全側）。
        // 「ボスが despawn 直前にかけたデバフ」が IsPlayer=false で記録されるので情報損失なし。
        if (sourceId != 0)
        {
            var srcObj = _objectTable.FindByEntityOrObjectId(sourceId);
            if (_pcDetector.IsPlayerOrPlayerOwned(srcObj)) return true;
        }
        return false;
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _states.Clear();
        _statusNameCache.Clear();
    }

    private void OnUpdate(IFramework _)
    {
        _seenActors.Clear();

        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleChara chara)
            {
                continue;
            }

            _seenActors.Add(chara.GameObjectId);
            UpdateActor(chara);
        }

        // 消えたアクターは状態ごと忘れる（個別 StatusLost は出さない：そもそもターゲット消失なので）
        if (_states.Count > _seenActors.Count)
        {
            _staleActors.Clear();
            foreach (var id in _states.Keys)
            {
                if (!_seenActors.Contains(id))
                {
                    _staleActors.Add(id);
                }
            }
            foreach (var id in _staleActors)
            {
                _states.Remove(id);
            }
        }
    }

    private void UpdateActor(IBattleChara chara)
    {
        var actorId = chara.GameObjectId;
        var actorName = chara.Name.TextValue;

        if (!_states.TryGetValue(actorId, out var st))
        {
            st = new ActorStatuses();
            _states[actorId] = st;
        }

        // 前フレームの状態は st.Previous、今フレームは st.Current（再利用バッファ）に詰める。
        // 毎フレーム new Dictionary を作らず 2 辞書を swap することで GC を抑える（PERF-01）。
        // 単純な「1 辞書 Clear + 再投入」は消失判定ループで今フレーム分が混入し StatusLost が
        // 一切発火しなくなるため不可。必ず Previous/Current を分離する。
        var prev = st.Previous;
        var current = st.Current;
        current.Clear();
        foreach (var status in chara.StatusList)
        {
            if (status is null || status.StatusId == 0)
            {
                continue;
            }
            var key = new StatusKey(status.StatusId, status.SourceId);
            current[key] = new StatusSnapshot(
                status.StatusId,
                status.SourceId,
                status.Param,
                status.RemainingTime);
        }

        // 新規 / 更新
        foreach (var (key, snap) in current)
        {
            if (!prev.TryGetValue(key, out var old))
            {
                var name = ResolveStatusName(snap.StatusId);
                _bus.Publish(new StatusGainedEvent(
                    DateTimeOffset.UtcNow, (uint)actorId, actorName,
                    snap.StatusId, name, snap.RemainingTime, snap.Stacks, snap.SourceId,
                    IsPlayer: IsPlayerStatus(snap.StatusId, snap.SourceId)));
                continue;
            }

            var stacksChanged = old.Stacks != snap.Stacks;
            var refreshed = snap.RemainingTime > old.RemainingTime + 0.05f;
            var remainingChanged = Math.Abs(old.RemainingTime - snap.RemainingTime) > RemainingTimeUpdateThresholdSeconds;
            // 発火条件：
            // - スタック変化（弱体・強化のスタック消費／追加）
            // - リフレッシュ（残時間が増えた）
            // - 残時間が閾値（0.5s）以上変化（debuff の自然減衰でタイムライン更新したい）
            // 旧コードは `remainingChanged && stacksChanged` だったが、& は | より優先のため
            // stacksChanged 単独でカバーされ実質デッドコードだった。
            if (stacksChanged || refreshed || remainingChanged)
            {
                _bus.Publish(new StatusUpdatedEvent(
                    DateTimeOffset.UtcNow, (uint)actorId,
                    snap.StatusId, snap.Stacks, snap.RemainingTime,
                    IsPlayer: IsPlayerStatus(snap.StatusId, snap.SourceId)));
            }
        }

        // 消失
        foreach (var (key, old) in prev)
        {
            if (current.ContainsKey(key))
            {
                continue;
            }
            var name = ResolveStatusName(old.StatusId);
            _bus.Publish(new StatusLostEvent(
                DateTimeOffset.UtcNow, (uint)actorId, actorName, old.StatusId, name,
                IsPlayer: IsPlayerStatus(old.StatusId, old.SourceId)));
        }

        // swap：今フレームの current を次フレームの previous に。旧 previous は次フレームの
        // current バッファとして再利用する（次回 UpdateActor 冒頭で Clear される）。
        st.Previous = current;
        st.Current = prev;
    }

    private string ResolveStatusName(uint statusId)
    {
        if (_statusNameCache.TryGetValue(statusId, out var cached))
        {
            return cached;
        }
        try
        {
            var sheet = _dataManager.GetExcelSheet<Status>();
            if (sheet.TryGetRow(statusId, out var row))
            {
                var name = row.Name.ToString();
                _statusNameCache[statusId] = name;
                return name;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] Status 名の解決に失敗 (id={Id})", statusId);
        }
        var fallback = $"Status#{statusId}";
        _statusNameCache[statusId] = fallback;
        return fallback;
    }

    private readonly record struct StatusKey(uint StatusId, uint SourceId);
    private readonly record struct StatusSnapshot(uint StatusId, uint SourceId, ushort Stacks, float RemainingTime);

    private sealed class ActorStatuses
    {
        // Previous = 前フレームの状態、Current = 今フレームの再利用バッファ。毎フレーム swap する。
        public Dictionary<StatusKey, StatusSnapshot> Previous { get; set; } = new();
        public Dictionary<StatusKey, StatusSnapshot> Current { get; set; } = new();
    }
}
