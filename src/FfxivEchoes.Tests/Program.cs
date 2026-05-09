using FfxivEchoes.Profiles;
using FfxivEchoes.Capture;
using FfxivEchoes.Recording;
using FfxivEchoes.SafeZone;
using FfxivEchoes.Triggers;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Events;
using FfxivEchoes.Windows;
using System.Numerics;

var tests = new List<(string Name, Action Body)>
{
    ("AggregateFiles keeps repeated cast timings", AggregateFiles_KeepsRepeatedCastTimings),
    ("AggregateFiles merges repeated fights by occurrence index", AggregateFiles_MergesRepeatedFightsByOccurrence),
    ("AggregateFiles clusters divergent repeated events by time", AggregateFiles_ClustersDivergentRepeatedEventsByTime),
    ("BuildCastPredictions expands occurrences and filters covered or party casts", BuildCastPredictions_ExpandsAndFilters),
    ("BuildCastPredictions exposes earliest time for safer warnings", BuildCastPredictions_ExposesEarliestTimeForWarnings),
    ("BuildTimelinePredictions defaults to mechanic timeline only", BuildTimelinePredictions_DefaultsToMechanicTimelineOnly),
    ("BuildTimelinePredictions can opt into enemy instant events", BuildTimelinePredictions_CanOptIntoEnemyInstantEvents),
    ("BuildTimelinePredictions filters player events by party object id", BuildTimelinePredictions_FiltersPlayerEventsByObjectId),
    ("BuildCastPredictions keeps boss casts targeting party members", BuildCastPredictions_KeepsBossCastsTargetingPartyMembers),
    ("BuildTimelinePredictions keeps boss actions targeting party members", BuildTimelinePredictions_KeepsBossActionsTargetingPartyMembers),
    ("BuildCastPredictions keeps source for boss selection", BuildCastPredictions_KeepsSourceForBossSelection),
    ("ActionUsedEvent serializes and aggregates as action_used", ActionUsedEvent_SerializesAndAggregates),
    ("Object events aggregate by object identity", ObjectEvents_AggregateByIdentity),
    ("AoeResolver guesses safe minimap gimmicks conservatively", AoeResolver_GuessesGimmicksConservatively),
    ("AutoSafeCallPlanner creates practical callouts", AutoSafeCallPlanner_CreatesCallouts),
    ("AutoSafeCallPlanner creates visual telegraph for large circle AoE", AutoSafeCallPlanner_CreatesVisualForLargeCircleAoe),
    ("AutoSafeCallPlanner recognizes named half arena attacks", AutoSafeCallPlanner_RecognizesNamedHalfArenaAttacks),
    ("StrategyPlanResolver converts active party strategy into notes and actions", StrategyPlanResolver_BuildsNotesAndActions),
    ("StrategyPlanResolver creates editable mechanic draft from learned timeline prediction", StrategyPlanResolver_CreatesMechanicDraftFromPrediction),
    ("StrategyPlanResolver does not invent spread markers for empty drafts", StrategyPlanResolver_DoesNotInventSpreadMarkersForEmptyDrafts),
    ("StrategyPlanResolver selects nearest registered mechanic for repeated predictions", StrategyPlanResolver_SelectsNearestMechanicForPrediction),
    ("TimelineNoteResolver resolves object attached notes from recording aggregate", TimelineNoteResolver_ResolvesObjectAttachedNotes),
    ("SyncOffsetTracker accepts large recording sync jumps for phase skips", SyncOffsetTracker_AcceptsLargeRecordingSyncJumps),
    ("SyncOffsetTracker chooses nearest sync point within tolerance", SyncOffsetTracker_ChoosesNearestSyncPointWithinTolerance),
    ("PredictedCastReminderService drops stale skipped predictions", PredictedCastReminderService_DropsStaleSkippedPredictions),
    ("PredictedCastReminderService builds visible default warning actions", PredictedCastReminderService_BuildsVisibleDefaultWarningActions),
    ("Profile keeps all-enabled and custom trigger selection distinct", Profile_CustomSelectionIsExplicit),
    ("BossSelectionPolicy keeps multiple bosses and preferred source", BossSelectionPolicy_KeepsMultipleBosses),
    ("InstantActionPolicy allows repeated AA after repeat window", InstantActionPolicy_AllowsRepeatedAaAfterWindow),
    ("InstantActionPolicy suppresses duplicate AA inside repeat window", InstantActionPolicy_SuppressesDuplicateAaInsideWindow),
    ("AutoAttackCadenceTracker predicts AA after two hits", AutoAttackCadenceTracker_PredictsAfterTwoHits),
    ("AutoAttackCadenceTracker keeps cadence across target swaps", AutoAttackCadenceTracker_KeepsCadenceAcrossTargetSwaps),
    ("AutoAttackCadenceTracker warns once before next AA", AutoAttackCadenceTracker_WarnsOnceBeforeNextAa),
    ("AttackDisplayPolicy respects all-attack and auto-attack toggles", AttackDisplayPolicy_RespectsToggles),
    ("AttackPulseLabelPolicy hides action names for combat HUD", AttackPulseLabelPolicy_HidesActionNames),
    ("ArenaCenterResolver uses actor bounds when default center is wrong", ArenaCenterResolver_UsesActorBoundsWhenDefaultCenterWrong),
    ("ArenaCenterResolver keeps trusted default center", ArenaCenterResolver_KeepsTrustedDefaultCenter),
    ("ArenaProjection maps FFXIV boss direction to minimap", ArenaProjection_MapsBossDirectionToMinimap),
    ("ArenaProjection clamps out-of-arena positions without changing direction", ArenaProjection_ClampsOutOfArenaPositionsWithoutChangingDirection),
    ("ArenaProjection uses long range for half arena cones", ArenaProjection_UsesLongRangeForHalfArenaCones),
    ("AutoSettings enables early prediction by default", AutoSettings_EnablesEarlyPredictionByDefault),
    ("Minimap display priority keeps real AoE above noise", MinimapDisplayPriority_KeepsRealAoeAboveNoise),
};

