using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FfxivEchoes.Events;
using FfxivEchoes.Utils;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace FfxivEchoes.Capture;

/// <summary>
/// FFXIV クライアントの <c>ActionEffectHandler.Receive</c> をフックして、
/// 全 NPC / プレイヤーのアクション発動（オートアタック含む）を捕捉する。
/// </summary>
/// <remarks>
/// <para>
/// 旧実装（CastCapture の polling）は IBattleNpc.CastActionId で「キャスト中」だけを
/// 拾っていたため、即時アクション（特にオートアタック）を取りこぼしていた。
/// その結果録画 jsonl に action_used がほぼ書かれず、タイムラインに AA が表示されない問題に直結。
/// </para>
/// <para>
/// 本実装は ActionEffectHandler.Receive をフックして網羅的に拾う。
/// 既存の CastCapture が拾う ActionUsedEvent（cast_time≈0 のインスタント）と重複する可能性は
/// あるが、InstantActionPolicy.DuplicateSuppressWindowSeconds (0.6 秒) によって
/// 次段（録画）側で潰される想定。
/// </para>
/// </remarks>
public sealed unsafe class ActionEffectCapture : IDisposable
{
    private delegate void ReceiveActionEffectDelegate(
        uint sourceEntityId,
        Character* sourceCharacter,
        Vector3* targetPos,
        ActionEffectHandler.Header* effectHeader,
        ActionEffectHandler.TargetEffects* effectArray,
        FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId* effectTargetIds);

    private readonly IObjectTable _objectTable;
    private readonly IDataManager _dataManager;
    private readonly IEventBus _bus;
    private readonly IPluginLog _log;

    private Hook<ReceiveActionEffectDelegate>? _hook;
    /// <summary>Dispose 中に新規の OnReceive 処理を入れない gate。
    /// 対応をしないと「Disable() と OnReceive() 実行中の競合」で
    /// _hook が null 化された参照を踏んでクラッシュする可能性がある。</summary>
    private volatile bool _disposing;
    private readonly Dictionary<uint, string> _actionNameCache = new();
    private readonly Dictionary<uint, bool> _playerActionCache = new();

