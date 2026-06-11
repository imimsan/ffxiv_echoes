using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using Lumina.Excel.Sheets;
using Dalamud.Game.ClientState.Objects.Types;

namespace FfxivEchoes.Windows;

/// <summary>
/// 自分に付いているデバフ（弱体）の名前・残り秒・スタックを戦闘中に一覧表示する HUD。
/// データ源はイベントではなく毎フレーム LocalPlayer.StatusList の直接参照
/// （メインスレッド・残り秒がライブで正確・状態管理不要）。
/// </summary>
public sealed class DebuffHudWindow : Window, IDisposable
{
    private const float Width = 240f;
    private const float Height = 180f;
    private const float WarnSec = 5f;

    private readonly IObjectTable _objectTable;
    private readonly IDataManager _dataManager;
    private readonly Configuration _config;
    private readonly IDisposable _eventSub;
    private readonly List<DebuffRow> _rowBuffer = new();
    private readonly List<DebuffRow> _displayBuffer = new();
    // Lumina Status シートの (名前, 弱体か) キャッシュ。シートは不変なので無期限。
    private readonly Dictionary<uint, (string Name, bool IsDetrimental)> _statusCache = new();
    private bool _inCombat;

    public DebuffHudWindow(IEventBus bus, IObjectTable objectTable, IDataManager dataManager, Configuration config)
        : base("##ffxiv-echoes-debuff-hud",
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse)
    {
        _objectTable = objectTable;
        _dataManager = dataManager;
        _config = config;
        Size = new Vector2(Width, Height) * ImGuiHelpers.GlobalScale;
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;
        RespectCloseHotkey = false;
        _eventSub = bus.SubscribeAll(OnEvent);
    }

    public void Dispose() => _eventSub.Dispose();

    private void OnEvent(IGameEvent ev)
    {
        switch (ev)
        {
            case CombatStartedEvent:
                _inCombat = true;
                break;
            case CombatEndedEvent:
            case ZoneChangedEvent:
                _inCombat = false;
                break;
        }
    }

    public override void PreDraw()
    {
        IsOpen = _inCombat && _config.ShowDebuffHud;
    }

    public override void Draw()
    {
        var lp = _objectTable.LocalPlayer;
        if (lp is null)
        {
            ImGui.TextDisabled("（プレイヤー情報なし）");
            return;
        }

        _rowBuffer.Clear();
        foreach (var status in lp.StatusList)
        {
            if (status is null || status.StatusId == 0)
            {
                continue;
            }
            var (name, isDetrimental) = ResolveStatus(status.StatusId);
            if (!isDetrimental)
            {
                continue;
            }
            _rowBuffer.Add(new DebuffRow(status.StatusId, name, status.RemainingTime, status.Param));
        }

        if (_rowBuffer.Count == 0)
        {
            ImGui.TextDisabled("デバフなし");
            return;
        }

        DebuffHudPolicy.SelectDisplayRows(_rowBuffer, _displayBuffer);

        foreach (var row in _displayBuffer)
        {
            var remaining = DebuffHudPolicy.FormatRemaining(row.RemainingSec);
            var label = row.Stacks > 0 ? $"{row.Name} ×{row.Stacks}" : row.Name;
            // 残り 5 秒未満は赤、それ以外は白
            var color = row.RemainingSec > 0f && row.RemainingSec < WarnSec
                ? new Vector4(1f, 0.35f, 0.35f, 1f)
                : new Vector4(1f, 1f, 1f, 1f);
            ImGui.TextColored(color, label);
            if (remaining.Length > 0)
            {
                ImGui.SameLine(170f * ImGuiHelpers.GlobalScale);
                ImGui.TextColored(color, remaining);
            }
        }
    }

    private (string Name, bool IsDetrimental) ResolveStatus(uint statusId)
    {
        if (_statusCache.TryGetValue(statusId, out var cached))
        {
            return cached;
        }
        var resolved = ResolveStatusFromLumina(statusId);
        _statusCache[statusId] = resolved;
        return resolved;
    }

    private (string Name, bool IsDetrimental) ResolveStatusFromLumina(uint statusId)
    {
        // StatusCapture.cs の名前解決と同じ流儀（Lumina Status シート参照）。
        // StatusCategory: 1=強化, 2=弱体。
        try
        {
            var sheet = _dataManager.GetExcelSheet<Status>();
            if (sheet.TryGetRow(statusId, out var row))
            {
                var name = row.Name.ToString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = $"Status#{statusId}";
                }
                return (name, row.StatusCategory == 2);
            }
        }
        catch
        {
            // シート未ロード等。次フレームも失敗するならキャッシュされた fallback のまま。
        }
        return ($"Status#{statusId}", false);
    }
}