var failed = 0;
foreach (var (name, body) in tests)
{
    try
    {
        body();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

if (failed > 0)
{
    Environment.ExitCode = 1;
}

static void AggregateFiles_KeepsRepeatedCastTimings()
{
    var dir = CreateTempDir();
    var path = Path.Combine(dir, "battle.jsonl");
    File.WriteAllLines(path, new[]
    {
        """{"meta":true,"zone":"Zone A","start_time":"2026-05-06T00:00:00.000Z","plugin_version":"0.1.0","party":[{"name":"Self","job":"PLD","role":"Tank"}]}""",
        """{"time":3.5,"type":"cast_start","source":"Boss","cast_id":"0x1234","cast_name":"First","cast_time":4.0}""",
        """{"time":42.0,"type":"cast_start","source":"Boss","cast_id":"0x1234","cast_name":"First","cast_time":4.0}""",
        """{"time":12.0,"type":"cast_start","source":"Self","cast_id":"0x5678","cast_name":"Player Skill","cast_time":2.0}""",
    });

    var agg = RecordingAggregationReader.AggregateFiles(new[] { path });
    var bossCast = Single(agg.Events, e => e.Key.Id == "0x1234");

    Equal(2, bossCast.Count, "boss cast count");
    SequenceEqual(new[] { 3.5, 42.0 }, bossCast.ObservedTimesSeconds, "boss cast times");
    False(bossCast.IsPartySource, "boss cast should not be marked as party source");

    var playerCast = Single(agg.Events, e => e.Key.Id == "0x5678");
    True(playerCast.IsPartySource, "player cast should be marked as party source");
}

static void AggregateFiles_MergesRepeatedFightsByOccurrence()
{
    var dir = CreateTempDir();
    var path1 = Path.Combine(dir, "battle1.jsonl");
    var path2 = Path.Combine(dir, "battle2.jsonl");

    File.WriteAllLines(path1, new[]
    {
        """{"meta":true,"zone":"Zone A","start_time":"2026-05-06T00:00:00.000Z","plugin_version":"0.1.0","party":[]}""",
        """{"time":5.0,"type":"cast_start","source":"Boss","cast_id":"0x1111","cast_name":"Repeat","cast_time":4.0}""",
        """{"time":50.0,"type":"cast_start","source":"Boss","cast_id":"0x1111","cast_name":"Repeat","cast_time":4.0}""",
    });
    File.WriteAllLines(path2, new[]
    {
        """{"meta":true,"zone":"Zone A","start_time":"2026-05-06T00:10:00.000Z","plugin_version":"0.1.0","party":[]}""",
        """{"time":6.0,"type":"cast_start","source":"Boss","cast_id":"0x1111","cast_name":"Repeat","cast_time":4.0}""",
        """{"time":51.0,"type":"cast_start","source":"Boss","cast_id":"0x1111","cast_name":"Repeat","cast_time":4.0}""",
    });

    var agg = RecordingAggregationReader.AggregateFiles(new[] { path1, path2 });
    var repeat = Single(agg.Events, e => e.Key.Id == "0x1111");

    Equal(4, repeat.Count, "total observed cast count");
    Equal(2, repeat.Occurrences.Count, "merged occurrence count");
    Equal(5.5, repeat.Occurrences[0].RepresentativeTimeSeconds, "first occurrence representative time");
    Equal(50.5, repeat.Occurrences[1].RepresentativeTimeSeconds, "second occurrence representative time");
    Equal(2, repeat.Occurrences[0].SeenCount, "first occurrence seen count");
    Equal(2, repeat.Occurrences[1].SeenCount, "second occurrence seen count");

    var predictions = RecordingPredictionPlanner.BuildCastPredictions(agg);
    Equal(2, predictions.Count, "prediction occurrence count");
    True(predictions.All(p => Math.Abs(p.Confidence - 1.0) < 0.001), "fully observed confidence");
    True(predictions.All(p => Math.Abs(p.TimeJitterSeconds - 1.0) < 0.001), "time jitter");
}

static void AggregateFiles_ClustersDivergentRepeatedEventsByTime()
{
    var dir = CreateTempDir();
    var path1 = Path.Combine(dir, "battle1.jsonl");
    var path2 = Path.Combine(dir, "battle2.jsonl");

    File.WriteAllLines(path1, new[]
    {
        """{"meta":true,"zone":"Zone A","start_time":"2026-05-06T00:00:00.000Z","plugin_version":"0.1.0","party":[]}""",
        """{"time":10.0,"type":"cast_start","source":"Boss","cast_id":"0x4444","cast_name":"Branch Cast","cast_time":4.0}""",
        """{"time":90.0,"type":"cast_start","source":"Boss","cast_id":"0x4444","cast_name":"Branch Cast","cast_time":4.0}""",
    });
    File.WriteAllLines(path2, new[]
    {
        """{"meta":true,"zone":"Zone A","start_time":"2026-05-06T00:10:00.000Z","plugin_version":"0.1.0","party":[]}""",
        """{"time":12.0,"type":"cast_start","source":"Boss","cast_id":"0x4444","cast_name":"Branch Cast","cast_time":4.0}""",
        """{"time":55.0,"type":"cast_start","source":"Boss","cast_id":"0x4444","cast_name":"Branch Cast","cast_time":4.0}""",
        """{"time":92.0,"type":"cast_start","source":"Boss","cast_id":"0x4444","cast_name":"Branch Cast","cast_time":4.0}""",
    });

    var agg = RecordingAggregationReader.AggregateFiles(new[] { path1, path2 });
    var branch = Single(agg.Events, e => e.Key.Id == "0x4444");

    Equal(5, branch.Count, "total observed cast count");
    Equal(3, branch.Occurrences.Count, "clustered occurrence count");
    SequenceEqual(new[] { 11.0, 55.0, 91.0 },
        branch.Occurrences.Select(o => o.RepresentativeTimeSeconds).ToArray(),
        "clustered occurrence times");
    SequenceEqual(new[] { 2, 1, 2 },
        branch.Occurrences.Select(o => o.SeenCount).ToArray(),
        "cluster seen counts");

    var predictions = RecordingPredictionPlanner.BuildCastPredictions(agg);
    SequenceEqual(new[] { 1.0, 0.5, 1.0 },
        predictions.Select(p => Math.Round(p.Confidence, 3)).ToArray(),
        "cluster confidence");
}

static void BuildCastPredictions_ExpandsAndFilters()
{
    var agg = new AggregatedEvents(new[]
    {
        new AggregatedEvent(new EventKey("cast_start", "0x1111", "Tankbuster", "Boss", null), 2, 5)
        {
            ObservedTimesSeconds = new[] { 5.0, 50.0 },
            Occurrences = new[]
            {
                new AggregatedOccurrence(0, 5.0, 1, new[] { 5.0 }),
                new AggregatedOccurrence(1, 50.0, 1, new[] { 50.0 }),
            },
        },
        new AggregatedEvent(new EventKey("cast_start", "0x2222", "Already Covered", "Boss", null), 1, 10)
        {
            ObservedTimesSeconds = new[] { 10.0 },
        },
        new AggregatedEvent(new EventKey("cast_start", "0x3333", "Player Skill", "Self", null), 1, 20)
        {
            ObservedTimesSeconds = new[] { 20.0 },
            IsPartySource = true,
        },
    }, 1, 4, 1);

    var predictions = RecordingPredictionPlanner.BuildCastPredictions(
        agg,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0x2222" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Self" });

    Equal(2, predictions.Count, "prediction count");
    SequenceEqual(new[] { 5.0, 50.0 }, predictions.Select(p => p.RelativeSeconds).ToArray(), "prediction times");
    True(predictions.All(p => p.CastId == "0x1111"), "only boss cast should remain");
    True(predictions.All(p => Math.Abs(p.Confidence - 1.0) < 0.001), "single-battle confidence should be 1.0");
}

static void BuildCastPredictions_ExposesEarliestTimeForWarnings()
{
    var dir = CreateTempDir();
    var path1 = Path.Combine(dir, "battle1.jsonl");
    var path2 = Path.Combine(dir, "battle2.jsonl");

    File.WriteAllLines(path1, new[]
    {
        """{"meta":true,"zone":"Zone A","start_time":"2026-05-06T00:00:00.000Z","plugin_version":"0.1.0","party":[]}""",
        """{"time":10.0,"type":"cast_start","source":"Boss","cast_id":"0x5555","cast_name":"Variable","cast_time":4.0}""",
    });
    File.WriteAllLines(path2, new[]
    {
        """{"meta":true,"zone":"Zone A","start_time":"2026-05-06T00:10:00.000Z","plugin_version":"0.1.0","party":[]}""",
        """{"time":14.0,"type":"cast_start","source":"Boss","cast_id":"0x5555","cast_name":"Variable","cast_time":4.0}""",
    });

    var agg = RecordingAggregationReader.AggregateFiles(new[] { path1, path2 });
    var prediction = Single(RecordingPredictionPlanner.BuildCastPredictions(agg), p => p.CastId == "0x5555");

    Equal(12.0, prediction.RelativeSeconds, "median prediction time");
    Equal(10.0, prediction.EarliestObservedSeconds, "earliest prediction time");
    Equal(14.0, prediction.LatestObservedSeconds, "latest prediction time");
    Equal(0.0, PredictedCastReminderService.CalculateFireAtSeconds(prediction, 12.0), "warning fires from earliest observed time");
}

static void BuildTimelinePredictions_DefaultsToMechanicTimelineOnly()
{
    var agg = new AggregatedEvents(new[]
    {
        new AggregatedEvent(new EventKey("cast_start", "0x1000", "Raidwide", "Boss", null), 1, 5)
        {
            Occurrences = new[] { new AggregatedOccurrence(0, 5.0, 1, new[] { 5.0 }) },
        },
        new AggregatedEvent(new EventKey("action_used", "0x2000", "Fight or Flight", "Self", null), 1, 8)
        {
            Occurrences = new[] { new AggregatedOccurrence(0, 8.0, 1, new[] { 8.0 }) },
        },
        new AggregatedEvent(new EventKey("status_gain", "3000", "Goring Blade Ready", null, "Self"), 1, 12)
        {
            Occurrences = new[] { new AggregatedOccurrence(0, 12.0, 1, new[] { 12.0 }) },
        },
        new AggregatedEvent(new EventKey("object_appear", "4000", "Tower", "Tower", null), 1, 15)
        {
            Occurrences = new[] { new AggregatedOccurrence(0, 15.0, 1, new[] { 15.0 }) },
        },
        new AggregatedEvent(new EventKey("cast_start", "0x9999", "Player Skill", "Self", null), 1, 20)
        {
            IsPartySource = true,
        },
    }, 1, 5, 1);

    var predictions = RecordingPredictionPlanner.BuildTimelinePredictions(
        agg,
        partyMembers: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Self" });

    SequenceEqual(new[] { "cast_start", "object_appear" },
        predictions.Select(p => p.EventType).ToArray(),
        "timeline event types");
    SequenceEqual(new[] { 5.0, 15.0 },
        predictions.Select(p => p.RelativeSeconds).ToArray(),
        "timeline times");
    Equal("Tower", predictions[1].Label, "object label");
}

static void BuildTimelinePredictions_CanOptIntoEnemyInstantEvents()
{
    var agg = new AggregatedEvents(new[]
    {
        new AggregatedEvent(new EventKey("action_used", "0x2000", "Shared Buster", "Boss", null), 1, 8)
        {
            Occurrences = new[] { new AggregatedOccurrence(0, 8.0, 1, new[] { 8.0 }) },
        },
        new AggregatedEvent(new EventKey("status_gain", "3000", "Vulnerability", null, "Boss"), 1, 12)
        {
            Occurrences = new[] { new AggregatedOccurrence(0, 12.0, 1, new[] { 12.0 }) },
        },
    }, 1, 2, 1);

    var predictions = RecordingPredictionPlanner.BuildTimelinePredictions(
        agg,
        includeActions: true,
        includeStatusGains: true);

    SequenceEqual(new[] { "action_used", "status_gain" },
        predictions.Select(p => p.EventType).ToArray(),
        "opt-in timeline event types");
}

static void BuildTimelinePredictions_FiltersPlayerEventsByObjectId()
{
    var dir = CreateTempDir();
    var path = Path.Combine(dir, "battle.jsonl");
    File.WriteAllLines(path, new[]
    {
        """{"meta":true,"zone":"Zone A","start_time":"2026-05-06T00:00:00.000Z","plugin_version":"0.1.0","party":[{"name":"Player One","job":"PLD","role":"Tank","object_id":2001,"is_self":true}]}""",
        """{"time":5.0,"type":"action_used","source":"Player One","source_id":2001,"action_id":"0x123","action_name":"ロイエ","auto_attack":false,"target_id":3001}""",
        """{"time":6.0,"type":"status_gain","target":"Player One","target_id":2001,"status_id":2673,"status_name":"ロイエ実行可","duration":15,"stacks":0}""",
        """{"time":10.0,"type":"cast_start","source":"Boss","source_id":3001,"cast_id":"0x9999","cast_name":"Boss Cast","cast_time":3.0}""",
    });

    var agg = RecordingAggregationReader.AggregateFiles(new[] { path });
    var predictions = RecordingPredictionPlanner.BuildTimelinePredictions(agg, includeActions: true);

    Equal(1, predictions.Count, "only enemy event should remain");
    Equal("Boss Cast", predictions[0].Label, "enemy cast label");
}

static void BuildCastPredictions_KeepsBossCastsTargetingPartyMembers()
{
    var dir = CreateTempDir();
    var path = Path.Combine(dir, "battle.jsonl");
    File.WriteAllLines(path, new[]
    {
        """{"meta":true,"zone":"Zone A","start_time":"2026-05-06T00:00:00.000Z","plugin_version":"0.1.0","party":[{"name":"Player One","job":"PLD","role":"Tank","object_id":2001,"is_self":true}]}""",
        """{"time":10.0,"type":"cast_start","source":"Boss","source_id":3001,"target_id":2001,"cast_id":"0x7777","cast_name":"Tankbuster","cast_time":4.0}""",
    });

    var agg = RecordingAggregationReader.AggregateFiles(new[] { path });
    var predictions = RecordingPredictionPlanner.BuildCastPredictions(agg);

    Equal(1, predictions.Count, "boss cast targeting party should remain");
    Equal("Tankbuster", predictions[0].Label, "boss cast label");
}

static void BuildTimelinePredictions_KeepsBossActionsTargetingPartyMembers()
{
    var dir = CreateTempDir();
    var path = Path.Combine(dir, "battle.jsonl");
    File.WriteAllLines(path, new[]
    {
        """{"meta":true,"zone":"Zone A","start_time":"2026-05-06T00:00:00.000Z","plugin_version":"0.1.0","party":[{"name":"Player One","job":"PLD","role":"Tank","object_id":2001,"is_self":true}]}""",
        """{"time":10.0,"type":"action_used","source":"Boss","source_id":3001,"target_id":2001,"action_id":"0x8888","action_name":"Shared Buster","auto_attack":false}""",
    });

    var agg = RecordingAggregationReader.AggregateFiles(new[] { path });
    var predictions = RecordingPredictionPlanner.BuildTimelinePredictions(agg, includeActions: true);

    Equal(1, predictions.Count, "boss action targeting party should remain");
    Equal("Shared Buster", predictions[0].Label, "boss action label");
}

static void BuildCastPredictions_KeepsSourceForBossSelection()
{
    var dir = CreateTempDir();
    var path = Path.Combine(dir, "battle.jsonl");
    File.WriteAllLines(path, new[]
    {
        """{"meta":true,"zone":"Zone A","start_time":"2026-05-06T00:00:00.000Z","plugin_version":"0.1.0","party":[]}""",
        """{"time":8.0,"type":"cast_start","source":"Nael Deus Darnus","source_id":3001,"cast_id":"0x179C","cast_name":"ヒートウィング","cast_time":4.0}""",
    });

    var agg = RecordingAggregationReader.AggregateFiles(new[] { path });
    var prediction = Single(RecordingPredictionPlanner.BuildCastPredictions(agg), p => p.CastId == "0x179C");

    Equal("Nael Deus Darnus", prediction.Source, "prediction source");
}

static void ActionUsedEvent_SerializesAndAggregates()
{
    var dir = CreateTempDir();
    var path = Path.Combine(dir, "battle.jsonl");
    var start = DateTimeOffset.Parse("2026-05-06T00:00:00.000Z");
    var action = new ActionUsedEvent(
        start.AddSeconds(7.25),
        SourceId: 1001,
        SourceName: "Boss",
        ActionId: 0xABCD,
        ActionName: "Auto Attack",
        TargetId: 2001,
        IsAutoAttack: true);

    File.WriteAllLines(path, new[]
    {
        """{"meta":true,"zone":"Zone A","start_time":"2026-05-06T00:00:00.000Z","plugin_version":"0.1.0","party":[]}""",
        EventSerializer.Serialize(action, start),
    });

    var agg = RecordingAggregationReader.AggregateFiles(new[] { path });
    var observed = Single(agg.Events, e => e.Key.Type == "action_used");

    Equal("0xABCD", observed.Key.Id, "action id");
    Equal("Auto Attack", observed.Key.Name, "action name");
    Equal("Boss", observed.Key.Source, "action source");
    SequenceEqual(new[] { 7.25 }, observed.ObservedTimesSeconds, "action time");
}

static void ObjectEvents_AggregateByIdentity()
{
    var dir = CreateTempDir();
    var path = Path.Combine(dir, "battle.jsonl");
    File.WriteAllLines(path, new[]
    {
        """{"meta":true,"zone":"Zone A","start_time":"2026-05-06T00:00:00.000Z","plugin_version":"0.1.0","party":[]}""",
        """{"time":10.0,"type":"object_appear","object_id":100,"object_name":"Tower","data_id":4000,"x":0,"y":0,"z":0}""",
        """{"time":30.0,"type":"object_appear","object_id":101,"object_name":"Tower","data_id":4000,"x":10,"y":0,"z":10}""",
        """{"time":12.0,"type":"object_appear","object_id":200,"object_name":"Orb","data_id":5000,"x":1,"y":0,"z":1}""",
    });

    var agg = RecordingAggregationReader.AggregateFiles(new[] { path });
    var tower = Single(agg.Events, e => e.Key.Type == "object_appear" && e.Key.Id == "4000");
    var orb = Single(agg.Events, e => e.Key.Type == "object_appear" && e.Key.Id == "5000");

    Equal("Tower", tower.Key.Name, "tower name");
    Equal(2, tower.Count, "tower count");
    SequenceEqual(new[] { 10.0, 30.0 }, tower.ObservedTimesSeconds, "tower times");
    Equal("Orb", orb.Key.Name, "orb name");
}

static void AoeResolver_GuessesGimmicksConservatively()
{
    Equal("inner_circle", AoeResolver.GuessGimmick(2, 8), "target circle is center danger");
    Equal("inner_circle", AoeResolver.GuessGimmick(5, 25), "caster circle is center danger even when large");
    Equal("outer_ring", AoeResolver.GuessGimmick(6, 20), "donut is inner safe");
    Equal("cone", AoeResolver.GuessGimmick(3, 30), "cone cast is cone gimmick");
    Equal("cone", AoeResolver.GuessGimmick(4, 30), "line cast is represented as cone gimmick");
}

static void AutoSafeCallPlanner_CreatesCallouts()
{
    var circle = AutoSafeCallPlanner.Create(new AoeResolver.AoeInfo(8, 5, true), "Explosion");
    NotNull(circle, "circle call");
    Equal("inner_circle", circle!.Gimmick, "circle gimmick");
    Equal("外周安置：Explosion", circle.Callout, "circle callout");
    Equal("外周安置", circle.TtsText, "circle tts");

    var largeCircle = AutoSafeCallPlanner.Create(new AoeResolver.AoeInfo(25, 5, true), "Raidwide");
    Null(largeCircle, "large circle should not produce a safe call");

    var donut = AutoSafeCallPlanner.Create(new AoeResolver.AoeInfo(20, 6, true), "Donut");
    NotNull(donut, "donut call");
    Equal("outer_ring", donut!.Gimmick, "donut gimmick");
    Equal("内側安置：Donut", donut.Callout, "donut callout");

    var cone = AutoSafeCallPlanner.Create(new AoeResolver.AoeInfo(30, 3, true), "Cleave");
    NotNull(cone, "cone call");
    Equal("cone", cone!.Gimmick, "cone gimmick");
    Equal("扇形回避：Cleave", cone.Callout, "cone callout");
    True(cone.IsEstimate, "cone should be marked as estimate");
}

static void AutoSafeCallPlanner_CreatesVisualForLargeCircleAoe()
{
    var safeCall = AutoSafeCallPlanner.Create(new AoeResolver.AoeInfo(25, 5, true), "Raidwide");
    Null(safeCall, "large circle should not force a dodge call");

    var visual = AutoSafeCallPlanner.CreateVisual(new AoeResolver.AoeInfo(25, 5, true), "Raidwide");

    NotNull(visual, "large circle should still be drawn on minimap");
    if (visual is null) return;
    Equal("inner_circle", visual.Gimmick, "large circle visual gimmick");
    Equal("AoE: Raidwide", visual.Callout, "large circle visual label");
    Equal(string.Empty, visual.TtsText, "visual-only telegraph should not speak");
}

static void AutoSafeCallPlanner_RecognizesNamedHalfArenaAttacks()
{
    var heatWing = AutoSafeCallPlanner.CreateKnownByName("ヒートウィング");
    NotNull(heatWing, "heat wing call");
    Equal("half_plane", heatWing!.Gimmick, "heat wing gimmick");
    Equal(180.0, heatWing.FanDeg, "heat wing fan");

    var cauterize = AutoSafeCallPlanner.CreateKnownByName("カータライズ");
    NotNull(cauterize, "cauterize call");
    Equal("half_plane", cauterize!.Gimmick, "cauterize gimmick");
    Equal(180.0, cauterize.FanDeg, "cauterize fan");

    var heatWingById = AutoSafeCallPlanner.CreateKnown(0x179C, "Unknown");
    NotNull(heatWingById, "heat wing cast id 0x179C");
    Equal(180.0, heatWingById!.FanDeg, "heat wing cast id 0x179C fan");

    var heatWingSecondId = AutoSafeCallPlanner.CreateKnown(0x189F, "Unknown");
    NotNull(heatWingSecondId, "heat wing cast id 0x189F");
    Equal(180.0, heatWingSecondId!.FanDeg, "heat wing cast id 0x189F fan");
}

static void StrategyPlanResolver_BuildsNotesAndActions()
{
    var file = new TriggerFile
    {
        ActiveStrategyProfileId = "team-b",
        StrategyProfiles = new List<StrategyProfile>
        {
            new()
            {
                Id = "team-a",
                Name = "Team A",
                Enabled = true,
                Mechanics = new List<MechanicStrategy>
                {
                    new() { Id = "unused", Label = "Unused", Time = 1 },
                },
            },
            new()
            {
                Id = "team-b",
                Name = "Team B",
                Enabled = true,
                ArenaRadius = 22,
                SpreadPositions = new List<StrategyPosition>
                {
                    new() { Slot = "mt", Label = "MT", Role = "mt", X = 0, Z = -16, Color = "#60A5FA" },
                    new() { Slot = "d1", Label = "D1", Role = "melee", X = -12, Z = 12, Color = "#F472B6" },
                },
                Mechanics = new List<MechanicStrategy>
                {
                    new()
                    {
                        Id = "spread-1",
                        Label = "First Spread",
                        AttachedTo = new MatchCondition { CastId = "0x1234", CastName = "Spread Cast" },
                        AdvanceWarningSec = 6,
                        Role = "mt",
                        WarningText = "MT spread north",
                        Gimmick = "scatter",
                        Positions = new List<string> { "mt", "d1" },
                    },
                },
            },
        },
    };

    var profile = StrategyPlanResolver.SelectActiveProfile(file);
    NotNull(profile, "active strategy profile");
    Equal("team-b", profile!.Id, "active profile id");

    var notes = StrategyPlanResolver.BuildTimelineNotes(file);
    Equal(1, notes.Count, "strategy note count");
    Equal("First Spread", notes[0].Label, "strategy note label");
    Equal("0x1234", notes[0].AttachedTo?.CastId, "strategy note attachment");
    Equal("mt", notes[0].Role, "strategy note role");

    var actions = StrategyPlanResolver.BuildReminderActions(profile, profile.Mechanics[0]);
    Equal(2, actions.Count, "strategy action count");
    Equal("tts", actions[0].Type, "first action type");
    Equal("MT spread north", actions[0].Text, "tts text");
    Equal("arena_view", actions[1].Type, "second action type");
    Equal("scatter", actions[1].Gimmick, "arena gimmick");
    Equal(22.0, actions[1].ArenaRadius, "arena radius");
    Equal(2, actions[1].StrategyPositions?.Count ?? 0, "strategy positions");
}

static void StrategyPlanResolver_CreatesMechanicDraftFromPrediction()
{
    var prediction = new RecordingTimelinePrediction(
        EventType: "cast_start",
        RelativeSeconds: 42.5,
        Label: "Limit Cut",
        Id: "0xABCD",
        Source: "Boss",
        Target: null,
        ObservedCount: 3,
        OccurrenceIndex: 1,
        OccurrenceSeenCount: 2,
        Confidence: 0.667,
        TimeJitterSeconds: 1.2);

    var mechanic = StrategyPlanResolver.CreateMechanicDraft(prediction, "limit_cut_2");

    Equal("limit_cut_2", mechanic.Id, "mechanic id");
    Equal("Limit Cut", mechanic.Label, "mechanic label");
    Equal(42.5, mechanic.Time, "mechanic time");
    Equal(5.0, mechanic.AdvanceWarningSec, "mechanic default warning");
    Equal("Limit Cut", mechanic.WarningText, "mechanic warning");
    Equal("Limit Cut", mechanic.Callout, "mechanic callout");
    Equal("0xABCD", mechanic.AttachedTo?.CastId, "attached cast id");
    Equal("Limit Cut", mechanic.AttachedTo?.CastName, "attached cast name");
    Equal("Boss", mechanic.AttachedTo?.Source, "attached source");
    Equal("cast_start", mechanic.SourceEventType, "source event type");
    Equal(3, mechanic.ObservedCount, "observed count");
    Equal(2, mechanic.OccurrenceSeenCount, "occurrence seen count");
    Equal(0.667, mechanic.Confidence, "confidence");
    Equal(1.2, mechanic.TimeJitterSeconds, "time jitter");
}

static void StrategyPlanResolver_DoesNotInventSpreadMarkersForEmptyDrafts()
{
    var profile = new StrategyProfile
    {
        Id = "team",
        Name = "Team",
        SpreadPositions = new List<StrategyPosition>
        {
            new() { Slot = "mt", Label = "MT", X = 0, Z = -12 },
            new() { Slot = "d1", Label = "D1", X = -10, Z = 10 },
        },
    };
    var mechanic = new MechanicStrategy
    {
        Id = "draft",
        Label = "Learned Cast",
        WarningText = "Learned Cast",
    };

    var actions = StrategyPlanResolver.BuildReminderActions(profile, mechanic);

    Equal(1, actions.Count, "draft should only create tts");
    Equal("tts", actions[0].Type, "draft action type");
}

static void StrategyPlanResolver_SelectsNearestMechanicForPrediction()
{
    var file = new TriggerFile
    {
        ActiveStrategyProfileId = "default",
        StrategyProfiles = new List<StrategyProfile>
        {
            new()
            {
                Id = "default",
                Name = "Default",
                Mechanics = new List<MechanicStrategy>
                {
                    new()
                    {
                        Id = "first",
                        Label = "First",
                        Time = 10,
                        AttachedTo = new MatchCondition { CastId = "0x2222", CastName = "Repeat" },
                    },
                    new()
                    {
                        Id = "second",
                        Label = "Second",
                        Time = 90,
                        AttachedTo = new MatchCondition { CastId = "0x2222", CastName = "Repeat" },
                    },
                },
            },
        },
    };

    var prediction = new RecordingPrediction(
        RelativeSeconds: 88,
        Label: "Repeat",
        CastId: "0x2222",
        Source: null,
        ObservedCount: 4,
        OccurrenceIndex: 1,
        OccurrenceSeenCount: 2,
        Confidence: 1,
        TimeJitterSeconds: 1);

    var selected = StrategyPlanResolver.FindMechanicForPrediction(file, prediction);

    NotNull(selected.Profile, "selected profile");
    NotNull(selected.Mechanic, "selected mechanic");
    Equal("second", selected.Mechanic!.Id, "nearest repeated mechanic");
}

static void TimelineNoteResolver_ResolvesObjectAttachedNotes()
{
    var note = new TimelineNote
    {
        Id = "tower-note",
        Label = "Tower",
        AttachedTo = new MatchCondition { Actor = "Tower" },
    };
    var agg = new AggregatedEvents(new[]
    {
        new AggregatedEvent(new EventKey("object_appear", "4000", "Tower", "Tower", null), 1, 33)
        {
            Occurrences = new[] { new AggregatedOccurrence(0, 33.0, 1, new[] { 33.0 }) },
        },
    }, 1, 1, 1);

    var time = TimelineNoteResolver.ResolveTime(note, agg);

    Equal(33.0, time, "object attached time");
}

static void SyncOffsetTracker_AcceptsLargeRecordingSyncJumps()
{
    True(SyncOffsetTracker.ShouldAcceptOffsetJump(
            hasPreviousSync: true,
            currentOffsetSec: 0,
            newOffsetSec: -45,
            allowLargeJump: true),
        "recording sync should allow phase skip jumps");

    False(SyncOffsetTracker.ShouldAcceptOffsetJump(
            hasPreviousSync: true,
            currentOffsetSec: 0,
            newOffsetSec: -45,
            allowLargeJump: false),
        "manual sync point should still reject suspicious jumps");
}

static void SyncOffsetTracker_ChoosesNearestSyncPointWithinTolerance()
{
    var syncPoints = new[]
    {
        new SyncPoint { Id = "first", Type = "cast_start", CastId = "0xAAAA", ExpectedTime = 10.0, Tolerance = 3.0 },
        new SyncPoint { Id = "second", Type = "cast_start", CastId = "0xAAAA", ExpectedTime = 90.0, Tolerance = 5.0 },
    };

    True(SyncOffsetTracker.TryFindBestSyncPointOffset(
            syncPoints,
            actualRelSec: 92.0,
            currentOffsetSec: 0.0,
            actualCastId: 0xAAAA,
            out var offset,
            out var label),
        "nearest sync point should be selected");
    Equal(2.0, offset, "sync offset");
    Equal("sync:second", label, "sync label");

    False(SyncOffsetTracker.TryFindBestSyncPointOffset(
            syncPoints,
            actualRelSec: 50.0,
            currentOffsetSec: 0.0,
            actualCastId: 0xAAAA,
            out _,
            out _),
        "out-of-tolerance sync should be ignored");
}

static void PredictedCastReminderService_DropsStaleSkippedPredictions()
{
    True(PredictedCastReminderService.ShouldFireDuePrediction(
            fireAtRelSec: 88.0,
            eventAtRelSec: 100.0,
            offsetSec: 0.0,
            nowRelSec: 90.0),
        "due warning before event should fire");

    False(PredictedCastReminderService.ShouldFireDuePrediction(
            fireAtRelSec: 88.0,
            eventAtRelSec: 100.0,
            offsetSec: -40.0,
            nowRelSec: 90.0),
        "skipped past event should be dropped");
}

static void PredictedCastReminderService_BuildsVisibleDefaultWarningActions()
{
    var actions = PredictedCastReminderService.BuildDefaultWarningActions("Heat Wing", 12.0);

    Equal(3, actions.Count, "default warning action count");
    Equal("tts", actions[0].Type, "first action is tts");
    Equal("Next: Heat Wing", actions[0].Text, "tts text");
    Equal("overlay_text", actions[1].Type, "second action is overlay text");
    Equal("Next: Heat Wing", actions[1].Text, "overlay text");
    Equal("timer_bar", actions[2].Type, "third action is timer bar");
    Equal("Next", actions[2].Label, "timer label");
    Equal(12.0, actions[2].Duration ?? -1, "timer duration");
}

static void Profile_CustomSelectionIsExplicit()
{
    var profile = new Profile();

    True(profile.IsTriggerActive("Zone A", "t1"), "missing zone means all triggers active");

    profile.ActiveTriggers["Zone A"] = new List<string>();
    True(profile.IsTriggerActive("Zone A", "t1"), "legacy empty list means all triggers active");

    profile.SetCustomTriggerSelection("Zone A", true);
    False(profile.IsTriggerActive("Zone A", "t1"), "explicit custom empty list means no triggers active");

    profile.ActiveTriggers["Zone A"] = new List<string> { "t1" };
    True(profile.IsTriggerActive("Zone A", "t1"), "listed trigger is active");
    False(profile.IsTriggerActive("Zone A", "t2"), "unlisted trigger is inactive");

    profile.SetCustomTriggerSelection("Zone A", false);
    profile.ActiveTriggers.Remove("Zone A");
    True(profile.IsTriggerActive("Zone A", "t2"), "returning to all-enabled removes custom restriction");
}

static void BossSelectionPolicy_KeepsMultipleBosses()
{
    var candidates = new[]
    {
        new BossCandidate(1001, "Alpha", 1_000_000, true),
        new BossCandidate(1002, "Beta", 1_500_000, true),
        new BossCandidate(1003, "Gamma", 800_000, true),
        new BossCandidate(2001, "Pet", 2_000_000, false),
    };

    var byHp = BossSelectionPolicy.Select(candidates, preferredSourceId: null, maxBosses: 2);
    SequenceEqual(new ulong[] { 1002, 1001 }, byHp.Bosses.Select(b => b.ObjectId).ToArray(), "top bosses by hp");
    Equal(1002UL, byHp.Primary?.ObjectId, "primary by hp");

    var preferred = BossSelectionPolicy.Select(candidates, preferredSourceId: 1003, maxBosses: 2);
    SequenceEqual(new ulong[] { 1003, 1002 }, preferred.Bosses.Select(b => b.ObjectId).ToArray(), "preferred source first");
    Equal(1003UL, preferred.Primary?.ObjectId, "preferred source primary");
}

static void InstantActionPolicy_AllowsRepeatedAaAfterWindow()
{
    var t0 = new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero);
    True(InstantActionPolicy.ShouldPublish(0x8888, 0f, 0, null, t0),
        "first instant action should publish");
    True(InstantActionPolicy.ShouldPublish(
            0x8888,
            0f,
            0x8888,
            t0,
            t0.AddSeconds(InstantActionPolicy.DuplicateSuppressWindowSeconds + 0.05)),
        "same auto attack should publish after repeat window");
}

static void InstantActionPolicy_SuppressesDuplicateAaInsideWindow()
{
    var t0 = new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero);
    False(InstantActionPolicy.ShouldPublish(0, 0f, 0, null, t0),
        "zero action id should not publish");
    False(InstantActionPolicy.ShouldPublish(0x8888, 0.2f, 0, null, t0),
        "casted actions are not instant actions");
    False(InstantActionPolicy.ShouldPublish(0x8888, 0f, 0x8888, t0, t0.AddSeconds(0.2)),
        "same instant action should be suppressed inside repeat window");
}

