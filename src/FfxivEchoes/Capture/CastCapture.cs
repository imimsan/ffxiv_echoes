using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace FfxivEchoes.Capture;

/// <summary>
/// 毎フレーム <see cref="IObjectTable"/> をポーリングして敵 NPC のキャスト状態変化を検出する。
/// </summary>
/// <remarks>
/// 完了／キャンセルの判定：IsCasting が true → false に変化したフレームの直前で
/// CurrentCastTime が TotalCastTime に近ければ完了、そうでなければキャンセルとみなす。
/// 即時アクション（cast_time = 0）の検知は本実装スコープ外。
/// </remarks>
public sealed class CastCapture : IDisposable
{
    private const float CompletionEpsilonSeconds = 0.15f;

    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly IDataManager _dataManager;
    private readonly IEventBus _bus;
    private readonly IPluginLog _log;

    private readonly Dictionary<ulong, CastState> _states = new();
    private readonly Dictionary<uint, string> _actionNameCache = new();

    public CastCapture(
        IFramework framework, IObjectTable objectTable, IDataManager dataManager,
        IEventBus bus, IPluginLog log)
    {
        _framework = framework;
        _objectTable = objectTable;
        _dataManager = dataManager;
        _bus = bus;
        _log = log;

        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _states.Clear();
        _actionNameCache.Clear();
    }

    private void OnUpdate(IFramework _)
    {
        var seen = new HashSet<ulong>();

        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleNpc battleNpc)
            {
                continue;
            }

            // PC のペット（クイーン / カーバンクル / ソルバハムート / 妖精 / *エギ 等）の
            // cast を録画から除外する。HpCapture と同じパターン：owner が PlayerCharacter なら skip。
            // ボスの召喚物（owner=boss）は通常 mechanic として価値があるので維持。
            if (battleNpc.OwnerId != 0)
            {
                var owner = _objectTable.SearchById(battleNpc.OwnerId);
                if (owner is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter)
                {
                    continue;
                }
            }

