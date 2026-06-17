using System;
using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Utils;

namespace FfxivEchoes.Capture;

/// <summary>
/// CastStartedEvent を受けて、キャスト開始時点の actor の rotation を保持する。
/// Splatoon の <c>UseCastRotation</c> 機能と同等：FFXIV の多くのボスキャストはキャスト開始
/// で向きが lock される（その後 actor が回転しても AoE は最初の向きで降ってくる）。
/// 後段の <c>ActorTrackedAoeService</c> がこの値を参照することで、cone / line AoE を
/// 「キャスト開始時の向き」に固定して描画できる。
/// </summary>
/// <remarks>
/// Cast 完了 / Cancel / 該当 actor の despawn でエントリは破棄。同じ actor の同 cast id
/// が連続で来たら最新で上書き（左右翼ボスのような multi-source ケースは
/// (sourceId, castId) ペアで分離されるので衝突しない）。
/// </remarks>
public sealed class CastRotationSnapshot : IDisposable
{
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;
    private readonly ConcurrentDictionary<(uint SourceId, uint CastId), Snapshot> _snapshots = new();
    /// <summary>
    /// per-source の最新スナップショット。<see cref="TryGetLatestForSource"/> の O(1) 化用。
    /// 絶コンテンツのフェーズ遷移で同 actor の cast id が大量に増えた場合、
    /// <c>_snapshots</c> 全走査を毎フレーム避けるための index。
    /// </summary>
    private readonly ConcurrentDictionary<uint, Snapshot> _latestBySource = new();
    private readonly IDisposable _startSub;
    private readonly IDisposable _cancelSub;
    private readonly IDisposable _zoneSub;
    private readonly IDisposable _combatStartSub;

    public CastRotationSnapshot(IEventBus bus, IObjectTable objectTable, IPluginLog log)
    {
        _objectTable = objectTable;
        _log = log;
        _startSub = bus.Subscribe<CastStartedEvent>(OnStart);
        _cancelSub = bus.Subscribe<CastCanceledEvent>(OnCancel);
        // ゾーン変更で全クリア（次のインスタンスに古い snapshot を持ち込まない）。
        // _latestBySource もクリアしないと、新ゾーンで TryGetLatestForSource が旧ゾーンの
        // 向きを返し AoE が旧向きで描かれる（LC-01）。
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(_ => ClearAll());
        // 同ゾーン再挑戦（ワイプ→リトライ）でも前回戦闘の向きを持ち込まないよう戦闘開始でもクリア。
        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ => ClearAll());
    }

    private void ClearAll()
    {
        _snapshots.Clear();
        _latestBySource.Clear();
    }

    public void Dispose()
    {
        _startSub.Dispose();
        _cancelSub.Dispose();
        _zoneSub.Dispose();
        _combatStartSub.Dispose();
        _snapshots.Clear();
        _latestBySource.Clear();
    }

    /// <summary>該当 (source, cast) のスナップショットを取得。</summary>
    public bool TryGet(uint sourceId, uint castId, out float rotationRad)
    {
        if (_snapshots.TryGetValue((sourceId, castId), out var snap))
        {
            rotationRad = snap.RotationRad;
            return true;
        }
        rotationRad = 0f;
        return false;
    }

    /// <summary>
    /// 同 source の中で最も新しいスナップショットを返す。castId 不明な場面の安全網。
    /// O(1)：<see cref="_latestBySource"/> インデックスを直接引く。
    /// </summary>
    public bool TryGetLatestForSource(uint sourceId, out float rotationRad, out uint castId)
    {
        if (_latestBySource.TryGetValue(sourceId, out var s))
        {
            rotationRad = s.RotationRad;
            castId = s.CastId;
            return true;
        }
        rotationRad = 0f;
        castId = 0;
        return false;
    }

    /// <summary>
    /// テスト・デバッグ用：現在保持しているスナップショット数。
    /// </summary>
    public int Count => _snapshots.Count;

    private void OnStart(CastStartedEvent ev)
    {
        // ObjectTable は Dalamud のメインスレッド前提だが SearchById 自体は同期 read。
        // CastStartedEvent は CastCapture が Framework.Update（メインスレッド）から発行する
        // ので、ここでも問題なく actor を読める。
        var obj = _objectTable.FindByEntityOrObjectId(ev.SourceId);
        if (obj is null)
        {
            _log.Debug("[FfxivEchoes] CastRotationSnapshot: source not found id={Src} cast=0x{Id:X4}",
                ev.SourceId, ev.CastActionId);
            return;
        }

        var snap = new Snapshot(
            SourceId: ev.SourceId,
            CastId: ev.CastActionId,
            RotationRad: obj.Rotation,
            At: ev.Timestamp);
        _snapshots[(ev.SourceId, ev.CastActionId)] = snap;
        // 「source の最新」インデックスも更新（per-source O(1) 取得用）。
        // 同 source が後続キャストで snap を上書きしても OK：常に最新で正しい。
        _latestBySource[ev.SourceId] = snap;
        _log.Debug("[FfxivEchoes] CastRotationSnapshot: capture src={Src} cast=0x{Id:X4} rot={R:F3}rad",
            ev.SourceId, ev.CastActionId, obj.Rotation);
    }

    // 注意：CastCompletedEvent では削除しない。Splatoon の overcast 思想に同じ。
    // キャスト終了直後の数秒（AoE が降ってきて余韻が消えるまで）も rotation は固定で
    // 描画したいため、snapshot は次の同 (sourceId, castId) キャスト開始で上書きされるか、
    // ZoneChangedEvent で全クリアされるまで生かす。
    private void OnCancel(CastCanceledEvent ev)
    {
        _snapshots.TryRemove((ev.SourceId, ev.CastActionId), out _);
        // per-source 最新インデックスからも除去。これが無いと、フェーズ境界でキャンセルされた
        // キャストの向きが _latestBySource に残り、後続 AoE が旧向きで描かれる（LC-02/NEW-04）。
        _latestBySource.TryRemove(ev.SourceId, out _);
    }

    private readonly record struct Snapshot(uint SourceId, uint CastId, float RotationRad, DateTimeOffset At);
}