static void AutoAttackCadenceTracker_PredictsAfterTwoHits()
{
    var tracker = new AutoAttackCadenceTracker();
    var t0 = new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero);

    Null(tracker.Record(new AutoAttackSample(1001, 2001, t0)), "first AA only seeds cadence");
    var prediction = tracker.Record(new AutoAttackSample(1001, 2001, t0.AddSeconds(2.4)));

    NotNull(prediction, "second AA should predict the next hit");
    if (prediction is null) return;
    Equal(2.4, Math.Round(prediction.IntervalSeconds, 2), "predicted interval");
    Equal(4.8, Math.Round((prediction.NextAt - t0).TotalSeconds, 2), "predicted next AA time");
    True(prediction.Confidence > 0, "prediction confidence");
}

static void AutoAttackCadenceTracker_KeepsCadenceAcrossTargetSwaps()
{
    var tracker = new AutoAttackCadenceTracker();
    var t0 = new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero);

    tracker.Record(new AutoAttackSample(1001, 2001, t0));
    var prediction = tracker.Record(new AutoAttackSample(1001, 2002, t0.AddSeconds(2.5)));

    NotNull(prediction, "target swap should not reset the boss AA cadence");
    if (prediction is null) return;
    Equal(2002U, prediction.TargetId ?? 0, "prediction should keep the latest target");
    Equal(2.5, Math.Round(prediction.IntervalSeconds, 2), "target swap interval");
}

