using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// 戦闘中のプレイヤー位置サンプル（<see cref="LocalPlayerPositionEvent"/>）から
/// アリーナの実寸を学習し、戦闘終了時に該当ゾーンのプロファイルへ自動反映する。
/// </summary>
/// <remarks>
/// <para>
/// ユーザーが「ルーラーで測って」「録画から推定して」などの作業を*しなくて済む*ようにする
/// ためのサービス。プレイヤーは戦闘中に必ずアリーナ端まで動かされるので、サンプルの bbox が
/// 床面とほぼ一致する。1 戦するだけでアリーナサイズが確定する。
/// </para>
/// <para>
/// 安全のため、ユーザーが既に手動で寸法を入れているプロファイルは触らない（null のときだけ書く）。
/// </para>
/// </remarks>
public sealed class AutoArenaCalibrationService : IDisposable
{
    /// <summary>採用最低サンプル数（戦闘 25 秒分）。これ未満だと精度不足とみなして書かない。</summary>
    private const int MinSamples = 50;

    /// <summary>サンプル bbox の最小幅（m）。これ未満ならアリーナとみなさず破棄。</summary>
    private const double MinDimensionMeters = 15.0;

    /// <summary>プレイヤーは壁ピッタリには行かないので少し広めに余裕を持たせる（片側 m）。</summary>
    private const double EdgeMarginMeters = 2.0;

    private readonly TriggerStore _store;
    private readonly IPluginLog _log;
    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;
    private readonly IDisposable _playerPosSub;
    private readonly IDisposable _zoneSub;

    private readonly List<Vector3> _samples = new();
    private string _currentZone = string.Empty;
    private bool _inCombat;

    public AutoArenaCalibrationService(IEventBus bus, TriggerStore store, IPluginLog log)
    {
        _store = store;
        _log = log;

        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ =>
        {
            _samples.Clear();
            _inCombat = true;
        });
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ =>
        {
            _inCombat = false;
            ApplyIfPossible();
        });
        _playerPosSub = bus.Subscribe<LocalPlayerPositionEvent>(ev =>
        {
            if (!_inCombat) return;
            _samples.Add(ev.Position);
        });
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(ev =>
        {
            _currentZone = ev.ZoneName ?? string.Empty;
            _samples.Clear();
            _inCombat = false;
        });
    }

    public void Dispose()
    {
        _combatStartSub.Dispose();
        _combatEndSub.Dispose();
        _playerPosSub.Dispose();
        _zoneSub.Dispose();
        _samples.Clear();
    }

    private void ApplyIfPossible()
    {
        if (_samples.Count < MinSamples)
        {
            _log.Debug("[FfxivEchoes] AutoArenaCalibration: サンプル不足 ({N} 件) 保留", _samples.Count);
            return;
        }

        if (string.IsNullOrEmpty(_currentZone))
        {
            return;
        }

        // X / Z の bbox を 99% タイルで取る（外れ値除去）
        var xs = new List<float>(_samples.Count);
        var zs = new List<float>(_samples.Count);
        foreach (var s in _samples)
        {
            xs.Add(s.X);
            zs.Add(s.Z);
        }
        xs.Sort();
        zs.Sort();
        var xLow = Pct(xs, 0.005);
        var xHigh = Pct(xs, 0.995);
        var zLow = Pct(zs, 0.005);
        var zHigh = Pct(zs, 0.995);
        var width = xHigh - xLow + EdgeMarginMeters * 2;
        var depth = zHigh - zLow + EdgeMarginMeters * 2;
        var cx = (xLow + xHigh) * 0.5;
        var cz = (zLow + zHigh) * 0.5;

        if (width < MinDimensionMeters || depth < MinDimensionMeters)
        {
            _log.Debug("[FfxivEchoes] AutoArenaCalibration: bbox 小さすぎ ({W:0.0}×{D:0.0}m) 採用しない",
                width, depth);
            return;
        }

        var file = _store.GetByZone(_currentZone);
        if (file is null)
        {
            return;
        }

        var changed = false;
        foreach (var profile in file.StrategyProfiles)
        {
            // 既にユーザーが寸法／中心を設定済のものは触らない（手動値を尊重）
            if (profile.ArenaWidth.HasValue ||
                profile.ArenaDepth.HasValue ||
                profile.ArenaCenterX.HasValue ||
                profile.ArenaCenterZ.HasValue)
            {
                continue;
            }
            profile.ArenaShape ??= "rect";
            profile.ArenaWidth = Snap(width);
            profile.ArenaDepth = Snap(depth);
            profile.ArenaCenterX = Snap(cx);
            profile.ArenaCenterZ = Snap(cz);
            // ArenaRadius は対角線半分相当（円形 fallback 用）
            profile.ArenaRadius = Snap(Math.Sqrt(width * width + depth * depth) * 0.5);
            changed = true;

            _log.Information(
                "[FfxivEchoes] AutoArenaCalibration: '{Profile}' を {W:0.0}×{D:0.0}m, 中心 ({X:0.0},{Z:0.0}) で自動設定 (zone={Zone}, samples={N})",
                profile.Name ?? profile.Id, width, depth, cx, cz, _currentZone, _samples.Count);
        }

        if (changed)
        {
            try
            {
                _store.SaveZone(_currentZone, file);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] AutoArenaCalibration: 保存失敗 (zone={Zone})", _currentZone);
            }
        }
    }

    private static double Pct(IReadOnlyList<float> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var i = (int)Math.Round((sorted.Count - 1) * p);
        i = Math.Clamp(i, 0, sorted.Count - 1);
        return sorted[i];
    }

    /// <summary>0.5 m 刻みに丸める（不必要に細かい数字を避ける）。</summary>
    private static double Snap(double v) => Math.Round(v * 2.0) * 0.5;
}
