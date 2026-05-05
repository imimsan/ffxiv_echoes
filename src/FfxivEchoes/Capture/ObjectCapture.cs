using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;

namespace FfxivEchoes.Capture;

/// <summary>
/// IObjectTable をポーリングしてオブジェクトの出現／消失を検知する（F9）。
/// </summary>
/// <remarks>
/// 対象は BattleNpc / EventObj / Companion / Treasure 等の戦闘ギミックで
/// 関心が高い種別に限定。プレイヤー（自分・PT）は出現/消失イベントとしては扱わない。
/// </remarks>
public sealed class ObjectCapture : IDisposable
{
    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly IEventBus _bus;
    private readonly IPluginLog _log;
    private readonly Dictionary<ulong, ObjectSnapshot> _seen = new();

    public ObjectCapture(IFramework framework, IObjectTable objectTable, IEventBus bus, IPluginLog log)
    {
        _framework = framework;
        _objectTable = objectTable;
        _bus = bus;
        _log = log;

        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _seen.Clear();
    }

    private void OnUpdate(IFramework _)
    {
        var now = DateTimeOffset.UtcNow;
        var current = new HashSet<ulong>();

        foreach (var obj in _objectTable)
        {
            if (!IsInteresting(obj.ObjectKind))
            {
                continue;
            }

            var key = obj.GameObjectId;
            current.Add(key);

            if (_seen.ContainsKey(key))
            {
                continue;
            }

            // 新規出現
            var pos = new Vector3(obj.Position.X, obj.Position.Y, obj.Position.Z);
            _seen[key] = new ObjectSnapshot(obj.Name.TextValue, obj.BaseId);
            _bus.Publish(new ObjectAppearedEvent(
                Timestamp: now,
                ObjectId: (uint)key,
                ObjectName: obj.Name.TextValue,
                DataId: obj.BaseId,
                Position: pos));
        }

        // 消失検知
        if (_seen.Count > current.Count)
        {
            var toRemove = new List<ulong>();
            foreach (var (id, snap) in _seen)
            {
                if (current.Contains(id))
                {
                    continue;
                }
                toRemove.Add(id);
                _bus.Publish(new ObjectDisappearedEvent(
                    Timestamp: now,
                    ObjectId: (uint)id,
                    ObjectName: snap.Name));
            }
            foreach (var id in toRemove)
            {
                _seen.Remove(id);
            }
        }
    }

    private static bool IsInteresting(ObjectKind kind) => kind switch
    {
        ObjectKind.BattleNpc => true,
        ObjectKind.EventObj => true,
        ObjectKind.EventNpc => true,
        ObjectKind.Treasure => true,
        ObjectKind.Aetheryte => false,
        ObjectKind.Pc => false, // プレイヤーは別管理
        _ => false,
    };

    private readonly record struct ObjectSnapshot(string Name, uint DataId);
}