static void AutoAttackCadenceTracker_WarnsOnceBeforeNextAa()
{
    var tracker = new AutoAttackCadenceTracker();
    var t0 = new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero);

    tracker.Record(new AutoAttackSample(1001, 2001, t0));
    tracker.Record(new AutoAttackSample(1001, 2001, t0.AddSeconds(2.5)));

    Equal(0, tracker.GetDueWarnings(t0.AddSeconds(3.0), 1.0).Count, "too early warning count");
    var warnings = tracker.GetDueWarnings(t0.AddSeconds(4.1), 1.0);

    Equal(1, warnings.Count, "due warning count");
    Equal(1001U, warnings[0].SourceId, "warning source");
    Equal(t0.AddSeconds(5.0), warnings[0].ExpectedAt, "warning expected time");
    Equal(0, tracker.GetDueWarnings(t0.AddSeconds(4.2), 1.0).Count, "warning should not repeat");
}

static void AttackDisplayPolicy_RespectsToggles()
{
    var settings = new AutoSettings
    {
        ShowAutoTelegraphs = true,
        ShowAllEnemyCasts = false,
        ShowAutoAttacks = false,
    };

    Equal(AttackDisplayDecision.AoeTelegraph,
        AttackDisplayPolicy.Decide(settings, new AttackDisplayRequest(false, true, false, true)),
        "known aoe should be displayed when auto telegraphs are on");

    Equal(AttackDisplayDecision.None,
        AttackDisplayPolicy.Decide(settings, new AttackDisplayRequest(false, false, false, true)),
        "non-aoe cast should be hidden by default");

    settings.ShowAllEnemyCasts = true;
    Equal(AttackDisplayDecision.AttackPulse,
        AttackDisplayPolicy.Decide(settings, new AttackDisplayRequest(false, false, false, true)),
        "non-aoe cast should be displayed when all enemy casts are on");

    Equal(AttackDisplayDecision.None,
        AttackDisplayPolicy.Decide(settings, new AttackDisplayRequest(false, false, true, false)),
        "auto attack should be hidden by default");

    settings.ShowAutoAttacks = true;
    Equal(AttackDisplayDecision.AutoAttackPulse,
        AttackDisplayPolicy.Decide(settings, new AttackDisplayRequest(false, false, true, false)),
        "auto attack should be displayed when enabled");

    Equal(AttackDisplayDecision.None,
        AttackDisplayPolicy.Decide(settings, new AttackDisplayRequest(true, true, false, true)),
        "friendly actions should be ignored");
}

