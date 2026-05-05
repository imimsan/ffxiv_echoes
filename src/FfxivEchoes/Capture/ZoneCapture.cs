using System;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using Lumina.Excel.Sheets;

namespace FfxivEchoes.Capture;

/// <summary>
/// <see cref="IClientState.TerritoryChanged"/> を購読して <see cref="ZoneChangedEvent"/> を発行する。
/// </summary>
public sealed class ZoneCapture : IDisposable
{
    private readonly IClientState _clientState;
    private readonly IDataManager _dataManager;
    private readonly IEventBus _bus;
    private readonly IPluginLog _log;

    public ZoneCapture(IClientState clientState, IDataManager dataManager, IEventBus bus, IPluginLog log)
    {
        _clientState = clientState;
        _dataManager = dataManager;
        _bus = bus;
        _log = log;

        _clientState.TerritoryChanged += OnTerritoryChanged;
    }

    public void Dispose()
    {
        _clientState.TerritoryChanged -= OnTerritoryChanged;
    }

    /// <summary>
    /// プラグイン読み込み時に既にゾーン内にいる場合のため、現在の territory を
    /// 一度だけ ZoneChangedEvent として発行する。すべての subscriber が attach
    /// された後に Plugin.cs から呼ぶこと。
    /// </summary>
    public void PublishInitialState()
    {
        var territoryId = _clientState.TerritoryType;
        if (territoryId == 0)
        {
            return;
        }
        var name = ResolveTerritoryName(territoryId);
        _log.Information("[FfxivEchoes] 初期ゾーン → {Id} {Name}", territoryId, name);
        _bus.Publish(new ZoneChangedEvent(DateTimeOffset.UtcNow, territoryId, name));
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        var name = ResolveTerritoryName(territoryId);
        _log.Debug("[FfxivEchoes] ZoneChanged → {Id} {Name}", territoryId, name);
        _bus.Publish(new ZoneChangedEvent(DateTimeOffset.UtcNow, territoryId, name));
    }

    private string ResolveTerritoryName(uint territoryId)
    {
        try
        {
            var sheet = _dataManager.GetExcelSheet<TerritoryType>();
            if (sheet.TryGetRow(territoryId, out var row))
            {
                var placeName = row.PlaceName.Value.Name.ToString();
                return string.IsNullOrEmpty(placeName) ? $"Territory#{territoryId}" : placeName;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] Territory 名の解決に失敗 (id={Id})", territoryId);
        }
        return $"Territory#{territoryId}";
    }
}
