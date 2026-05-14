using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
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
    private readonly LuminaPcDetector _pcDetector;
    private readonly Dictionary<ulong, ObjectSnapshot> _seen = new();
    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _zoneSub;

    /// <summary>戦闘開始 / ゾーン変更時に「次フレームで _seen をクリアして全オブジェクトを
    /// 改めて出現として publish する」フラグ。
    /// 旧実装：戦闘開始 *前* に画面に存在するオブジェクトは pre-combat 時点で publish 済 →
    /// _seen に入ってしまい、戦闘開始後は「既知」扱いで再 publish されない。
    /// 結果、BattleRecorder（戦闘中だけ録画）に object_appear が 1 件も入らない問題があった。
    /// 戦闘開始のたびに resnap して、戦闘中録画に最低 1 回は全オブジェクトの位置が入るよう保証する。</summary>
    private bool _pendingResnapshot;

    public ObjectCapture(IFramework framework, IObjectTable objectTable, IEventBus bus, IPluginLog log,
        LuminaPcDetector pcDetector)
    {
        _framework = framework;
        _objectTable = objectTable;
        _bus = bus;
        _log = log;
        _pcDetector = pcDetector ?? throw new ArgumentNullException(nameof(pcDetector));

        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ =>
        {
            _pendingResnapshot = true;
            _log.Debug("[FfxivEchoes] ObjectCapture: combat start → 次フレームで resnapshot");
        });
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(_ =>
        {
            // ゾーン変わったら _seen を即座にクリア（古いゾーンのオブジェクトを混ぜない）
            _seen.Clear();
        });

        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _combatStartSub.Dispose();
        _zoneSub.Dispose();
        _seen.Clear();
    }

    private void OnUpdate(IFramework _)
    {
        if (_pendingResnapshot)
        {
            _pendingResnapshot = false;
            _seen.Clear();
        }

        var now = DateTimeOffset.UtcNow;
        var current = new HashSet<ulong>();

        foreach (var obj in _objectTable)
        {
            if (!IsInteresting(obj.ObjectKind))
            {
                continue;
            }

            // PC のペット（フェアリー・エオス / カーバンクル / クイーン / バハムート 等）を
            // 判定。LuminaPcDetector で BattleNpcSubKind.Pet 一次判定 + OwnerId 二次判定を一括。
            // ボスの召喚物（owner=boss）は通常 mechanic として価値があるので IsPlayer=false で通す。
            var isPlayerOwned = obj is IBattleNpc bnpc && _pcDetector.IsPetBnpc(bnpc);

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
                Position: pos,
                IsPlayer: isPlayerOwned,
                EntityId: obj.EntityId));
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
