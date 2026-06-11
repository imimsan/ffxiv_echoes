using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Diagnostics;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Triggers;

/// <summary>
/// タイムライン HUD（UpcomingEventsWindow）の補正済み予測リストを入力に、残り
/// PredictedAoeAdvanceSec 秒以内の cast_start 予測の実効 AoE 範囲をミニマップ俯瞰図へ
/// 事前描画する。形状解決は safe_call 辞書（真偽等の実効範囲）→ Lumina の既存パスを
/// cast_id 単位でキャッシュ。source actor は名前完全一致のみで解決し、解決できなければ
/// 描画しない（旧予測描画の回帰原因だった最大 HP fallback は行わない）。
/// SPEC: docs/superpowers/specs/2026-06-11-aoe-preview-minimap-design.md
/// </summary>
public sealed class UpcomingAoePreviewService : IDisposable
{
    private static readonly TimeSpan TableRescanInterval = TimeSpan.FromSeconds(0.5);

    private readonly TriggerStore _store;
    private readonly CombatClock _combatClock;
    private readonly IDataManager _dataManager;
    private readonly IObjectTable _objectTable;
    private readonly MinimapWindow _minimap;
    private readonly Configuration _config;
    private readonly IPluginLog _log;

    private readonly IDisposable _eventSub;
    private readonly object _gate = new();
    private readonly Dictionary<uint, PreviewGeometry?> _geometryCache = new();
    private readonly Dictionary<string, uint> _sourceIdByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, double> _lastConfirmRel = new();
    private readonly List<UpcomingItem> _candidateBuffer = new();
    private readonly List<PredictedAoePreviewItem> _itemBuffer = new();
    private string _currentZone = "Unknown";
    private DateTimeOffset _lastTableScan = DateTimeOffset.MinValue;
    private volatile bool _cachesDirty;
    private bool _lastPublishHadItems;

    public UpcomingAoePreviewService(
        IEventBus bus,
        TriggerStore store,
        CombatClock combatClock,
        IDataManager dataManager,
        IObjectTable objectTable,
        MinimapWindow minimap,
        Configuration config,
        IPluginLog log)
    {
        _store = store;
        _combatClock = combatClock;
        _dataManager = dataManager;
        _objectTable = objectTable;
        _minimap = minimap;
        _config = config;
        _log = log;
        _eventSub = bus.SubscribeAll(OnEvent);
        // Reloaded はワーカースレッド発火。フラグだけ立てて実処理は次の Publish（メインスレッド）で行う。
        _store.Reloaded += OnTriggerStoreReloaded;
    }

    public void Dispose()
    {
        _eventSub.Dispose();
        _store.Reloaded -= OnTriggerStoreReloaded;
    }

    private void OnTriggerStoreReloaded() => _cachesDirty = true;

    private void OnEvent(IGameEvent ev)
    {
        switch (ev)
        {
            case ZoneChangedEvent z:
                _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName;
                _cachesDirty = true;
                lock (_gate)
                {
                    _lastConfirmRel.Clear();
                }
                break;
            case CombatStartedEvent:
            case CombatEndedEvent:
                lock (_gate)
                {
                    _lastConfirmRel.Clear();
                }
                break;
            case CastStartedEvent c:
                var rel = _combatClock.RelativeSecondsAt(c.Timestamp);
                if (rel is not null)
                {
                    lock (_gate)
                    {
                        _lastConfirmRel[c.CastActionId] = rel.Value;
                    }
                }
                break;
        }
    }

    /// <summary>
    /// UpcomingEventsWindow.Draw() から毎フレーム呼ばれる（メインスレッド）。
    /// items は CollectUpcoming の出力（時刻昇順・補正済み・表示用 dedup 前）。
    /// </summary>
    public void Publish(IReadOnlyList<UpcomingItem> items, double nowRel)
    {
        try
        {
            PublishCore(items, nowRel);
        }
        catch (Exception ex)
        {
            FrameErrorThrottle.Report(_log, ex, "UpcomingAoePreviewService.Publish");
        }
    }

