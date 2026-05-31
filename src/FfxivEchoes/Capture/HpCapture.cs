using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Diagnostics;
using FfxivEchoes.Events;

namespace FfxivEchoes.Capture;

/// <summary>
/// 毎フレーム <see cref="IObjectTable"/> をポーリングして、敵 NPC + PT メンバーの HP 変化を検出する。
/// 毎フレーム値が変動するので、<see cref="HpPctChangeThreshold"/> 以上の % 変化があった時のみ
/// <see cref="HpChangedEvent"/> を発行する。
/// </summary>
/// <remarks>
/// PT メンバーの HP も拾うことで、raid-wide 攻撃検知（同タイミングで PT 全員が被弾したか）
/// などの相関ロジックが動く。
/// </remarks>
public sealed class HpCapture : IDisposable
{
    private const float HpPctChangeThreshold = 1.0f;

    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly IEventBus _bus;
    private readonly IPluginLog _log;
    private readonly LuminaPcDetector _pcDetector;

    private readonly Dictionary<ulong, float> _lastPublishedHpPct = new();
    // 毎フレームの new HashSet/List を避ける再利用バッファ（PERF-02）。
    private readonly HashSet<ulong> _seen = new();
    private readonly List<ulong> _toRemove = new();

    public HpCapture(IFramework framework, IObjectTable objectTable, IEventBus bus, IPluginLog log,
        LuminaPcDetector pcDetector)
    {
        _framework = framework;
        _objectTable = objectTable;
        _bus = bus;
        _log = log;
        _pcDetector = pcDetector ?? throw new ArgumentNullException(nameof(pcDetector));

        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _lastPublishedHpPct.Clear();
    }

    private void OnUpdate(IFramework _)
    {
        try
        {
            OnUpdateCore();
        }
        catch (Exception ex)
        {
            FrameErrorThrottle.Report(_log, ex, "HpCapture.OnUpdate");
        }
    }

    private void OnUpdateCore()
    {
        var seen = _seen;
        seen.Clear();

        foreach (var obj in _objectTable)
        {
            // IBattleChara は IBattleNpc / IPlayerCharacter の共通基底（HP / MP を持つ）
            if (obj is not IBattleChara actor)
            {
                continue;
            }
            if (actor.MaxHp == 0)
            {
                continue;
            }

            // PC ペット（カーバンクル / ソルバハムート / 妖精 / フェアリー / *エギ 等）を除外。
            // LuminaPcDetector.IsPetBnpc が BattleNpcSubKind.Pet 一次判定 + OwnerId 二次判定を一括処理。
            // ボスの召喚物（ボスがオーナー）は通常 mechanic として価値があるので維持。
            if (actor is IBattleNpc bnpc && _pcDetector.IsPetBnpc(bnpc))
            {
                continue;
            }

            seen.Add(actor.GameObjectId);

            var pct = (float)actor.CurrentHp / actor.MaxHp * 100f;
            var lastPct = _lastPublishedHpPct.TryGetValue(actor.GameObjectId, out var p) ? p : float.NaN;

            if (float.IsNaN(lastPct) || Math.Abs(lastPct - pct) >= HpPctChangeThreshold || pct == 0f && lastPct > 0f)
            {
                _lastPublishedHpPct[actor.GameObjectId] = pct;
                // IsPlayer フラグ：BattleRecorder が録画ファイルから PC HP を除外する判定に使う。
                // event bus 経由では引き続き全員配信され、RaidWideDetector 等の判定ロジックに届く。
                // 他の Capture（Status/Object）と同じく LuminaPcDetector 経由で統一判定する：
                //   PC ペット（フェアリー / カーバンクル / クイーン 等）が上の continue で skip
                //   されない edge case（OwnerId=0 で SubKind が Pet 以外）も IsPlayer=true で
                //   フラグ付与され、BattleRecorder で録画から除外される。
                var isPlayer = _pcDetector.IsPlayerOrPlayerOwned(actor);
                _bus.Publish(new HpChangedEvent(
                    DateTimeOffset.UtcNow,
                    actor.EntityId,
                    actor.Name.TextValue,
                    pct,
                    actor.CurrentHp,
                    actor.MaxHp,
                    IsPlayer: isPlayer));
            }
        }

        // 消えたアクターは忘れる
        if (_lastPublishedHpPct.Count > seen.Count)
        {
            _toRemove.Clear();
            foreach (var id in _lastPublishedHpPct.Keys)
            {
                if (!seen.Contains(id))
                {
                    _toRemove.Add(id);
                }
            }
            foreach (var id in _toRemove)
            {
                _lastPublishedHpPct.Remove(id);
            }
        }
    }
}
