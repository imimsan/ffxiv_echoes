using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;

namespace FfxivEchoes.Capture;

/// <summary>
/// 毎フレーム <see cref="IObjectTable"/> をポーリングして、敵 NPC の HP 変化を検出する。
/// 毎フレーム値が変動するので、<see cref="HpPctChangeThreshold"/> 以上の % 変化があった時のみ
/// <see cref="HpChangedEvent"/> を発行する。
/// </summary>
/// <remarks>
/// 自分や PT メンバーの HP は本キャプチャでは扱わない（M3 スコープ：ボスフェーズ移行検知が主目的）。
/// </remarks>
public sealed class HpCapture : IDisposable
{
    private const float HpPctChangeThreshold = 1.0f;

    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly IEventBus _bus;
    private readonly IPluginLog _log;

    private readonly Dictionary<ulong, float> _lastPublishedHpPct = new();

    public HpCapture(IFramework framework, IObjectTable objectTable, IEventBus bus, IPluginLog log)
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
        _lastPublishedHpPct.Clear();
    }

    private void OnUpdate(IFramework _)
    {
        var seen = new HashSet<ulong>();

        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleNpc actor)
            {
                continue;
            }
            if (actor.MaxHp == 0)
            {
                continue;
            }

            seen.Add(actor.GameObjectId);

            var pct = (float)actor.CurrentHp / actor.MaxHp * 100f;
            var lastPct = _lastPublishedHpPct.TryGetValue(actor.GameObjectId, out var p) ? p : float.NaN;

            if (float.IsNaN(lastPct) || Math.Abs(lastPct - pct) >= HpPctChangeThreshold || pct == 0f && lastPct > 0f)
            {
                _lastPublishedHpPct[actor.GameObjectId] = pct;
                _bus.Publish(new HpChangedEvent(
                    DateTimeOffset.UtcNow,
                    (uint)actor.GameObjectId,
                    actor.Name.TextValue,
                    pct,
                    actor.CurrentHp,
                    actor.MaxHp));
            }
        }

        // 消えたアクターは忘れる
        if (_lastPublishedHpPct.Count > seen.Count)
        {
            var toRemove = new List<ulong>();
            foreach (var id in _lastPublishedHpPct.Keys)
            {
                if (!seen.Contains(id))
                {
                    toRemove.Add(id);
                }
            }
            foreach (var id in toRemove)
            {
                _lastPublishedHpPct.Remove(id);
            }
        }
    }
}