static void AttackPulseLabelPolicy_HidesActionNames()
{
    Equal("AA", AttackPulseLabelPolicy.Format("AA", "Auto Attack"), "aa label");
    Equal("Action", AttackPulseLabelPolicy.Format(" Action ", "Huge Skill"), "action label");
    Equal("Cast", AttackPulseLabelPolicy.Format("Cast", "Raidwide"), "cast label");
    Equal("Attack", AttackPulseLabelPolicy.Format("", "Some Skill"), "fallback label");
}

static void ArenaCenterResolver_UsesActorBoundsWhenDefaultCenterWrong()
{
    var fallback = new Vector3(100, 0, 100);
    var anchors = new[]
    {
        new Vector3(490, 0, -310),
        new Vector3(510, 0, -290),
    };

    Equal(new Vector3(500, 0, -300), ArenaCenterResolver.Resolve(fallback, anchors),
        "far combat cluster should recenter the arena projection");
}

static void ArenaCenterResolver_KeepsTrustedDefaultCenter()
{
    var fallback = new Vector3(100, 0, 100);
    var anchors = new[]
    {
        new Vector3(96, 0, 100),
        new Vector3(104, 0, 102),
    };

    Equal(fallback, ArenaCenterResolver.Resolve(fallback, anchors),
        "nearby actors should keep the known FFXIV arena center");
}

