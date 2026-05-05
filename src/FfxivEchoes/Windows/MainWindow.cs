using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FfxivEchoes.Windows.Tabs;

namespace FfxivEchoes.Windows;

public sealed class MainWindow : Window, IDisposable
{
    // 初回ユーザーが先に見る場所として「使い方」をデフォルトに
    public const string DefaultTabId = "help";

    private readonly IReadOnlyList<ITab> _tabs;
    private string? _focusTabId;

    public MainWindow(IReadOnlyList<ITab> tabs, Tabs.TabContext? tabContext = null)
        : base("FFXIV Echoes 設定###ffxiv-echoes-main",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 460),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        _tabs = tabs;
        _tabContext = tabContext;
    }

    private readonly Tabs.TabContext? _tabContext;

    public void Dispose()
    {
        // タブが IDisposable を実装していれば破棄（LiveHudTab の event subscription など）
        foreach (var tab in _tabs)
        {
            if (tab is IDisposable d)
            {
                try { d.Dispose(); } catch { /* 破棄時の例外は無視 */ }
            }
        }
    }

    /// <summary>
    /// 設定ウィンドウを開く。<paramref name="tabId"/> 指定時は次フレームでそのタブにフォーカス。
    /// </summary>
    public void Open(string? tabId = null)
    {
        _focusTabId = tabId;
        IsOpen = true;
    }

    public override void Draw()
    {
        // タブ間からのフォーカス要求を吸収
        if (_tabContext?.PendingFocusTab is { } pending)
        {
            _focusTabId = pending;
            _tabContext.PendingFocusTab = null;
        }

        if (ImGui.BeginTabBar("##ffxiv-echoes-tabs", ImGuiTabBarFlags.Reorderable))
        {
            foreach (var tab in _tabs)
            {
                var flags = (_focusTabId is not null && tab.Id == _focusTabId)
                    ? ImGuiTabItemFlags.SetSelected
                    : ImGuiTabItemFlags.None;

                if (ImGui.BeginTabItem(tab.Title, flags))
                {
                    ImGui.Spacing();
                    tab.Draw();
                    ImGui.EndTabItem();
                }
            }

            ImGui.EndTabBar();
        }

        // 1 フレーム消費したら自動でフォーカス指定をクリア
        _focusTabId = null;
    }
}