    private void PublishCore(IReadOnlyList<UpcomingItem> items, double nowRel)
    {
        if (!_config.ShowPredictedAoeOnMinimap)
        {
            ClearIfNeeded();
            return;
        }
        if (_cachesDirty)
        {
            _geometryCache.Clear();
            _sourceIdByName.Clear();
            _cachesDirty = false;
        }

        PredictedAoePreviewPolicy.SelectPreviewCandidates(
            items, nowRel, _config.PredictedAoeAdvanceSec,
            PredictedAoePreviewPolicy.MaxPreviewItems, _candidateBuffer);

        var file = _store.GetByZone(_currentZone);
        var arena = AutoAoeDisplayPolicy.ResolveArena(file);

        _itemBuffer.Clear();
        foreach (var c in _candidateBuffer)
        {
            if (!AoeResolver.TryParseCastId(c.Id ?? string.Empty, out var actionId) || actionId == 0)
            {
                continue;
            }

            double? confirmed;
            lock (_gate)
            {
                confirmed = _lastConfirmRel.TryGetValue(actionId, out var t) ? t : (double?)null;
            }
            if (PredictedAoePreviewPolicy.IsSuppressedByConfirmedCast(c.Time, confirmed))
            {
                continue;
            }

            var geom = ResolveGeometry(actionId, c.Label, file);
            if (geom is null)
            {
                continue;
            }

            var npc = ResolveSourceStrict(c.Source);
            if (npc is null)
            {
                continue;
            }

            var radius = geom.Aoe is null
                ? (float?)null
                : AoeResolver.EffectiveRadius(geom.Aoe, npc.HitboxRadius);
            var facing = geom.UsesFacing
                ? ArenaProjection.RotationToMapAngleRad(npc.Rotation)
                : (float?)null;

            _itemBuffer.Add(new PredictedAoePreviewItem(
                Label: c.Label,
                RemainingSec: c.Time - nowRel,
                Gimmick: geom.Gimmick,
                FanDeg: geom.FanDeg ?? 90.0,
                DirectionAngleRad: facing,
                SourceWorld: npc.Position,
                AoeRadius: radius,
                AoeHalfWidthM: geom.Aoe is { HalfWidthM: > 0f } a ? a.HalfWidthM : null,
                AoeCastType: geom.Aoe?.CastType,
                AoeOmenId: geom.Aoe?.OmenId,
                ArenaRadius: (float)arena.ArenaRadius,
                ArenaShape: arena.ArenaShape,
                ArenaHalfWidth: arena.ArenaWidth is { } w && w > 0 ? (float)(w * 0.5) : (float)arena.ArenaRadius,
                ArenaHalfDepth: arena.ArenaDepth is { } d && d > 0 ? (float)(d * 0.5) : (float)arena.ArenaRadius,
                LockedArenaCenter: arena.LockedArenaCenter));
        }

        if (_itemBuffer.Count > 0 || _lastPublishHadItems)
        {
            _minimap.SetPredictedAoePreview(_itemBuffer);
        }
        _lastPublishHadItems = _itemBuffer.Count > 0;
    }

    private void ClearIfNeeded()
    {
        if (!_lastPublishHadItems)
        {
            return;
        }
        _itemBuffer.Clear();
        _minimap.SetPredictedAoePreview(_itemBuffer);
        _lastPublishHadItems = false;
    }

    private PreviewGeometry? ResolveGeometry(uint actionId, string label, TriggerFile? file)
    {
        if (_geometryCache.TryGetValue(actionId, out var cached))
        {
            return cached;
        }
        var resolved = ResolveGeometryCore(actionId, label, file);
        _geometryCache[actionId] = resolved;
        return resolved;
    }

    private PreviewGeometry? ResolveGeometryCore(uint actionId, string label, TriggerFile? file)
    {
        if (AutoSafeCallPlanner.IsRaidWide(file, actionId, label))
        {
            return null;
        }
        var match = new MatchCondition { CastId = $"0x{actionId:X}", CastName = label };
        if (AutoSafeCallPlanner.ShouldSuppressMinimap(file, match))
        {
            return null;
        }

        var known = AutoSafeCallPlanner.CreateKnown(actionId, label);
        var aoe = AoeResolver.Resolve(_dataManager, actionId, _log);
        if (aoe is not null && PredictedAoePreviewPolicy.ShouldSkipOversized(aoe.CastType, aoe.Radius))
        {
            if (known is null)
            {
                return null;
            }
            // 辞書で実効形状が明示されている（真偽の two_side_cleave 等）場合は、Lumina 上の
            // 巨大 EffectRange を捨ててギミック形状のみ描く（アリーナ半径基準のデフォルト寸法）。
            aoe = null;
        }

        var safeCall = known ?? (aoe is not null ? AutoSafeCallPlanner.Create(aoe, label) : null);
        var visual = safeCall ?? (aoe is not null ? AutoSafeCallPlanner.CreateVisual(aoe, label) : null);
        if (visual is null)
        {
            return null;
        }

        return new PreviewGeometry(
            Gimmick: visual.Gimmick,
            FanDeg: visual.FanDeg,
            Aoe: aoe,
            UsesFacing: ArenaProjection.UsesFacing(visual.Gimmick));
    }

    private IBattleNpc? ResolveSourceStrict(string? sourceName)
    {
        if (string.IsNullOrEmpty(sourceName))
        {
            return null;
        }

        if (_sourceIdByName.TryGetValue(sourceName, out var id))
        {
            if (_objectTable.SearchById(id) is IBattleNpc cachedNpc &&
                string.Equals(cachedNpc.Name.TextValue, sourceName, StringComparison.OrdinalIgnoreCase))
            {
                return cachedNpc;
            }
            _sourceIdByName.Remove(sourceName);
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastTableScan < TableRescanInterval)
        {
            return null;
        }
        _lastTableScan = now;

        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleNpc npc)
            {
                continue;
            }
            if (npc.MaxHp == 0)
            {
                continue;
            }
            if (string.Equals(npc.Name.TextValue, sourceName, StringComparison.OrdinalIgnoreCase))
            {
                _sourceIdByName[sourceName] = npc.EntityId;
                return npc;
            }
        }

        // 名前完全一致のみ。最大 HP fallback は「中央の謎ドーナツ」回帰の根本原因なので行わない。
        return null;
    }

    private sealed record PreviewGeometry(
        string Gimmick,
        double? FanDeg,
        AoeResolver.AoeInfo? Aoe,
        bool UsesFacing);
}
