using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using Lumina.Excel.Sheets;

namespace FfxivEchoes.Recording;

/// <summary>
/// イベントバスを購読し、戦闘単位で <see cref="RecordingSession"/> を作成・破棄する。
/// 出力先は <c>{ConfigDirectory}/recordings/{zone}/{datetime}.jsonl</c>（SPEC.md §2.3 / §9.2）。
/// </summary>
public sealed class BattleRecorder : IDisposable
{
    private const string RecordingsDirName = "recordings";

    private readonly RecordingController _controller;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPartyList _partyList;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly IPlayerState _playerState;
    private readonly IDataManager _dataManager;
    private readonly IPluginLog _log;

    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;
    private readonly IDisposable _zoneChangeSub;
    private readonly IDisposable _allEventsSub;

    private RecordingSession? _session;
    private string _currentZone = "Unknown";

    public BattleRecorder(
        IEventBus bus,
        RecordingController controller,
        IDalamudPluginInterface pluginInterface,
        IPartyList partyList,
        IClientState clientState,
        IObjectTable objectTable,
        IPlayerState playerState,
        IDataManager dataManager,
        IPluginLog log)
    {
        _controller = controller;
        _pluginInterface = pluginInterface;
        _partyList = partyList;
        _clientState = clientState;
        _objectTable = objectTable;
        _playerState = playerState;
        _dataManager = dataManager;
        _log = log;

        _currentZone = ResolveCurrentZoneName();

        _combatStartSub = bus.Subscribe<CombatStartedEvent>(OnCombatStart);
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(OnCombatEnd);
        _zoneChangeSub = bus.Subscribe<ZoneChangedEvent>(OnZoneChange);
        _allEventsSub = bus.SubscribeAll(OnAnyEvent);
    }

    public bool IsRecording => _session is not null;
    public string? CurrentFilePath => _session?.FilePath;
    public int CurrentEventCount => _session?.EventCount ?? 0;

    public void Dispose()
    {
        _allEventsSub.Dispose();
        _zoneChangeSub.Dispose();
        _combatEndSub.Dispose();
        _combatStartSub.Dispose();

        _session?.Dispose();
        _session = null;
    }

    private void OnZoneChange(ZoneChangedEvent ev)
    {
        _currentZone = string.IsNullOrEmpty(ev.ZoneName) ? "Unknown" : ev.ZoneName;
    }

    private void OnCombatStart(CombatStartedEvent ev)
    {
        if (_session is not null)
        {
            _log.Warning("[FfxivEchoes] 既存セッションが残ったまま CombatStarted を受信。クローズしてから新規セッションを開始");
            _session.Dispose();
            _session = null;
        }

        if (!_controller.ShouldRecord(_currentZone))
        {
            return;
        }

        try
        {
            var dir = Path.Combine(_pluginInterface.ConfigDirectory.FullName, RecordingsDirName, SanitizePathSegment(_currentZone));
            var fileName = $"{ev.Timestamp.ToLocalTime():yyyy-MM-dd_HH-mm-ss}.jsonl";
            var filePath = Path.Combine(dir, fileName);

            var party = CollectParty();
            var pluginVersion = _pluginInterface.Manifest.AssemblyVersion.ToString();

            _session = new RecordingSession(filePath, _currentZone, ev.Timestamp, pluginVersion, party, _log);
            _log.Information("[FfxivEchoes] 録画開始：{Path} (zone={Zone}, party={N})",
                filePath, _currentZone, party.Count);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] 録画セッションの作成に失敗");
            _session?.Dispose();
            _session = null;
        }
    }

    private void OnCombatEnd(CombatEndedEvent ev)
    {
        var s = _session;
        if (s is null)
        {
            return;
        }
        s.WriteEvent(ev);
        s.Dispose();
        _session = null;
    }

    private void OnAnyEvent(IGameEvent ev)
    {
        if (_session is null)
        {
            return;
        }
        if (ev is CombatEndedEvent)
        {
            // OnCombatEnd で既に書き込み済み
            return;
        }
        // CombatStartedEvent は録画ファイルの先頭イベントとして書きたいので通す
        _session.WriteEvent(ev);
    }

    private List<PartyMemberInfo> CollectParty()
    {
        var party = new List<PartyMemberInfo>();
        var localPlayer = _objectTable.LocalPlayer;

        if (_partyList.Length == 0)
        {
            // ソロ：自分のみ
            if (localPlayer is not null)
            {
                party.Add(BuildSelf(localPlayer));
            }
            return party;
        }

        var localContentId = _playerState.IsLoaded ? _playerState.ContentId : 0UL;
        var seenSelf = false;
        foreach (var member in _partyList)
        {
            var name = member.Name.TextValue;
            var job = member.ClassJob.Value;
            var isSelf = localContentId != 0 && unchecked((ulong)member.ContentId) == localContentId;
            if (isSelf)
            {
                seenSelf = true;
                name = localPlayer?.Name.TextValue ?? name;
            }
            party.Add(new PartyMemberInfo(
                Name: name,
                Job: job.Abbreviation.ToString(),
                Role: ResolveRoleName(job),
                SubRole: null,
                ObjectId: unchecked((uint)member.EntityId),
                IsSelf: isSelf));
        }

        if (!seenSelf && localPlayer is not null)
        {
            party.Insert(0, BuildSelf(localPlayer));
        }

        return party;
    }

    private static PartyMemberInfo BuildSelf(IPlayerCharacter localPlayer)
    {
        var job = localPlayer.ClassJob.Value;
        return new PartyMemberInfo(
            Name: localPlayer.Name.TextValue,
            Job: job.Abbreviation.ToString(),
            Role: ResolveRoleName(job),
            SubRole: null,
            ObjectId: unchecked((uint)localPlayer.GameObjectId),
            IsSelf: true);
    }

    private static string ResolveRoleName(ClassJob job)
    {
        // Lumina ClassJob.Role: 0=Non-combat, 1=Tank, 2=MeleeDPS, 3=Ranged/Caster, 4=Healer
        return job.Role switch
        {
            1 => "Tank",
            4 => "Healer",
            2 or 3 => "DPS",
            _ => "Unknown",
        };
    }

    private string ResolveCurrentZoneName()
    {
        try
        {
            var territoryId = _clientState.TerritoryType;
            if (territoryId == 0)
            {
                return "Unknown";
            }
            var sheet = _dataManager.GetExcelSheet<TerritoryType>();
            if (sheet.TryGetRow(territoryId, out var row))
            {
                var name = row.PlaceName.Value.Name.ToString();
                return string.IsNullOrEmpty(name) ? $"Territory#{territoryId}" : name;
            }
            return $"Territory#{territoryId}";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string SanitizePathSegment(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "Unknown";
        }
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }
}