            seen.Add(battleNpc.GameObjectId);
            UpdateActor(battleNpc);
        }

        // ObjectTable から消えたアクターの状態を片付ける（消失中にキャスト中だった場合は Cancel 扱い）
        if (_states.Count > seen.Count)
        {
            var toRemove = new List<ulong>();
            foreach (var (id, state) in _states)
            {
                if (seen.Contains(id))
                {
                    continue;
                }
                if (state.IsCasting)
                {
                    var now = DateTimeOffset.UtcNow;
                    _bus.Publish(new CastCanceledEvent(now, state.SourceId, state.SourceName,
                        state.CastActionId, state.CastActionName));
                }
                toRemove.Add(id);
            }
            foreach (var id in toRemove)
            {
                _states.Remove(id);
            }
        }
    }

    private void UpdateActor(IBattleNpc actor)
    {
        var key = actor.GameObjectId;
        var nowCasting = actor.IsCasting;
        var actionId = actor.CastActionId;
        var totalCast = actor.TotalCastTime;
        var currentCast = actor.CurrentCastTime;
        var sourceId = actor.EntityId;
        var sourceName = actor.Name.TextValue;

        if (!_states.TryGetValue(key, out var prev))
        {
            // 初観測。現在キャスト中ならその瞬間に Started 扱いにする
            if (nowCasting)
            {
                var name = ResolveActionName(actionId);
                var target = ResolveTargetSnapshot(actor);
                var ev = new CastStartedEvent(
                    DateTimeOffset.UtcNow, sourceId, sourceName, actionId, name,
                    totalCast, target.EntityId, target.World);
                _bus.Publish(ev);
                _states[key] = CastState.Casting(sourceId, sourceName, actionId, name, totalCast, currentCast);
            }
            else
            {
                // 即時アクション（cast_time=0）の publish は ActionEffectCapture が担当する。
                // ここでは状態だけ Idle にする。
                _states[key] = CastState.Idle(sourceId, sourceName);
            }
            return;
        }

        // 状態遷移を判定
        if (!prev.IsCasting && nowCasting)
        {
            var name = ResolveActionName(actionId);
            var target = ResolveTargetSnapshot(actor);
            _bus.Publish(new CastStartedEvent(
                DateTimeOffset.UtcNow, sourceId, sourceName, actionId, name,
                totalCast, target.EntityId, target.World));
            _states[key] = CastState.Casting(sourceId, sourceName, actionId, name, totalCast, currentCast);
            return;
        }

        if (prev.IsCasting && !nowCasting)
        {
            var completed = prev.LastCurrentCastTime + CompletionEpsilonSeconds >= prev.TotalCastTime;
            if (completed)
            {
                _bus.Publish(new CastCompletedEvent(
                    DateTimeOffset.UtcNow, prev.SourceId, prev.SourceName,
                    prev.CastActionId, prev.CastActionName));
            }
            else
            {
                _bus.Publish(new CastCanceledEvent(
                    DateTimeOffset.UtcNow, prev.SourceId, prev.SourceName,
                    prev.CastActionId, prev.CastActionName));
            }
            _states[key] = CastState.Idle(sourceId, sourceName);
            return;
        }

        if (prev.IsCasting && nowCasting && prev.CastActionId != actionId)
        {
            // キャスト切り替え：直前を Cancel、新しいキャストを Start
            _bus.Publish(new CastCanceledEvent(
                DateTimeOffset.UtcNow, prev.SourceId, prev.SourceName,
                prev.CastActionId, prev.CastActionName));
            var name = ResolveActionName(actionId);
            var target = ResolveTargetSnapshot(actor);
            _bus.Publish(new CastStartedEvent(
                DateTimeOffset.UtcNow, sourceId, sourceName, actionId, name,
                totalCast, target.EntityId, target.World));
            _states[key] = CastState.Casting(sourceId, sourceName, actionId, name, totalCast, currentCast);
            return;
        }

        // キャスト継続中：時間進捗だけ更新
        if (nowCasting)
        {
            _states[key] = prev with { LastCurrentCastTime = currentCast };
            return;
        }

        // 非キャスト中の状態維持。ActionEffectCapture がインスタント発動を別経路で publish する。
    }

    private (uint? EntityId, System.Numerics.Vector3? World) ResolveTargetSnapshot(IBattleNpc actor)
    {
        var target = actor.CastTargetObjectId;
        if (target == 0)
        {
            return (null, null);
        }

        var targetObject = _objectTable.SearchById(target);
        return targetObject is null
            ? (null, null)
            : (targetObject.EntityId, targetObject.Position);
    }

    private string ResolveActionName(uint actionId)
    {
        if (actionId == 0)
        {
            return "(unknown)";
        }
        if (_actionNameCache.TryGetValue(actionId, out var cached))
        {
            return cached;
        }

        try
        {
            var sheet = _dataManager.GetExcelSheet<LuminaAction>();
            if (sheet.TryGetRow(actionId, out var row))
            {
                var name = row.Name.ToString();
                _actionNameCache[actionId] = name;
                return name;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] Action 名の解決に失敗 (id={Id})", actionId);
        }
        var fallback = $"Action#{actionId}";
        _actionNameCache[actionId] = fallback;
        return fallback;
    }

    // 旧 UpdateInstantAction / InstantActionState は撤去：
    // インスタント / オートアタックの ActionUsedEvent 発行は ActionEffectCapture 単独に統一。
    // ポーリング起源（CastCapture）の publish と並走させると同イベントが二重に流れるため。

    private readonly record struct CastState(
        bool IsCasting,
        uint SourceId,
        string SourceName,
        uint CastActionId,
        string CastActionName,
        float TotalCastTime,
        float LastCurrentCastTime)
    {
        public static CastState Idle(uint sourceId, string sourceName) =>
            new(false, sourceId, sourceName, 0, string.Empty, 0f, 0f);

        public static CastState Casting(uint sourceId, string sourceName, uint actionId, string actionName,
            float total, float current) =>
            new(true, sourceId, sourceName, actionId, actionName, total, current);
    }
}
