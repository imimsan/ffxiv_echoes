using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
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

    private readonly Dictionary<ulong, ActorStatuses> _states = new();
    private readonly Dictionary<uint, string> _statusNameCache = new();

    public StatusCapture(
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
        _statusNameCache.Clear();
    }

    private void OnUpdate(IFramework _)
    {
        var seenActors = new HashSet<ulong>();

        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleChara chara)
            {
                continue;
            }

            seenActors.Add(chara.GameObjectId);
            UpdateActor(chara);
        }

        // 消えたアクターは状態ごと忘れる（個別 StatusLost は出さない：そもそもターゲット消失なので）
        if (_states.Count > seenActors.Count)
        {
            var toRemove = new List<ulong>();
            foreach (var id in _states.Keys)
            {
                if (!seenActors.Contains(id))
                {
                    toRemove.Add(id);
                }
            }
            foreach (var id in toRemove)
            {
                _states.Remove(id);
            }
        }
    }

    private void UpdateActor(IBattleChara chara)
    {
        var actorId = chara.GameObjectId;
        var actorName = chara.Name.TextValue;

        if (!_states.TryGetValue(actorId, out var prev))
        {
            prev = new ActorStatuses();
            _states[actorId] = prev;
        }

        var current = new Dictionary<StatusKey, StatusSnapshot>();
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
            if (!prev.Statuses.TryGetValue(key, out var old))
            {
                var name = ResolveStatusName(snap.StatusId);
                _bus.Publish(new StatusGainedEvent(
                    DateTimeOffset.UtcNow, (uint)actorId, actorName,
                    snap.StatusId, name, snap.RemainingTime, snap.Stacks, snap.SourceId));
                continue;
            }

            var stacksChanged = old.Stacks != snap.Stacks;
            var remainingChanged = Math.Abs(old.RemainingTime - snap.RemainingTime) > RemainingTimeUpdateThresholdSeconds;
            // 残時間は単調減少なので「閾値以上の差」を増分として捉えると更新が必要なケースを拾える
            // 増分（リフレッシュ）も含める：差が +0 以上を含めて判定する
            if (stacksChanged || (snap.RemainingTime > old.RemainingTime + 0.05f) || remainingChanged && stacksChanged)
            {
                _bus.Publish(new StatusUpdatedEvent(
                    DateTimeOffset.UtcNow, (uint)actorId,
                    snap.StatusId, snap.Stacks, snap.RemainingTime));
            }
        }

        // 消失
        foreach (var (key, old) in prev.Statuses)
        {
            if (current.ContainsKey(key))
            {
                continue;
            }
            var name = ResolveStatusName(old.StatusId);
            _bus.Publish(new StatusLostEvent(
                DateTimeOffset.UtcNow, (uint)actorId, actorName, old.StatusId, name));
        }

        prev.Statuses = current;
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
        public Dictionary<StatusKey, StatusSnapshot> Statuses { get; set; } = new();
    }
}
