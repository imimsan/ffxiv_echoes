using System;
using System.Collections.Concurrent;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace FfxivEchoes.Capture;

/// <summary>
/// Lumina の Action / Status / BNpc データを参照して「PC 由来か」を決定論的に判定する共用サービス。
/// 名前パターンに頼らず、ゲームデータ上の属性で確実に分類する。
/// </summary>
/// <remarks>
/// <para>
/// 判定基準（既知のゲームデータ仕様に基づく）：
/// </para>
/// <list type="bullet">
/// <item><b>Status</b>: <c>ClassJobCategory.RowId</c> が 2 以上 = 特定 PC ジョブ専用バフ。
/// 0 = 未割当て / 1 = "All Classes"（ボスデバフ ＋ 食事系 PC バフが混在するためここは PC 扱いしない）。</item>
/// <item><b>Action</b>: <c>IsPlayerAction</c> が true なら PC アクション。</item>
/// <item><b>BattleNpc</b>: <see cref="BattleNpcSubKind.Pet"/> = 2 が PC 召喚物。
/// OwnerId による fallback 判定も併用（古いゲームバージョンや稀なケース対応）。</item>
/// </list>
/// <para>
/// キャッシュは <see cref="ConcurrentDictionary{TKey, TValue}"/> で並行アクセス安全。
/// Dispose で全クリア（プラグインリロード時の古いキャッシュ混入を防ぐ）。
/// </para>
/// <para>
/// Lumina sheet 取得失敗 / row 不在のときは <c>false</c> を返す（除外しない安全側）。
/// ゲームパッチ直後の一時的な lookup 失敗でクラッシュしないため。
/// </para>
/// </remarks>
public sealed class LuminaPcDetector : IDisposable
{
    private readonly IDataManager _dataManager;
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;
    private readonly ConcurrentDictionary<uint, bool> _statusCache = new();
    private readonly ConcurrentDictionary<uint, bool> _actionCache = new();

    public LuminaPcDetector(IDataManager dataManager, IObjectTable objectTable, IPluginLog log)
    {
        _dataManager = dataManager;
        _objectTable = objectTable;
        _log = log;
    }

    public void Dispose()
    {
        _statusCache.Clear();
        _actionCache.Clear();
    }

    /// <summary>
    /// Status sheet を引いて、PC ジョブ専用バフかどうか判定する。
    /// <c>ClassJobCategory.RowId &gt;= 2</c> なら PC 専用 → true。
    /// </summary>
    /// <remarks>
    /// <para>罠：<c>RowId == 1</c> は "All Classes"（全ジョブ共通カテゴリ）で、ボスデバフと PC 食事バフ
    /// 等が混在する。ここを true にするとボスギミックを誤フィルタするので注意。</para>
    /// <para>パッチ耐性：FFXIV のメジャーパッチで Status の ClassJobCategory が変動した場合、
    /// 本来 PC 専用と判定されるべき新規 status が <c>category &lt; 2</c> に分類されてサイレント誤フィルタ
    /// 漏れを起こす可能性がある。安全側 fail（除外しない）なのでクラッシュはしないが、
    /// 録画ノイズ増の症状で気付くことになる。リリース後パッチ直後はサンプル録画を要再確認。
    /// status 解決失敗時のみログを出して可視化する。</para>
    /// </remarks>
    public bool IsPlayerStatus(uint statusId)
    {
        if (statusId == 0) return false;
        if (_statusCache.TryGetValue(statusId, out var cached)) return cached;
        var result = false;
        var rowFound = false;
        try
        {
            var sheet = _dataManager.GetExcelSheet<LuminaStatus>();
            if (sheet.TryGetRow(statusId, out var row))
            {
                rowFound = true;
                var category = row.ClassJobCategory.RowId;
                // 2 以上 = 特定 PC ジョブカテゴリ。0 = 未割当て、1 = All Classes (PC 扱いしない)。
                result = category >= 2;
            }
        }
        catch (Exception ex)
        {
            _log.Verbose(ex, "[FfxivEchoes] LuminaPcDetector: Status row 取得失敗 id={Id}", statusId);
        }
        if (!rowFound)
        {
            // ゲームパッチ直後 / カスタム status の可能性。Verbose で記録（ノイズ抑制）。
            _log.Verbose("[FfxivEchoes] LuminaPcDetector: Status row が見つからない id={Id} (PC 扱いせず録画継続)", statusId);
        }
        _statusCache[statusId] = result;
        return result;
    }

    /// <summary>
    /// Action sheet の <c>IsPlayerAction</c> を参照して PC アクションかどうか判定。
    /// </summary>
    public bool IsPlayerAction(uint actionId)
    {
        if (actionId == 0) return false;
        if (_actionCache.TryGetValue(actionId, out var cached)) return cached;
        var result = false;
        try
        {
            var sheet = _dataManager.GetExcelSheet<LuminaAction>();
            if (sheet.TryGetRow(actionId, out var row))
            {
                result = row.IsPlayerAction;
            }
        }
        catch (Exception ex)
        {
            _log.Verbose(ex, "[FfxivEchoes] LuminaPcDetector: Action row 取得失敗 id={Id}", actionId);
        }
        _actionCache[actionId] = result;
        return result;
    }

    /// <summary>
    /// BNpc が PC 召喚物（フェアリー / カーバンクル / クイーン / バハムート / 妖精 等）かどうか判定。
    /// 判定条件：<c>OwnerId</c> から実 actor を引いて <see cref="IPlayerCharacter"/> なら PC 召喚物。
    /// </summary>
    /// <remarks>
    /// 注意：以前は <see cref="BattleNpcSubKind.Pet"/> 単独で判定していたが、FFXIV の一部ボス
    /// 召喚 add（例：月の底のケツァクウァトル）が SubKind=Pet を持つケースがあり、それらが
    /// PC 召喚物扱いされて ObjectCapture の IsPlayer=true → BattleRecorder で録画除外、
    /// AddObjectAoeService で AoE 描画除外、攻略登録タブから消失する regression を起こす。
    /// 真の PC ペット（フェアリー/カーバンクル等）は必ず OwnerId が PC を指すので、OwnerId 経由
    /// での判定に統一する。SubKind=Pet だが OwnerId=0 or OwnerId=ボス の NPC はギミック add として扱う。
    /// </remarks>
    public bool IsPetBnpc(IBattleNpc bnpc)
    {
        if (bnpc is null) return false;
        if (bnpc.OwnerId == 0) return false;
        var owner = _objectTable.SearchById(bnpc.OwnerId);
        return owner is IPlayerCharacter;
    }

    /// <summary>
    /// 任意の <see cref="IGameObject"/> が PC または PC 召喚物かを判定する複合ヘルパ。
    /// </summary>
    public bool IsPlayerOrPlayerOwned(IGameObject? obj)
    {
        if (obj is null) return false;
        if (obj is IPlayerCharacter) return true;
        if (obj is IBattleNpc bnpc) return IsPetBnpc(bnpc);
        return false;
    }
}
