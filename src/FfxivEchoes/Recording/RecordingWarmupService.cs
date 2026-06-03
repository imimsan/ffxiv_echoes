using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;

namespace FfxivEchoes.Recording;

/// <summary>
/// zone 入場時に録画集計をバックグラウンドでウォームアップし、戦闘開始フレームでの
/// 同期読み込み（開幕の固まり）を防ぐ。
/// </summary>
/// <remarks>
/// <see cref="RecordingScanner.Aggregate"/> / <see cref="RecordingScanner.ListPartyMembers"/> は
/// zone 単位キャッシュを持つが、戦闘開始の最初の1回だけはキャッシュ未生成で全録画を同期読込する。
/// その読込が CombatStart のフレーム上で走ると開幕が固まるため、戦闘より前（zone 入場時）に
/// バックグラウンドスレッドで先にキャッシュを作っておく。RecordingScanner はキャッシュアクセスを
/// lock で保護しているのでバックグラウンドからの呼び出しは安全。
/// </remarks>
public sealed class RecordingWarmupService : IDisposable
{
    private readonly RecordingScanner _scanner;
    private readonly IPluginLog _log;
    private readonly IDisposable _zoneSub;
    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;

    public RecordingWarmupService(IEventBus bus, RecordingScanner scanner, IPluginLog log)
    {
        _scanner = scanner;
        _log = log;
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(OnZoneChanged);
        // 戦闘中は録画キャッシュを固定して、フェーズ移行時の全録画同期再読込（フレーム落ち）を防ぐ（FIX-03）。
        // ウォームアップで zone 入場時にキャッシュ生成済みなので、固定中もキャッシュヒットする。
        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ => _scanner.SetCombatCacheLock(true));
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ => _scanner.SetCombatCacheLock(false));
    }

    private void OnZoneChanged(ZoneChangedEvent ev)
    {
        // ゾーン移動したら固定を解除（前回戦闘が CombatEnded を出さずに離脱したケースに備える）。
        _scanner.SetCombatCacheLock(false);
        var zone = string.IsNullOrEmpty(ev.ZoneName) ? "Unknown" : ev.ZoneName;
        _ = Task.Run(() => WarmUp(zone));
    }

    private void WarmUp(string zone)
    {
        try
        {
            _scanner.Aggregate(zone);
            _scanner.ListPartyMembers(zone);
            // 開幕 cast によるプルセグメント分離（前半/後半）も zone 入場時に温める。
            // 戦闘開幕フレームでの分岐解析（先頭30秒読み + グループ別集計）の同期実行を回避。
            _scanner.AggregateBySegment(zone);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] 録画ウォームアップ失敗 zone={Zone}", zone);
        }
    }

    public void Dispose()
    {
        _zoneSub.Dispose();
        _combatStartSub.Dispose();
        _combatEndSub.Dispose();
    }
}
