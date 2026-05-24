using System;
using System.Numerics;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;

namespace FfxivEchoes.Capture;

/// <summary>
/// 戦闘中の自分（LocalPlayer）の位置を一定間隔でサンプルし、
/// <see cref="LocalPlayerPositionEvent"/> として publish する。
/// </summary>
/// <remarks>
/// 主用途：録画ベースのアリーナ寸法推定。
/// プレイヤーは戦闘中に必ずアリーナ端まで動かされる（散開・誘導・退避）ので、
/// 位置サンプルの bbox がアリーナ床面とほぼ一致する。
/// object_appear 単独だとボス／add の出現位置が中央付近に偏り bbox が小さくなりがちで
/// アリーナ全体を捉えきれない。
///
/// サンプル頻度は <see cref="SampleIntervalSec"/> 秒（既定 0.5 秒）。
/// 1 戦闘 8 分なら 1000 件弱。録画ファイルが膨大にならない範囲で密度を担保する。
/// 戦闘中のみ動作。<see cref="CombatStartedEvent"/> で開始、<see cref="CombatEndedEvent"/> /
/// <see cref="ZoneChangedEvent"/> で停止。
/// </remarks>
public sealed class PlayerPositionCapture : IDisposable
{
    /// <summary>サンプル間隔（秒）。これより短い間隔の発火は捨てる。</summary>
    private const double SampleIntervalSec = 0.5;

    /// <summary>同位置は無視する閾値（メートル）。立ち止まり中の連続サンプルを抑制。</summary>
    private const float MinMoveMeters = 0.20f;

    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly IEventBus _bus;
    private readonly IPluginLog _log;

    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;
    private readonly IDisposable _zoneSub;

    private bool _inCombat;
    private DateTimeOffset _lastSampleAt = DateTimeOffset.MinValue;
    private Vector3 _lastSampledPos = Vector3.Zero;
    private bool _hasLastSample;

    public PlayerPositionCapture(IFramework framework, IObjectTable objectTable, IEventBus bus, IPluginLog log)
    {
        _framework = framework;
        _objectTable = objectTable;
        _bus = bus;
        _log = log;

        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ =>
        {
            _inCombat = true;
            _lastSampleAt = DateTimeOffset.MinValue;
            _hasLastSample = false;
        });
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ =>
        {
            _inCombat = false;
        });
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(_ =>
        {
            _inCombat = false;
            _hasLastSample = false;
        });

        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _combatStartSub.Dispose();
        _combatEndSub.Dispose();
        _zoneSub.Dispose();
    }

    private void OnUpdate(IFramework _)
    {
        if (!_inCombat) return;

        var now = DateTimeOffset.UtcNow;
        if ((now - _lastSampleAt).TotalSeconds < SampleIntervalSec) return;

        var lp = _objectTable.LocalPlayer;
        if (lp is null) return;

        var pos = new Vector3(lp.Position.X, lp.Position.Y, lp.Position.Z);

        // 位置不明 / 0,0 は除外（ロード直後など）
        if (pos.X == 0 && pos.Z == 0) return;

        // 立ち止まり中は省略
        if (_hasLastSample && Vector3.Distance(pos, _lastSampledPos) < MinMoveMeters)
        {
            _lastSampleAt = now; // 抑制したフレームも次回判定起点を更新（高頻度の hit 計算を避ける）
            return;
        }

        _lastSampleAt = now;
        _lastSampledPos = pos;
        _hasLastSample = true;

        _bus.Publish(new LocalPlayerPositionEvent(Timestamp: now, Position: pos));
    }
}