static void ArenaProjection_MapsBossDirectionToMinimap()
{
    var center = new Vector2(100, 100);
    var arenaCenter = new Vector3(0, 0, 0);

    var east = ArenaProjection.ProjectWorldToMap(center, 80, arenaCenter, 20, new Vector3(10, 0, 0));
    Equal(new Vector2(140, 100), east, "east projects right");

    var south = ArenaProjection.ProjectWorldToMap(center, 80, arenaCenter, 20, new Vector3(0, 0, 10));
    Equal(new Vector2(100, 140), south, "south projects down");

    NearlyEqual(MathF.PI / 2f, ArenaProjection.RotationToMapAngleRad(0), "rotation 0 faces south/down");
    NearlyEqual(0f, ArenaProjection.RotationToMapAngleRad(MathF.PI / 2f), "rotation pi/2 faces east/right");
    NearlyEqual(-MathF.PI / 2f, ArenaProjection.RotationToMapAngleRad(MathF.PI), "rotation pi faces north/up");
}

static void ArenaProjection_ClampsOutOfArenaPositionsWithoutChangingDirection()
{
    var center = new Vector2(100, 100);
    var projected = ArenaProjection.ProjectWorldToMap(
        center,
        mapRadius: 80,
        arenaCenter: Vector3.Zero,
        arenaRadius: 20,
        worldPos: new Vector3(60, 0, 0));

    True(projected.X > center.X, "east out-of-range point should stay east");
    NearlyEqual(center.Y, projected.Y, "east out-of-range point should stay on east axis");
    True(projected.X <= center.X + 80, "clamped point should stay inside map radius");
}