    public ActionEffectCapture(
        IGameInteropProvider hookProvider,
        IObjectTable objectTable,
        IDataManager dataManager,
        IEventBus bus,
        IPluginLog log)
    {
        _objectTable = objectTable;
        _dataManager = dataManager;
        _bus = bus;
        _log = log;

        try
        {
            // FFXIVClientStructs が生成する関数ポインタ。ゲームパッチごとにアドレスは追従される。
            var addr = (nint)ActionEffectHandler.MemberFunctionPointers.Receive;
            if (addr == 0)
            {
                _log.Warning("[FfxivEchoes] ActionEffectHandler.Receive のアドレス取得に失敗。AA キャプチャ無効。");
                return;
            }
            _hook = hookProvider.HookFromAddress<ReceiveActionEffectDelegate>(addr, OnReceive);
            _hook.Enable();
            _log.Information("[FfxivEchoes] ActionEffectCapture: フック有効化 (addr=0x{Addr:X})", addr);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] ActionEffectCapture: フック初期化失敗。AA は録画されない");
        }
    }

    public void Dispose()
    {
        // フラグ先行 → これ以降 OnReceive は早期 return（独自処理を行わない）
        _disposing = true;
        var hook = _hook;
        _hook = null;
        try
        {
            hook?.Disable();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] ActionEffectCapture: Hook Disable 失敗（無視して継続）");
        }
        try
        {
            hook?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] ActionEffectCapture: Hook Dispose 失敗（無視して継続）");
        }
        _actionNameCache.Clear();
        _playerActionCache.Clear();
    }

    private void OnReceive(
        uint sourceEntityId,
        Character* sourceCharacter,
        Vector3* targetPos,
        ActionEffectHandler.Header* effectHeader,
        ActionEffectHandler.TargetEffects* effectArray,
        FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId* effectTargetIds)
    {
        // ローカルに hook 参照を確保（Dispose と競合しても自分のローカルは生きている）。
        var hook = _hook;
        // Dispose 中／フック未確保なら独自処理せず素通し。original 呼び出しは hook null なら諦め。
        // （/xlrestart 時に Dispose と OnReceive が並走してクラッシュするのを防ぐ）
        if (_disposing || hook is null)
        {
            try
            {
                hook?.Original(sourceEntityId, sourceCharacter, targetPos, effectHeader, effectArray, effectTargetIds);
            }
            catch
            {
                // フック自体の Original 呼び出しが失敗しても、ゲームは継続
            }
            return;
        }

        // 例外でゲームを巻き込まないよう全捕捉。original 呼び出しは finally で必ず行う。
        try
        {
            if (effectHeader is not null)
            {
                ProcessHeader(sourceEntityId, targetPos, effectHeader, effectTargetIds);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] ActionEffectCapture.OnReceive 例外（ゲーム動作には影響なし）");
        }
        finally
        {
            try
            {
                hook.Original(sourceEntityId, sourceCharacter, targetPos, effectHeader, effectArray, effectTargetIds);
            }
            catch
            {
                // ローカル参照の hook が破棄済みでも try/catch でゲームを巻き込まない
            }
        }
    }

    private void ProcessHeader(
        uint sourceEntityId,
        Vector3* targetPos,
        ActionEffectHandler.Header* header,
        FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId* effectTargetIds)
    {
        var actionId = header->ActionId;
        if (actionId == 0)
        {
            return;
        }

        // ActionType の値（FFXIV クライアント内部 enum 相当）
        //  0x01 = Action（通常技 / 詠唱完了 / インスタント）
        //  0x06 = AutoAttack
        //  他（Status/Item 等）は無視
        var actionType = header->ActionType;
        var isAutoAttack = actionType == ActionEffectType.AutoAttack;
        if (actionType != ActionEffectType.Action && !isAutoAttack)
        {
            return;
        }

        var actionName = ResolveActionName(actionId);
        if (!isAutoAttack)
        {
            isAutoAttack = IsAutoAttackActionName(actionName);
        }
        var sourceObj = _objectTable.FindByEntityOrObjectId(sourceEntityId);
        var sourceName = sourceObj?.Name.TextValue ?? string.Empty;

        // PC（プレイヤー）または PC のペット（owner が IPlayerCharacter）は録画から
        // 除外するため IsPlayer フラグを立てる。BattleRecorder が録画ファイルへの保存を
        // skip する。event bus 経由では引き続き全配信（trigger エンジン等が利用）。
        var sourceIsPlayerObject = sourceObj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter;
        var sourceIsPlayerOwnedPet = false;
        if (!sourceIsPlayerObject && sourceObj is Dalamud.Game.ClientState.Objects.Types.IBattleNpc petBnpc &&
            petBnpc.OwnerId != 0)
        {
            var owner = _objectTable.FindByEntityOrObjectId(petBnpc.OwnerId);
            if (owner is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter)
            {
                sourceIsPlayerOwnedPet = true;
            }
        }
        var isPlayer = ShouldMarkActionAsPlayer(
            sourceIsPlayerObject,
            sourceIsPlayerOwnedPet,
            !isAutoAttack && IsPlayerAction(actionId));

        _bus.Publish(new ActionUsedEvent(
            Timestamp: DateTimeOffset.UtcNow,
            SourceId: sourceEntityId,
            SourceName: sourceName,
            ActionId: actionId,
            ActionName: actionName,
            TargetId: ResolveFirstTargetEntityId(effectTargetIds),
            IsAutoAttack: isAutoAttack,
            IsPlayer: isPlayer,
            TargetWorld: ResolveTargetWorld(targetPos)));
    }

    public static bool ShouldMarkActionAsPlayer(
        bool sourceIsPlayerObject,
        bool sourceIsPlayerOwnedPet,
        bool actionIsPlayerAction)
    {
        return sourceIsPlayerObject || sourceIsPlayerOwnedPet || actionIsPlayerAction;
    }

    private uint? ResolveFirstTargetEntityId(
        FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId* effectTargetIds)
    {
        if (effectTargetIds is null)
        {
            return null;
        }

        var rawObjectId = *((ulong*)effectTargetIds);
        if (rawObjectId == 0 || rawObjectId == ulong.MaxValue)
        {
            return null;
        }

        return _objectTable.SearchById(rawObjectId)?.EntityId;
    }

    private static Vector3? ResolveTargetWorld(Vector3* targetPos)
    {
        if (targetPos is null)
        {
            return null;
        }

        var value = *targetPos;
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            return null;
        }

        return value == Vector3.Zero ? null : value;
    }

    private string ResolveActionName(uint actionId)
    {
        if (_actionNameCache.TryGetValue(actionId, out var cached))
        {
            return cached;
        }
        try
        {
            var sheet = _dataManager.GetExcelSheet<LuminaAction>();
            if (sheet.TryGetRow(actionId, out var row))
            {
                var name = row.Name.ToString();
                if (string.IsNullOrEmpty(name))
                {
                    name = $"Action#{actionId}";
                }
                _actionNameCache[actionId] = name;
                return name;
            }
        }
        catch
        {
            // ignore
        }
        var fallback = $"Action#{actionId}";
        _actionNameCache[actionId] = fallback;
        return fallback;
    }

    private bool IsPlayerAction(uint actionId)
    {
        if (actionId == 0)
        {
            return false;
        }

        if (_playerActionCache.TryGetValue(actionId, out var cached))
        {
            return cached;
        }

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
            _log.Verbose(ex, "[FfxivEchoes] ActionEffectCapture: player action 判定失敗 id={Id}", actionId);
        }

        _playerActionCache[actionId] = result;
        return result;
    }

    private static bool IsAutoAttackActionName(string? actionName)
    {
        if (string.IsNullOrWhiteSpace(actionName))
        {
            return false;
        }

        var normalized = actionName.Trim();
        return string.Equals(normalized, "攻撃", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "Attack", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "Auto Attack", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "Auto-Attack", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>FFXIV クライアントの ActionEffectHandler.Header.ActionType に入る値。</summary>
    private static class ActionEffectType
    {
        public const byte Action = 0x01;
        public const byte AutoAttack = 0x06;
    }
}
