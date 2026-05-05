using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FfxivEchoes.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration _configuration;

    public ConfigWindow(Plugin plugin)
        : base("FFXIV Echoes 設定###ffxiv-echoes-config")
    {
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        Size = new Vector2(360, 160);
        SizeCondition = ImGuiCond.FirstUseEver;

        _configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var debug = _configuration.DebugMode;
        if (ImGui.Checkbox("デバッグモード", ref debug))
        {
            _configuration.DebugMode = debug;
            _configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("詳細な設定項目は M2 以降で順次追加");
    }
}