static void ArenaProjection_UsesLongRangeForHalfArenaCones()
{
    True(ArenaProjection.ConeRangeScale(180) >= 2.2f, "half arena cone must cover the arena diameter");
    NearlyEqual(1.45f, ArenaProjection.ConeRangeScale(90), "normal cone range");
    False(ArenaProjection.ShouldAnchorGimmickToSource("half_plane"), "half arena should split the arena, not start from boss dot");
    True(ArenaProjection.ShouldAnchorGimmickToSource("cone"), "normal cone should start from the source");
}

static void AutoSettings_EnablesEarlyPredictionByDefault()
{
    var settings = new AutoSettings();

    Equal(AutoSettings.DefaultPredictAdvanceWarningSec, settings.PredictAdvanceWarningSec, "default prediction warning");
    True(settings.PredictAdvanceWarningSec >= 12.0, "default warning should be early enough to dodge");
}

static void MinimapDisplayPriority_KeepsRealAoeAboveNoise()
{
    True(ArenaViewPriority.GetDisplayPriority("cone") > ArenaViewPriority.GetDisplayPriority("attack"),
        "cone should beat generic attack pulses");
    True(ArenaViewPriority.GetDisplayPriority("half_plane") >= ArenaViewPriority.GetDisplayPriority("cone"),
        "half arena should be at least as important as cones");
    True(ArenaViewPriority.GetDisplayPriority("outer_ring") > ArenaViewPriority.GetDisplayPriority("attack"),
        "real aoe should beat generic attack pulses");
    True(ArenaViewPriority.GetDisplayPriority("attack") > ArenaViewPriority.GetDisplayPriority("unknown"),
        "attack still beats unknown placeholders");
}

static string CreateTempDir()
{
    var dir = Path.Combine(Path.GetTempPath(), "FfxivEchoesTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return dir;
}

static T Single<T>(IEnumerable<T> items, Func<T, bool> predicate)
{
    var matches = items.Where(predicate).ToArray();
    if (matches.Length != 1)
    {
        throw new InvalidOperationException($"Expected single match, got {matches.Length}.");
    }

    return matches[0];
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}.");
    }
}

static void NearlyEqual(float expected, float actual, string label, float tolerance = 0.0001f)
{
    if (MathF.Abs(expected - actual) > tolerance)
    {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}.");
    }
}

static void True(bool condition, string label)
{
    if (!condition)
    {
        throw new InvalidOperationException(label);
    }
}

static void False(bool condition, string label) => True(!condition, label);

static void NotNull(object? value, string label)
{
    if (value is null)
    {
        throw new InvalidOperationException(label);
    }
}

static void Null(object? value, string label)
{
    if (value is not null)
    {
        throw new InvalidOperationException(label);
    }
}

static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string label)
{
    if (expected.Count != actual.Count)
    {
        throw new InvalidOperationException($"{label}: expected {expected.Count} items, got {actual.Count}.");
    }

    for (var i = 0; i < expected.Count; i++)
    {
        if (!EqualityComparer<T>.Default.Equals(expected[i], actual[i]))
        {
            throw new InvalidOperationException($"{label}[{i}]: expected {expected[i]}, got {actual[i]}.");
        }
    }
}
