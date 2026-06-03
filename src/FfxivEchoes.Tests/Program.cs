using FfxivEchoes.Profiles;
using FfxivEchoes.Actions;
using FfxivEchoes.Actions.Handlers;
using FfxivEchoes.Capture;
using FfxivEchoes.Commands.Handlers;
using FfxivEchoes.Recording;
using FfxivEchoes.SafeZone;
using FfxivEchoes.SafeZone.Geometry;
using FfxivEchoes.SafeZone.Presets;
using FfxivEchoes.Triggers;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Events;
using FfxivEchoes.Windows;
using FfxivEchoes.Windows.Tabs;
using System.Numerics;

var tests = new List<(string Name, Action Body)>
{
    ("AggregateFiles keeps repeated cast timings", AggregateFiles_KeepsRepeatedCastTimings),
    ("AggregateFiles reads active recording files", AggregateFiles_ReadsActiveRecordingFiles),
    ("AggregateFiles merges repeated fights by occurrence index", AggregateFiles_MergesRepeatedFightsByOccurrence),
    ("AggregateFiles clusters divergent repeated events by time", AggregateFiles_ClustersDivergentRepeatedEventsByTime),
    ("BuildCastPredictions expands occurrences and filters covered or party casts", BuildCastPredictions_ExpandsAndFilters),
    ("BuildCastPredictions exposes earliest time for safer warnings", BuildCastPredictions_ExposesEarliestTimeForWarnings),
    ("BuildCastPredictions excludes instant actions by default", BuildCastPredictions_ExcludesInstantActionsByDefault),
    ("BuildTimelinePredictions defaults to combat HUD safe events only", BuildTimelinePredictions_DefaultsToCombatHudSafeEventsOnly),
    ("BuildTimelinePredictions can opt into object events for drafting", BuildTimelinePredictions_CanOptIntoObjectEventsForDrafting),
    ("BuildTimelinePredictions can opt into enemy instant events", BuildTimelinePredictions_CanOptIntoEnemyInstantEvents),
    ("BuildTimelinePredictions can opt into auto attacks separately", BuildTimelinePredictions_CanOptIntoAutoAttacksSeparately),
    ("BuildTimelinePredictions keeps repeated auto attack occurrences", BuildTimelinePredictions_KeepsRepeatedAutoAttackOccurrences),
    ("BuildTimelinePredictions keeps same label from different sources", BuildTimelinePredictions_KeepsSameLabelDifferentSources),
    ("BuildTimelinePredictions filters player events by party object id", BuildTimelinePredictions_FiltersPlayerEventsByObjectId),
    ("BuildCastPredictions keeps boss casts targeting party members", BuildCastPredictions_KeepsBossCastsTargetingPartyMembers),
    ("BuildTimelinePredictions keeps boss actions targeting party members", BuildTimelinePredictions_KeepsBossActionsTargetingPartyMembers),
    ("BuildCastPredictions keeps source for boss selection", BuildCastPredictions_KeepsSourceForBossSelection),
    ("RecordingScanner learns object action_used after appearance", RecordingScanner_LearnsObjectActionUsedAfterAppearance),
    ("RecordingScanner filters object action learning by object name", RecordingScanner_FiltersObjectActionLearningByName),
    ("RecordingScanner suppresses repeated old-recording warnings", RecordingScanner_SuppressesRepeatedOldRecordingWarnings),
    ("RecordingScanner skips malformed/truncated lines without throwing", RecordingScanner_SkipsMalformedRecordingLineWithoutThrowing),
    ("AggregateFiles warns only for below-minimum plugin version", AggregateFiles_WarnsOnlyForBelowMinimumPluginVersion),
    ("Recording aggregation suppression wrapper dedupes across repeated aggregations", RecordingAggregation_SuppressionWrapper_DedupesAcrossRepeatedAggregations),
    ("RecordingScanner signature detects recording changes", RecordingScanner_Signature_DetectsRecordingChanges),
    ("ActionUsedEvent serializes and aggregates auto attacks separately", ActionUsedEvent_SerializesAndAggregates),
    ("ActionUsedEvent serializes target world", ActionUsedEvent_SerializesTargetWorld),
    ("ActionUsedEvent with attack name is treated as auto attack", ActionUsedEvent_AttackNameAggregatesAsAutoAttack),
    ("ActionEffectCapture marks Lumina player actions as player", ActionEffectCapture_MarksLuminaPlayerActionsAsPlayer),
    ("Auto attacks are separated by source before party filtering", AutoAttacks_AreSeparatedBySourceBeforePartyFiltering),
    ("Object events aggregate by object identity", ObjectEvents_AggregateByIdentity),
    ("ObjectAppearedEvent serializes entity id", ObjectAppearedEvent_SerializesEntityId),
    ("ObjectCapture republishes object identity changes", ObjectCapture_RepublishesObjectIdentityChanges),
    ("AoeResolver guesses safe minimap gimmicks conservatively", AoeResolver_GuessesGimmicksConservatively),
    ("AoeResolver adds caster hitbox for caster-origin shapes", AoeResolver_AddsCasterHitboxForCasterOriginShapes),
    ("AoeResolver rejects oversized unreliable ranges", AoeResolver_RejectsOversizedRanges),
    ("AoeResolver uses Omen for shape inference", AoeResolver_UsesOmenForShapeInference),
    ("AoeResolver BuildAoeInfo propagates XAxisModifier as line half width", AoeResolver_BuildAoeInfo_PropagatesXAxisModifierAsLineHalfWidth),
    ("AutoSafeCallPlanner creates practical callouts", AutoSafeCallPlanner_CreatesCallouts),
    ("AutoSafeCallPlanner creates visual telegraph for large circle AoE", AutoSafeCallPlanner_CreatesVisualForLargeCircleAoe),
    ("AutoSafeCallPlanner treats donut variants as outer ring", AutoSafeCallPlanner_TreatsDonutVariantsAsOuterRing),
    ("AutoSafeCallPlanner recognizes named half arena attacks", AutoSafeCallPlanner_RecognizesNamedHalfArenaAttacks),
    ("AutoSafeCallPlanner suppresses minimap calls for raid-wide overrides", AutoSafeCallPlanner_SuppressesRaidWideMinimapCalls),
    ("AutoSafeCallPlanner uses content scoped raid-wide markers", AutoSafeCallPlanner_UsesContentScopedRaidWideMarkers),
    ("AutoSafeCallPlanner ignores global HP-correlation raid-wide for suppression", AutoSafeCallPlanner_IgnoresGlobalHpCorrelationRaidWideForSuppression),
    ("AutoSafeCallPlanner removes minimap actions for raid-wide source events", AutoSafeCallPlanner_RemovesMinimapActionsForRaidWideSourceEvents),
    ("AutoSafeCallPlanner removes minimap actions for raid-wide attached matches", AutoSafeCallPlanner_RemovesMinimapActionsForRaidWideAttachedMatches),
    ("AutoSafeCallPlanner Id marker does not match same-name different id", AutoSafeCallPlanner_IdMarkerDoesNotMatchSameNameDifferentId),
    ("ActionDispatcher removes stale auto generated arena views", ActionDispatcher_RemovesStaleAutoGeneratedArenaViews),
    ("ActionDispatcher dedup suppresses repeated text within window", ActionDispatcher_DedupSuppressesRepeatedTextWithinWindow),
    ("StrategyPlanResolver converts active party strategy into notes and actions", StrategyPlanResolver_BuildsNotesAndActions),
    ("StrategyPlanResolver suppresses minimap for raid-wide mechanics", StrategyPlanResolver_SuppressesRaidWideMechanicMinimap),
    ("StrategyPlanResolver creates editable mechanic draft from learned timeline prediction", StrategyPlanResolver_CreatesMechanicDraftFromPrediction),
    ("StrategyPlanResolver does not invent spread markers for empty drafts", StrategyPlanResolver_DoesNotInventSpreadMarkersForEmptyDrafts),
    ("StrategyPlanResolver selects nearest registered mechanic for repeated predictions", StrategyPlanResolver_SelectsNearestMechanicForPrediction),
    ("StrategyDraftGenerator creates mechanic drafts from learned timeline", StrategyDraftGenerator_CreatesMechanicDraftsFromLearnedTimeline),
    ("StrategyDraftGenerator skips existing attached mechanics", StrategyDraftGenerator_SkipsExistingAttachedMechanics),
    ("StrategyDraftGenerator keeps same cast from different sources separate", StrategyDraftGenerator_KeepsSameCastDifferentSourcesSeparate),
    ("StrategyDraftGenerator inherits arena shape and dimensions from profile", StrategyDraftGenerator_InheritsArenaShapeFromProfile),
    ("TimelineNoteResolver resolves object attached notes from recording aggregate", TimelineNoteResolver_ResolvesObjectAttachedNotes),
    ("SyncOffsetTracker accepts large recording sync jumps for phase skips", SyncOffsetTracker_AcceptsLargeRecordingSyncJumps),
    ("SyncOffsetTracker chooses nearest sync point within tolerance", SyncOffsetTracker_ChoosesNearestSyncPointWithinTolerance),
    ("PredictedCastReminderService drops stale skipped predictions", PredictedCastReminderService_DropsStaleSkippedPredictions),
    ("NoteReminderService drops stale notes on reschedule after phase or branch change", NoteReminderService_DropsStaleNotesOnReschedule),
    ("PhaseTransitionPolicy builds phase ordinals from sync points and hp triggers", PhaseTransitionPolicy_BuildsPhaseOrdinals),
    ("PhaseTransitionPolicy IsPastPhase suppresses earlier phases only", PhaseTransitionPolicy_IsPastPhase_SuppressesEarlierPhasesOnly),
    ("PhaseTransitionPolicy phase filtering disabled when no phases annotated", PhaseTransitionPolicy_NoPhasesAnnotated_FilterDisabled),
    ("BranchObserver IsWithinBranchWindow uses per-group activation", BranchObserver_IsWithinBranchWindow_UsesGroupActivation),
    ("BossRelativePreset angle=90 points to boss right (not left)", BossRelativePreset_Angle90_PointsToBossRight),
    ("TimelineNoteResolver SelectOccurrenceTime picks occurrence when no hint time", TimelineNoteResolver_SelectOccurrenceTime_UsesOccurrenceIndex),
    ("AddObjectAoe MatchesObjectSource rejects short partial matches", AddObjectAoe_MatchesObjectSource_RejectsShortPartial),
    ("UpcomingTimelinePolicy filters branch-rejected predictions via Triggers path", UpcomingTimelinePolicy_FiltersBranchRejected_TriggersPath),
    ("PredictedCastReminderService builds visible default warning actions", PredictedCastReminderService_BuildsVisibleDefaultWarningActions),
    ("PredictedCastReminderService draws inferred visuals unless minimap is suppressed", PredictedCastReminderService_DrawsInferredVisualsUnlessSuppressed),
    ("Profile keeps all-enabled and custom trigger selection distinct", Profile_CustomSelectionIsExplicit),
    ("BossSelectionPolicy keeps multiple bosses and preferred source", BossSelectionPolicy_KeepsMultipleBosses),
    ("InstantActionPolicy allows repeated AA after repeat window", InstantActionPolicy_AllowsRepeatedAaAfterWindow),
    ("InstantActionPolicy suppresses duplicate AA inside repeat window", InstantActionPolicy_SuppressesDuplicateAaInsideWindow),
    ("AutoAttackCadenceTracker predicts AA after two hits", AutoAttackCadenceTracker_PredictsAfterTwoHits),
    ("AutoAttackCadenceTracker keeps cadence across target swaps", AutoAttackCadenceTracker_KeepsCadenceAcrossTargetSwaps),
    ("AutoAttackCadenceTracker warns once before next AA", AutoAttackCadenceTracker_WarnsOnceBeforeNextAa),
    ("AutoAttackTimingService creates unique timer trigger ids", AutoAttackTimingService_CreatesUniqueTimerTriggerIds),
    ("AutoAttackTimingService warning text includes source", AutoAttackTimingService_WarningTextIncludesSource),
    ("AttackDisplayPolicy respects all-attack and auto-attack toggles", AttackDisplayPolicy_RespectsToggles),
    ("AutoAoeDisplayPolicy gates auto AoE by zone setting", AutoAoeDisplayPolicy_GatesByZoneSetting),
    ("AutoAoeDisplayPolicy allows instant action impact telegraphs", AutoAoeDisplayPolicy_AllowsInstantActionImpactTelegraphs),
    ("TriggerStore keeps meaningful object mechanics enabled", TriggerStore_KeepsMeaningfulObjectMechanicsEnabled),
    ("MechanicTriggerService waits for stable object groups", MechanicTriggerService_WaitsForStableObjectGroups),
    ("MechanicTriggerService dedupe key includes source", MechanicTriggerService_DedupeKeyIncludesSource),
    ("AutoAoeDisplayPolicy allows learned single object AoE only when enabled", AutoAoeDisplayPolicy_AllowsLearnedSingleObjectOnlyWhenEnabled),
    ("AutoAoeDisplayPolicy uses active profile arena calibration", AutoAoeDisplayPolicy_UsesActiveProfileArenaCalibration),
    ("AddObjectAoeService suppresses unknown object group fallback", AddObjectAoeService_SuppressesUnknownObjectGroupFallback),
    ("AddObjectAoeService suppresses mixed object cohort fallback", AddObjectAoeService_SuppressesMixedObjectCohortFallback),
    ("AddObjectAoeService suppresses repeated object AoE for display duration", AddObjectAoeService_SuppressesRepeatedObjectAoeForDisplayDuration),
    ("AddObjectAoeService shares suppress keys between appear and live scan", AddObjectAoeService_SharesSuppressKeysBetweenAppearAndLiveScan),
    ("AddObjectAoeService scans live objects after instant mechanic actions", AddObjectAoeService_ScansLiveObjectsAfterInstantMechanicActions),
    ("AddObjectAoeService does not scan from known player actions", AddObjectAoeService_DoesNotScanFromKnownPlayerActions),
    ("AddObjectAoeService skips oversized live object cohorts", AddObjectAoeService_SkipsOversizedLiveObjectCohorts),
    ("AddObjectAoeService selects recorded object action candidates by source", AddObjectAoeService_SelectsRecordedObjectActionCandidatesBySource),
    ("AddObjectAoeService rejects placeholder object positions", AddObjectAoeService_RejectsPlaceholderObjectPositions),
    ("ObjectAoeRuleResolver matches profile object rules", ObjectAoeRuleResolver_MatchesProfileRules),
    ("ObjectAoeRuleResolver matches Japanese object name variants", ObjectAoeRuleResolver_MatchesJapaneseObjectNameVariants),
    ("ObjectAoeRuleResolver ignores auto learned source actor rules", ObjectAoeRuleResolver_IgnoresAutoLearnedSourceActorRules),
    ("ObjectAoeRuleResolver preserves shape specific fields", ObjectAoeRuleResolver_PreservesShapeFields),
    ("ObjectAoeRuleResolver learned named rules ignore data id drift", ObjectAoeRuleResolver_LearnedNamedRulesIgnoreDataIdDrift),
    ("ObjectAoeRuleResolver MatchesIgnoringEnabled returns true for disabled rules with same name", ObjectAoeRuleResolver_MatchesIgnoringEnabled_TrueForDisabledRule),
    ("AddObjectAoeService does not persist recording action rules for known event sources", AddObjectAoeService_DoesNotPersistRecordingActionRulesForKnownEventSources),
    ("AddObjectAoeService preserves resolved action geometry", AddObjectAoeService_PreservesResolvedActionGeometry),
    ("AddObjectAoeService builds per-object static zones", AddObjectAoeService_BuildsPerObjectStaticZones),
    ("KnownAoeGeometry maps safe calls into minimap and floor geometry", KnownAoeGeometry_MapsSafeCallsToGeometry),
    ("AutoTelegraphService prefers actual AoE visual shape", AutoTelegraphService_PrefersActualAoeVisualShape),
    ("AutoTelegraphService uses target world snapshot", AutoTelegraphService_UsesTargetWorldSnapshot),
    ("AutoTelegraphService skips player action-used telegraphs", AutoTelegraphService_SkipsPlayerActionUsedTelegraphs),
    ("AutoTelegraphService skips action-used AoE without world position", AutoTelegraphService_SkipsActionUsedWithoutWorldPosition),
    ("AttackPulseLabelPolicy hides action names for combat HUD", AttackPulseLabelPolicy_HidesActionNames),
    ("ArenaCenterResolver uses actor bounds when default center is wrong", ArenaCenterResolver_UsesActorBoundsWhenDefaultCenterWrong),
    ("ArenaCenterResolver keeps trusted default center", ArenaCenterResolver_KeepsTrustedDefaultCenter),
    ("ArenaProjection maps FFXIV boss direction to minimap", ArenaProjection_MapsBossDirectionToMinimap),
    ("ArenaProjection clamps out-of-arena positions without changing direction", ArenaProjection_ClampsOutOfArenaPositionsWithoutChangingDirection),
    ("ArenaProjection uses long range for half arena cones", ArenaProjection_UsesLongRangeForHalfArenaCones),
    ("ArenaProjection keeps directional AoE lengths beyond arena radius", ArenaProjection_KeepsDirectionalAoeLengthsBeyondArenaRadius),
    ("ArenaProjection maps rectangular relative coordinates", ArenaProjection_MapsRectRelativeCoordinates),
    ("ArenaProjection rect arena world origin matches relative dot", ArenaProjection_RectArena_WorldOriginMatchesRelativeDot),
    ("AoeGeometryPolicy uses readable line width", AoeGeometryPolicy_UsesReadableLineWidth),
    ("AoeGeometryPolicy points object AoE toward arena center", AoeGeometryPolicy_PointsObjectAoeTowardArenaCenter),
    ("MarkerRelativePreset accepts marker alias A", MarkerRelativePreset_AcceptsMarkerAliasA),
    ("AutoSettings enables early prediction by default", AutoSettings_EnablesEarlyPredictionByDefault),
    ("Arena ruler reaches forty meters", ArenaRuler_ReachesFortyMeters),
    ("Minimap display priority keeps real AoE above noise", MinimapDisplayPriority_KeepsRealAoeAboveNoise),
    ("Minimap display grouping layers simultaneous auto AoEs", MinimapDisplayGrouping_LayersSimultaneousAutoAoes),
    ("Upcoming timeline policy keeps repeated auto attacks", UpcomingTimelinePolicy_KeepsRepeatedAutoAttacks),
    ("Upcoming timeline policy keeps same label from different sources", UpcomingTimelinePolicy_KeepsSameLabelFromDifferentSources),
    ("Upcoming timeline policy dedupes common configured rows against sourced predictions", UpcomingTimelinePolicy_DedupesCommonConfiguredRowsAgainstSourcedPredictions),
    ("Upcoming timeline policy strips source prefix from row labels", UpcomingTimelinePolicy_StripsSourcePrefixFromRowLabels),
    ("Upcoming timeline policy hides common source header", UpcomingTimelinePolicy_HidesCommonSourceHeader),
    ("Upcoming timeline policy drops due and past items", UpcomingTimelinePolicy_DropsDueAndPastItems),
    ("Upcoming timeline policy drops far-future items beyond horizon", UpcomingTimelinePolicy_DropsFarFutureBeyondHorizon),
    ("RecordingSegmentBuilder separates pulls and computes common casts", RecordingSegmentBuilder_SeparatesPullsAndCommon),
    ("TryResolveSegment resolves by opening cast and exclusive cast", UpcomingTimelinePolicy_TryResolveSegment_ResolvesByOpeningAndExclusive),
    ("SelectActiveAgg picks confirmed segment and degrades safely", UpcomingTimelinePolicy_SelectActiveAgg_PicksConfirmedSegment),
    ("ResolveEmptyStateMessage avoids misleading record-first text", UpcomingTimelinePolicy_ResolveEmptyStateMessage_ContextAware),
    ("Minimap hides live dots for user-authored layouts", Minimap_HidesLiveDotsForUserAuthoredLayouts),
    ("Minimap keeps self dot for user-authored layouts", Minimap_KeepsSelfDotForUserAuthoredLayouts),
    ("Mechanic arena defaults policy applies profile arena to one mechanic", MechanicArenaDefaultsPolicy_AppliesProfileArenaToMechanic),
    ("Mechanic arena defaults policy applies profile arena to all mechanics", MechanicArenaDefaultsPolicy_AppliesProfileArenaToAllMechanics),
    ("Arena profile center drag moves profile center only", ArenaCenterDragPolicy_MovesProfileCenterOnly),
    ("Arena center drag moves center without shifting layout", ArenaCenterDragPolicy_MovesCenterOnly),
    ("ActorTrackedAoe shape inference covers Lumina cast types", ActorTrackedAoe_InferShape_CoversLuminaCastTypes),
    ("ActorTrackedAoe applies user zone hitbox and rotation offset", ActorTrackedAoe_AppliesUserZoneHitboxAndRotationOffset),
    ("ActorTrackedAoe half-plane uses default width", ActorTrackedAoe_HalfPlaneUsesDefaultWidth),
    ("CastStartedEvent snapshots target world", CastStartedEvent_SnapshotsTargetWorld),
    ("ActorTrackedAoe snapshots caster-origin AoE source positions", ActorTrackedAoe_StaticSnapshotPolicy_UsesCasterOriginOnly),
    ("ActorTrackedAoe rotation conversion matches ArenaProjection", ActorTrackedAoe_RotationConversion_MatchesArenaProjection),
    ("ActorTrackedAoe TowardsTarget faces correct cardinal direction", ActorTrackedAoe_TowardsTarget_FacesCardinalDirection),
    ("ActorTrackedAoe ParseZoneShape covers all string forms", ActorTrackedAoe_ParseZoneShape_CoversAllStringForms),
    ("AoeAnchorResolver extracts source id from each event type", AoeAnchorResolver_ExtractSourceIdFromEvents),
    ("AoeAnchorResolver uses object event positions as static anchors", AoeAnchorResolver_UsesObjectEventPositions),
    ("AoeAnchorResolver expands object group positions", AoeAnchorResolver_ExpandsObjectGroupPositions),
    ("AoeSequenceStep schedules with positive delay and zero delay", AoeSequenceStep_ScheduleAccepts),
    ("ActorTrackedAoe ComputePhaseAlpha returns expected per-phase alphas", ActorTrackedAoe_ComputePhaseAlpha_ReturnsPerPhaseAlphas),
    ("WorldShapeRenderer RecommendSegments scales with radius", WorldShapeRenderer_RecommendSegments_ScalesWithRadius),
    ("RectShape rotation matches game facing convention", RectShape_Contains_RotationMatchesGameFacing),
    ("LinePerpendicular side normal matches player left/right", LinePerpendicularPreset_SideNormal_MatchesPlayerLeftRight),
    ("TimelineBranch JSON roundtrip preserves condition fields", TimelineBranch_JsonRoundtrip_PreservesFields),
    ("BranchObserver scopes rejection to branch group", BranchObserver_ScopesRejectionToBranchGroup),
    ("StrategyPlanResolver FindMechanicForPrediction filters branch", StrategyPlanResolver_FindMechanicForPrediction_FiltersByBranch),
    ("StrategyPlanResolver FindMechanicForPrediction signals branch-rejected vs undefined", StrategyPlanResolver_FindMechanicForPrediction_SignalsBranchRejected),
    ("UpcomingTimelinePolicy filters branch-rejected recording predictions", UpcomingTimelinePolicy_FiltersBranchRejectedPredictions),
    ("UpcomingTimelinePolicy keeps prediction when at least one branch is active", UpcomingTimelinePolicy_KeepsPredictionWhenAnyBranchActive),
    ("TriggerFile JSON roundtrip preserves raid-wide markers", TriggerFile_JsonRoundtrip_PreservesRaidWideMarkers),
    ("TriggerFile JSON roundtrip preserves object AoE rules", TriggerFile_JsonRoundtrip_PreservesObjectAoeRules),
    ("Trigger schema exposes advanced AoE fields", TriggerSchema_ExposesAdvancedAoeFields),
    ("StrategyPlanResolver BuildTimelineNotes filters by branch active check", StrategyPlanResolver_BuildTimelineNotes_FiltersByBranch),
    ("RecordingBranchAnalyzer detects two patterns from clustered files", RecordingBranchAnalyzer_TwoPatterns_DetectsBranch),
    ("RecordingBranchAnalyzer keeps all primary branch patterns up to eight", RecordingBranchAnalyzer_ThreePatterns_AllPrimary),
    ("RecordingBranchAnalyzer keeps eight branch patterns selectable", RecordingBranchAnalyzer_EightPatterns_AllPrimary),
    ("RecordingBranchAnalyzer detects branch after common first cast", RecordingBranchAnalyzer_CommonFirstCast_DifferentSecondCast),
    ("RecordingBranchAnalyzer all same pattern reports no branch", RecordingBranchAnalyzer_AllSamePattern_NoBranchDetected),
    ("RecordingBranchAnalyzer single file group is excluded as outlier", RecordingBranchAnalyzer_SingleFileGroup_Excluded),
    ("TriggerBranchApplier preserves manually set branch_id", TriggerBranchApplier_DoesNotOverwriteExistingBranchId),
    ("TriggerBranchApplier generateMechanics tags branch-exclusive casts", TriggerBranchApplier_GenerateMechanics_TagsBranchExclusiveCasts),
    ("StrategyDraftGenerator LooksLikePcSkillOrPet rejects PC skills and pets", StrategyDraftGenerator_LooksLikePcSkillOrPet_RejectsKnownNames),
    ("StrategyDraftGenerator pattern catches BRD/NIN/RPR/SCH self-buffs", StrategyDraftGenerator_LooksLikePcSkillOrPet_CoversReportedJobNames),
    ("Aggregation party self-buff filtered, boss self-buff retained", Aggregation_FiltersSelfAppliedStatusEvents),
    ("Aggregation old recording fallback filters self-applied without party meta", Aggregation_OldRecordingFallback_FiltersSelfAppliedWithoutPartyMeta),
    ("StrategyDraftGenerator pattern catches AST PCT names too", StrategyDraftGenerator_LooksLikePcSkillOrPet_CoversAstPct),
    ("PcSkillNameFilter rejects cast_start PC names and pet skills", PcSkillNameFilter_RejectsCastAndPetSkills),
    ("PcSkillNameFilter rejects new reported skills (fairy/grit/shadow body)", PcSkillNameFilter_RejectsNewReportedSkills),
    ("StatusGainedEvent IsPlayer default backward compat", StatusGainedEvent_IsPlayer_DefaultBackwardCompat),
    ("ObjectAppearedEvent IsPlayer default backward compat", ObjectAppearedEvent_IsPlayer_DefaultBackwardCompat),
    ("StatusGainedEvent boss debuff on PC tank must NOT be IsPlayer", StatusGainedEvent_BossDebuffOnPc_IsNotPlayer),
    ("StatusGainedEvent PC self-buff IS IsPlayer", StatusGainedEvent_PcSelfBuff_IsPlayer),
    ("HpChangedEvent IsPlayer flag propagates", HpChangedEvent_IsPlayer_DefaultBackwardCompat),
    ("HpChangedEvent boss raid-wide hit must NOT be IsPlayer", HpChangedEvent_BossRaidWide_IsNotPlayer),
    ("InMemoryActionLookup exposes ActionGeometry via IActionLookup", InMemoryActionLookup_ExposesActionGeometry),
    ("BuildSpawnId distinguishes object name and trigger event (T1 dup fix)", BuildSpawnId_DistinguishesObjectNameAndTriggerEvent),
    ("BuildSpawnId is stable across runs (FNV-1a not String.GetHashCode)", BuildSpawnId_StableAcrossRuns),
    ("IsUsableObjectAoePosition rejects out-of-arena placeholders (T1 §3 補足1)", IsUsableObjectAoePosition_RejectsOutOfArenaPlaceholders),
    ("IsUsableObjectAoePosition keeps valid positions near arena edge", IsUsableObjectAoePosition_KeepsValidEdgePositions),
    ("HasPlaceholderActionUsedTarget detects -0.015 placeholder (T1 §2 症状B)", HasPlaceholderActionUsedTarget_DetectsPlaceholder),
    ("HasPlaceholderActionUsedTarget passes valid targets", HasPlaceholderActionUsedTarget_PassesValidTargets),
    ("PredictedObjectSpawnLearner separates cast_start/cast_complete delays (T1 §2 症状A delay ズレ)", PredictedObjectSpawnLearner_SeparatesCastStartAndComplete),
    ("ReconcileObjectAoeRules updates recording-sourced rules from spawns (T1 §4 優先度1)", ReconcileObjectAoeRules_UpdatesRecordingSourcedRules),
    ("ReconcileObjectAoeRules protects manual and dictionary rules", ReconcileObjectAoeRules_ProtectsManualAndDictionaryRules),
    ("PhaseTransitionPolicy CrossedBelow detects downward threshold crossing", PhaseTransitionPolicy_CrossedBelow),
    ("TtsHandler dedup suppresses repeated text within window", TtsHandlerDedup_SuppressesRepeatWithinWindow),
    ("WavHandler dedup suppresses repeated file within window", WavHandlerDedup_SuppressesRepeatWithinWindow),
    ("TtsHandler IsStale drops over-aged queued speech", TtsHandler_IsStale_DropsOverAged),
    ("TtsHandler serial queue keeps distinct burst calls within configured stale window", TtsHandler_SerialQueue_DistinctBurstCallsSurvive),
    ("DirectionCall output_format template interpolates direction tokens", DirectionCallTemplate_InterpolatesTokens),
    ("TelegraphGap FindSafePoint prefers clear gap and stays if origin safe", TelegraphGap_FindSafePoint_PrefersClearGap),
    ("TelegraphGap FindSafePoint returns null when fully covered", TelegraphGap_FindSafePoint_FullCoverageReturnsNull),
    ("TelegraphGap FindSafePoint moves out of telegraph", TelegraphGap_FindSafePoint_MovesOutOfTelegraph),
    ("FilterPastPhase drops past-phase, keeps current and unmapped casts", FilterPastPhase_DropsPastPhaseKeepsCurrentAndUnmapped),
    ("KnockbackProjection pushes outward (away from source), never toward it", KnockbackProjection_PushesOutwardNotTowardSource),
    ("KnockbackProjection degenerate self==source falls back to arena center push", KnockbackProjection_DegenerateFallsBackToArenaCenter),
    ("KnockbackProjection clamps landing to arena radius", KnockbackProjection_ClampsToArenaRadius),
    ("EventMatcher maps tether events to tether/tether_remove type names", EventMatcher_MapsTetherTypeNames),
    ("TtsHandler EnqueueByPriority orders priority first and protects from overflow", TtsHandler_EnqueueByPriority_OrdersAndProtects),
    ("AutoTelegraph ShouldConsiderSuppress excludes branch-rejected and explicit-trigger mechanics", AutoTelegraph_ShouldConsiderSuppress_ExcludesRejectedAndTriggered),
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

static void PhaseTransitionPolicy_CrossedBelow()
{
    True(PhaseTransitionPolicy.CrossedBelow(60f, 48f, 50f), "60→48 crosses below 50");
    False(PhaseTransitionPolicy.CrossedBelow(48f, 45f, 50f), "48→45 already below 50");
    False(PhaseTransitionPolicy.CrossedBelow(70f, 50f, 50f), "70→50 not strictly below threshold");
    False(PhaseTransitionPolicy.CrossedBelow(45f, 55f, 50f), "45→55 rising not below");
    False(PhaseTransitionPolicy.CrossedBelow(float.NaN, 48f, 50f), "NaN prev (first observation) is false");
}

static void TtsHandlerDedup_SuppressesRepeatWithinWindow()
{
    var recent = new System.Collections.Generic.Dictionary<string, System.DateTimeOffset>();
    var t0 = System.DateTimeOffset.UnixEpoch;
    True(TtsHandler.ShouldSpeakByDedup("北", t0, recent, 0.5), "初回は通す");
    False(TtsHandler.ShouldSpeakByDedup("北", t0.AddSeconds(0.2), recent, 0.5), "窓内の同一テキストは抑制");
    True(TtsHandler.ShouldSpeakByDedup("南", t0.AddSeconds(0.2), recent, 0.5), "別テキストは通す");
    True(TtsHandler.ShouldSpeakByDedup("北", t0.AddSeconds(0.6), recent, 0.5), "窓経過後は通す");
    True(TtsHandler.ShouldSpeakByDedup(null, t0, recent, 0.5), "null は常に通す");
    True(TtsHandler.ShouldSpeakByDedup("", t0, recent, 0.5), "空文字は常に通す");
}

static void WavHandlerDedup_SuppressesRepeatWithinWindow()
{
    var recent = new System.Collections.Generic.Dictionary<string, System.DateTimeOffset>(System.StringComparer.OrdinalIgnoreCase);
    var t0 = System.DateTimeOffset.UnixEpoch;
    True(WavHandler.ShouldPlayByDedup("alert.wav", t0, recent, 0.5), "初回は通す");
    False(WavHandler.ShouldPlayByDedup("alert.wav", t0.AddSeconds(0.3), recent, 0.5), "窓内の同一ファイルは抑制");
    True(WavHandler.ShouldPlayByDedup("other.wav", t0.AddSeconds(0.3), recent, 0.5), "別ファイルは通す");
    True(WavHandler.ShouldPlayByDedup("alert.wav", t0.AddSeconds(0.7), recent, 0.5), "窓経過後は通す");
    True(WavHandler.ShouldPlayByDedup(null, t0, recent, 0.5), "null は常に通す");
}

static void TtsHandler_IsStale_DropsOverAged()
{
    var t0 = System.DateTimeOffset.UnixEpoch;
    False(TtsHandler.IsStale(t0, t0.AddSeconds(1.0), 2.5), "1.0s 経過は陳腐化していない");
    True(TtsHandler.IsStale(t0, t0.AddSeconds(3.0), 2.5), "3.0s 経過は陳腐化（ドロップ）");
}

static void TtsHandler_SerialQueue_DistinctBurstCallsSurvive()
{
    // 別ギミックの4コールが同時(t0)にバースト発火し、直列ワーカーが各約1.0秒で再生する状況を再現。
    // MaxStaleSeconds が短いと後発の別ギミック警告が「手遅れ」と判定されドロップ＝無音化（被弾）。
    // 実録画50戦のシミュレーションで、発話長1.0〜1.3秒のとき 2.5秒では別テキストコールが
    // 27〜82件ドロップし、5.0秒で 0 件になることを確認済み（被検世界「シグマ」V4.0）。
    // この回帰テストは WorkerLoop の FIFO + IsStale ドロップ判定を純粋に再現し、設定値
    // TtsHandler.MaxStaleSeconds で同時4コールが全て発話される（無音化しない）ことを固定する。
    var t0 = System.DateTimeOffset.UnixEpoch;
    var enqueuedAt = new[] { t0, t0, t0, t0 };
    const double playbackSec = 1.0;

    var dropped = 0;
    var workerFreeSec = 0.0; // t0 からの相対秒。ワーカーが次の発話を取り出せる時刻。
    foreach (var eq in enqueuedAt)
    {
        var dequeueAt = t0.AddSeconds(workerFreeSec);
        if (TtsHandler.IsStale(eq, dequeueAt, TtsHandler.MaxStaleSeconds))
        {
            dropped++; // 手遅れ＝ワーカーは再生せず破棄（free 時刻は進めない）
            continue;
        }
        workerFreeSec += playbackSec;
    }

    Equal(0, dropped, "同時バーストの別ギミック4コールは設定 stale 窓内で全て発話される（無音化しない）");
}

static void FilterPastPhase_DropsPastPhaseKeepsCurrentAndUnmapped()
{
    static RecordingTimelinePrediction Pred(string id) =>
        new("cast_start", 10.0, "Tech", id, null, null, 1, 0, 1, 1.0, 0.0);

    var file = new TriggerFile
    {
        StrategyProfiles =
        {
            new StrategyProfile
            {
                Id = "p",
                Enabled = true,
                Mechanics =
                {
                    new MechanicStrategy { Id = "m1", Enabled = true, Phase = "P1", AttachedTo = new MatchCondition { CastId = "0xPAST" } },
                    new MechanicStrategy { Id = "m2", Enabled = true, Phase = "P2", AttachedTo = new MatchCondition { CastId = "0xCUR" } },
                },
            },
        },
    };

    var preds = new System.Collections.Generic.List<RecordingTimelinePrediction>
    {
        Pred("0xPAST"), Pred("0xCUR"), Pred("0xUNMAPPED"),
    };

    // P2 を現在フェーズ、P1 を過去とみなす phaseActiveCheck
    var result = UpcomingTimelinePolicy.FilterPastPhasePredictions(preds, file, phase => phase == "P2");
    var ids = new System.Collections.Generic.HashSet<string>(result.Select(p => p.Id));

    False(ids.Contains("0xPAST"), "過去フェーズ P1 の cast は除外");
    True(ids.Contains("0xCUR"), "現フェーズ P2 の cast は残す");
    True(ids.Contains("0xUNMAPPED"), "phase 未マップの cast は残す（安全側）");
}

static void TelegraphGap_FindSafePoint_PrefersClearGap()
{
    var origin = new System.Numerics.Vector3(0f, 0f, 0f);
    var shapes = new System.Collections.Generic.List<TelegraphShape>
    {
        new CircleShape { Center = new System.Numerics.Vector3(6f, 0f, 0f), Radius = 3f },
        new CircleShape { Center = new System.Numerics.Vector3(-6f, 0f, 0f), Radius = 3f },
    };
    var p = TelegraphGapPreset.FindSafePoint(origin, 12f, shapes, 1.0f);
    True(p is not null, "隙間があれば安全点を返す");
    foreach (var s in shapes)
    {
        False(s.Contains(p!.Value), "安全点は telegraph 外");
    }
    // origin が安全（両円から十分離れている）なら不要な移動をしない＝その場待機。
    True(p!.Value.X == 0f && p.Value.Z == 0f, "origin が安全なら origin を返す");
}

static void TelegraphGap_FindSafePoint_FullCoverageReturnsNull()
{
    var shapes = new System.Collections.Generic.List<TelegraphShape>
    {
        new CircleShape { Center = new System.Numerics.Vector3(0f, 0f, 0f), Radius = 100f },
    };
    var p = TelegraphGapPreset.FindSafePoint(new System.Numerics.Vector3(0f, 0f, 0f), 10f, shapes, 1.0f);
    True(p is null, "全領域が telegraph なら null（無音で安全側に倒す）");
}

static void TelegraphGap_FindSafePoint_MovesOutOfTelegraph()
{
    var shapes = new System.Collections.Generic.List<TelegraphShape>
    {
        new CircleShape { Center = new System.Numerics.Vector3(0f, 0f, 0f), Radius = 4f },
    };
    var p = TelegraphGapPreset.FindSafePoint(new System.Numerics.Vector3(0f, 0f, 0f), 12f, shapes, 1.0f);
    True(p is not null, "外側に安全点があれば返す");
    False(shapes[0].Contains(p!.Value), "返り点は telegraph 外");
}

static void DirectionCallTemplate_InterpolatesTokens()
{
    var info = new DirectionInfo(90f, "N", 10.5f, 0f);
    Equal("安置 10.5時方向", DirectionCallHandler.ApplyTemplate("安置 ${direction_clock}時方向", info), "clock トークン");
    Equal("北へ", DirectionCallHandler.ApplyTemplate("${direction_cardinal_jp}へ", info), "cardinal_jp トークン");
    Equal("Nへ", DirectionCallHandler.ApplyTemplate("${direction_cardinal}へ", info), "cardinal トークン");
    Equal("ボス右", DirectionCallHandler.ApplyTemplate("ボス${direction_relative_jp}", info), "relative_jp トークン");
    Equal("90度", DirectionCallHandler.ApplyTemplate("${direction_deg}度", info), "deg トークン");
    Equal("そのまま ${unknown}", DirectionCallHandler.ApplyTemplate("そのまま ${unknown}", info), "未知トークンは原文維持");
}

static void KnockbackProjection_PushesOutwardNotTowardSource()
{
    var center = new Vector3(100f, 0f, 100f);

    // self が source の真東 → さらに東(+X)へ投射される。逆向き(source へ吸い込む)なら AoE 中心へ誘導＝全滅。
    var selfEast = new Vector3(105f, 0f, 100f);
    var landE = KnockbackProjectionPreset.ProjectLanding(selfEast, center, center, 10f, false, null);
    NotNull(landE, "東押しは着地点を返す");
    NearlyEqual(115f, landE!.Value.X, "東へ +10 投射した X");
    NearlyEqual(100f, landE.Value.Z, "東押しでは Z 不変");
    True(landE.Value.X > selfEast.X, "source から離れる向き（東）に押される＝符号反転していない");

    // self が source の真北(self.Z<source.Z) → -Z(北)へ。FFXIV は -Z=北。
    var selfNorth = new Vector3(100f, 0f, 95f);
    var landN = KnockbackProjectionPreset.ProjectLanding(selfNorth, center, center, 10f, false, null);
    NotNull(landN, "北押しは着地点を返す");
    NearlyEqual(85f, landN!.Value.Z, "北へ -10 投射した Z");
    True(landN.Value.Z < selfNorth.Z, "北（-Z）へ押される＝符号反転していない");
}

static void KnockbackProjection_DegenerateFallsBackToArenaCenter()
{
    var center = new Vector3(100f, 0f, 90f);
    var self = new Vector3(100f, 0f, 100f);

    // source==self の縮退 → アリーナ中心(100,90)からの外向き(+Z)へフォールバック。
    var land = KnockbackProjectionPreset.ProjectLanding(self, self, center, 10f, false, null);
    NotNull(land, "縮退時はアリーナ中心フォールバックで着地点を返す");
    NearlyEqual(110f, land!.Value.Z, "中心(z=90)から外向き(+Z)へ 10");

    // self==source==center の二重縮退 → 方向定義不能 → null（無音＝安全側）。
    var doubleDegenerate = KnockbackProjectionPreset.ProjectLanding(center, center, center, 10f, false, null);
    True(doubleDegenerate is null, "二重縮退は null（無音）");
}

static void KnockbackProjection_ClampsToArenaRadius()
{
    var center = new Vector3(100f, 0f, 100f);
    var self = new Vector3(118f, 0f, 100f); // 中心から東へ 18

    // 投射前 (128,100) は中心から 28 > 半径20 → 東境界 (120,100) に丸める。
    var clamped = KnockbackProjectionPreset.ProjectLanding(self, center, center, 10f, true, 20f);
    NotNull(clamped, "clamp ありでも着地点を返す");
    NearlyEqual(120f, clamped!.Value.X, "半径 20 でアリーナ東境界に clamp");

    // arena_radius 未指定なら clamp せず純投射（境界外の着地点＝落下警告に使える）。
    var raw = KnockbackProjectionPreset.ProjectLanding(self, center, center, 10f, true, null);
    NotNull(raw, "arena_radius 未指定でも着地点を返す");
    NearlyEqual(128f, raw!.Value.X, "arena_radius 未指定なら clamp せず純投射");
}

static void EventMatcher_MapsTetherTypeNames()
{
    var appeared = new TetherAppearedEvent(
        System.DateTimeOffset.UnixEpoch, 1u, "Boss", 2u, "Player", 17u, 30f);
    Equal("tether", FfxivEchoes.Triggers.Matching.EventMatcher.EventTypeName(appeared), "tether 出現の type 名");

    var removed = new TetherRemovedEvent(System.DateTimeOffset.UnixEpoch, 1u, 2u, 17u);
    Equal("tether_remove", FfxivEchoes.Triggers.Matching.EventMatcher.EventTypeName(removed), "tether 消失の type 名");
}

static void TtsHandler_EnqueueByPriority_OrdersAndProtects()
{
    var t0 = System.DateTimeOffset.UnixEpoch;
    static TtsHandler.SpeechRequest Req(string text, int prio, System.DateTimeOffset t) =>
        new TtsHandler.SpeechRequest(text, null, 0, 1.0, 1f, false, t, prio);
    static string Join(System.Collections.Generic.LinkedList<TtsHandler.SpeechRequest> q)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in q)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(r.Text);
        }
        return sb.ToString();
    }

    // 優先コールは通常コール群より前・各 tier 内 FIFO で挿す。
    var q = new System.Collections.Generic.LinkedList<TtsHandler.SpeechRequest>();
    TtsHandler.EnqueueByPriority(q, Req("a", 0, t0), 6);
    TtsHandler.EnqueueByPriority(q, Req("b", 0, t0), 6);
    TtsHandler.EnqueueByPriority(q, Req("p1", 1, t0), 6);
    TtsHandler.EnqueueByPriority(q, Req("p2", 1, t0), 6);
    Equal("p1,p2,a,b", Join(q), "優先は前・各 tier 内 FIFO");

    // 溢れ時は最古の通常コールを落とし、優先コールは守る（maxQueue=3）。
    var q2 = new System.Collections.Generic.LinkedList<TtsHandler.SpeechRequest>();
    TtsHandler.EnqueueByPriority(q2, Req("p", 1, t0), 3);
    TtsHandler.EnqueueByPriority(q2, Req("n1", 0, t0), 3);
    TtsHandler.EnqueueByPriority(q2, Req("n2", 0, t0), 3);
    TtsHandler.EnqueueByPriority(q2, Req("n3", 0, t0), 3); // count 4 > 3 → 最古通常 n1 を落とす
    Equal("p,n2,n3", Join(q2), "溢れ時は最古の通常を落とし優先を守る");
}

static void AutoTelegraph_ShouldConsiderSuppress_ExcludesRejectedAndTriggered()
{
    // legacy(AttachedTo)で発火しうる mechanic（明示 Trigger 無し・分岐許可）だけ抑制を検討してよい。
    True(AutoTelegraphService.ShouldConsiderSuppress(mechTriggerCount: 0, branchAllowed: true),
        "明示Trigger無し＋分岐許可なら抑制検討可");
    // 分岐棄却された mechanic は MechanicTriggerService が撃たないので抑制すると両方無音化＝事故。
    False(AutoTelegraphService.ShouldConsiderSuppress(mechTriggerCount: 0, branchAllowed: false),
        "分岐棄却 mechanic は抑制対象外（無音化回帰の防止）");
    // 明示 Triggers を持つ mechanic は legacy では撃たれないので抑制対象外。
    False(AutoTelegraphService.ShouldConsiderSuppress(mechTriggerCount: 2, branchAllowed: true),
        "明示Triggerあり mechanic は抑制対象外（無音化回帰の防止）");
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

static void AggregateFiles_ReadsActiveRecordingFiles()
{
    var dir = CreateTempDir();
    var path = Path.Combine(dir, "active.jsonl");
    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
    using var writer = new StreamWriter(stream) { AutoFlush = true };

    writer.WriteLine("""{"meta":true,"zone":"Zone A","start_time":"2026-05-12T00:00:00.000Z","plugin_version":"0.1.0","party":[]}""");
    writer.WriteLine("""{"time":3.0,"type":"action_used","source":"Boss","source_id":3001,"action_id":"0x6C73","action_name":"攻撃","auto_attack":true,"target_id":2001}""");

    var agg = RecordingAggregationReader.AggregateFiles(new[] { path });
    var aa = Single(agg.Events, e => e.Key.Type == "auto_attack");

    Equal(1, agg.BattleCount, "active recording battle count");
    Equal("Boss", aa.Key.Source, "active recording AA source");
    SequenceEqual(new[] { 3.0 }, aa.ObservedTimesSeconds, "active recording AA time");
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

static void BuildCastPredictions_ExcludesInstantActionsByDefault()
{
    var agg = new AggregatedEvents(new[]
    {
        new AggregatedEvent(new EventKey("cast_start", "0x1000", "Cast Telegraph", "Boss", null), 1, 10)
        {
            Occurrences = new[] { new AggregatedOccurrence(0, 10.0, 1, new[] { 10.0 }) },
        },
        new AggregatedEvent(new EventKey("action_used", "0x2000", "Instant Damage", "Boss", null), 1, 12)
        {
            Occurrences = new[] { new AggregatedOccurrence(0, 12.0, 1, new[] { 12.0 }) },
        },
    }, 1, 2, 1);

    var predictions = RecordingPredictionPlanner.BuildCastPredictions(agg);

    Equal(1, predictions.Count, "prediction count");
    Equal("0x1000", predictions[0].CastId, "cast prediction id");
}

static void BuildTimelinePredictions_DefaultsToCombatHudSafeEventsOnly()
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

    SequenceEqual(new[] { "cast_start" },
        predictions.Select(p => p.EventType).ToArray(),
        "timeline event types");
    SequenceEqual(new[] { 5.0 },
        predictions.Select(p => p.RelativeSeconds).ToArray(),
        "timeline times");
}

static void BuildTimelinePredictions_CanOptIntoObjectEventsForDrafting()
{
    var agg = new AggregatedEvents(new[]
    {
        new AggregatedEvent(new EventKey("cast_start", "0x1000", "Raidwide", "Boss", null), 1, 5)
        {
            Occurrences = new[] { new AggregatedOccurrence(0, 5.0, 1, new[] { 5.0 }) },
        },
        new AggregatedEvent(new EventKey("object_appear", "4000", "Tower", "Tower", null), 1, 15)
        {
            Occurrences = new[] { new AggregatedOccurrence(0, 15.0, 1, new[] { 15.0 }) },
        },
    }, 1, 2, 1);

    var predictions = RecordingPredictionPlanner.BuildTimelinePredictions(
        agg,
        includeObjects: true);

    SequenceEqual(new[] { "cast_start", "object_appear" },
        predictions.Select(p => p.EventType).ToArray(),
        "timeline event types with object opt-in");
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

static void BuildTimelinePredictions_CanOptIntoAutoAttacksSeparately()
{
    var agg = new AggregatedEvents(new[]
    {
        new AggregatedEvent(new EventKey("action_used", "0x2000", "Shared Buster", "Boss", null), 1, 8)
        {
            Occurrences = new[] { new AggregatedOccurrence(0, 8.0, 1, new[] { 8.0 }) },
        },
        new AggregatedEvent(new EventKey("auto_attack", "0x0007", "Auto Attack", "Boss", null), 1, 10)
        {
            Occurrences = new[] { new AggregatedOccurrence(0, 10.0, 1, new[] { 10.0 }) },
        },
    }, 1, 2, 1);

    var predictions = RecordingPredictionPlanner.BuildTimelinePredictions(
        agg,
        includeActions: false,
        includeAutoAttacks: true);

    Equal(1, predictions.Count, "auto attack prediction count");
    Equal("auto_attack", predictions[0].EventType, "auto attack event type");
    Equal("Boss", predictions[0].Source, "auto attack source");
}

static void BuildTimelinePredictions_KeepsRepeatedAutoAttackOccurrences()
{
    var agg = new AggregatedEvents(new[]
    {
        new AggregatedEvent(new EventKey("auto_attack", "0x0007", "Auto Attack", "Boss", null), 3, 10)
        {
            Occurrences = new[]
            {
                new AggregatedOccurrence(0, 10.0, 1, new[] { 10.0 }),
                new AggregatedOccurrence(1, 12.4, 1, new[] { 12.4 }),
                new AggregatedOccurrence(2, 14.8, 1, new[] { 14.8 }),
            },
        },
    }, 1, 3, 1);

    var predictions = RecordingPredictionPlanner.BuildTimelinePredictions(
        agg,
        includeActions: false,
        includeAutoAttacks: true);

    Equal(3, predictions.Count, "repeated auto attack predictions");
    SequenceEqual(new[] { 10.0, 12.4, 14.8 },
        predictions.Select(p => p.RelativeSeconds).ToArray(),
        "auto attack occurrence times");
}

static void BuildTimelinePredictions_KeepsSameLabelDifferentSources()
{
    var agg = new AggregatedEvents(new[]
    {
        new AggregatedEvent(new EventKey("cast_start", "0x7777", "Twin Cleave", "Boss A", null), 1, 20.0)
        {
            Occurrences = new[] { new AggregatedOccurrence(0, 20.0, 1, new[] { 20.0 }) },
        },
        new AggregatedEvent(new EventKey("cast_start", "0x7777", "Twin Cleave", "Boss B", null), 1, 20.2)
        {
            Occurrences = new[] { new AggregatedOccurrence(0, 20.2, 1, new[] { 20.2 }) },
        },
    }, 1, 2, 1);

    var predictions = RecordingPredictionPlanner.BuildTimelinePredictions(agg);

    Equal(2, predictions.Count, "same label/cast from two sources must stay visible");
    SequenceEqual(new[] { "Boss A", "Boss B" },
        predictions.Select(p => p.Source ?? "").ToArray(),
        "source order");
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

static void RecordingScanner_LearnsObjectActionUsedAfterAppearance()
{
    var seen = new Dictionary<uint, double>();
    var votes = new Dictionary<uint, int>();

    True(RecordingScanner.TryVoteNpcActionFromRecordingLine(
            "{\"time\":10.0,\"type\":\"object_appear\",\"object_name\":\"秘紋\",\"object_id\":200745,\"entity_id\":9001,\"data_id\":14388,\"position\":{\"x\":1,\"y\":0,\"z\":2}}",
            targetDataId: 14388,
            maxDelaySec: 8.0,
            npcAppeared: seen,
            actionVotes: votes),
        "object appearance should be tracked");

    True(RecordingScanner.TryVoteNpcActionFromRecordingLine(
            "{\"time\":11.2,\"type\":\"action_used\",\"source\":\"秘紋\",\"source_id\":9001,\"action_id\":\"0x67ED\",\"action_name\":\"アルゲドン\",\"auto_attack\":false,\"target\":null}",
            targetDataId: 14388,
            maxDelaySec: 8.0,
            npcAppeared: seen,
            actionVotes: votes),
        "object action_used should vote as learned geometry source");

    Equal(1, votes.Count, "vote count");
    Equal(1, votes[0x67ED], "learned action vote");
    False(seen.ContainsKey(9001), "entity id should be consumed after first valid action");
}

static void RecordingScanner_FiltersObjectActionLearningByName()
{
    var seen = new Dictionary<uint, double>();
    var votes = new Dictionary<uint, int>();

    False(RecordingScanner.TryVoteNpcActionFromRecordingLine(
            "{\"time\":10.0,\"type\":\"object_appear\",\"object_name\":\"ベヒーモス・エイドロン\",\"object_id\":1,\"entity_id\":9001,\"data_id\":14388}",
            targetDataId: 14388,
            targetObjectName: "ケラノウス・エイドロン",
            maxDelaySec: 8.0,
            npcAppeared: seen,
            actionVotes: votes),
        "different object name should not seed learning for this rule");

    True(RecordingScanner.TryVoteNpcActionFromRecordingLine(
            "{\"time\":10.0,\"type\":\"object_appear\",\"object_name\":\"ケラノウス・エイドロン\",\"object_id\":2,\"entity_id\":9002,\"data_id\":14388}",
            targetDataId: 14388,
            targetObjectName: "ケラノウス・エイドロン",
            maxDelaySec: 8.0,
            npcAppeared: seen,
            actionVotes: votes),
        "matching object name should seed learning");

    True(RecordingScanner.TryVoteNpcActionFromRecordingLine(
            "{\"time\":11.0,\"type\":\"action_used\",\"source\":\"ケラノウス・エイドロン\",\"source_id\":9002,\"action_id\":\"0x67ED\",\"action_name\":\"アルゲドン\",\"auto_attack\":false}",
            targetDataId: 14388,
            targetObjectName: "ケラノウス・エイドロン",
            maxDelaySec: 8.0,
            npcAppeared: seen,
            actionVotes: votes),
        "matching object action should vote");

    Equal(1, votes.Count, "name-filtered object action votes");
    Equal(1, votes[0x67ED], "name-filtered action id");
}

static void RecordingScanner_SkipsMalformedRecordingLineWithoutThrowing()
{
    var seen = new Dictionary<uint, double>();
    var votes = new Dictionary<uint, int>();

    // ライブ録画は AutoFlush で書き込み中、末尾行が途中で切れることがある。破損/途中切れ行は
    // 例外を投げず静かに無視されなければならない。さもないと NPC 初回詠唱学習が全録画スキャンの
    // たびに JsonReaderException を投げ、フレームスレッドを直撃して dalamud.log を氾濫させる。
    False(RecordingScanner.TryVoteNpcActionFromRecordingLine(
            "{\"time\":10.0,\"type\":\"object_appear\",\"data_id\":14",
            targetDataId: 14388,
            maxDelaySec: 8.0,
            npcAppeared: seen,
            actionVotes: votes),
        "truncated json line should be skipped, not throw");

    False(RecordingScanner.TryVoteNpcActionFromRecordingLine(
            "not json at all",
            targetDataId: 14388,
            maxDelaySec: 8.0,
            npcAppeared: seen,
            actionVotes: votes),
        "non-json line should be skipped, not throw");

    False(RecordingScanner.TryVoteNpcActionFromRecordingLine(
            "{\"time\":10.0,\"type\":\"object_appear\",\"data_id\":14388,\"position\":{\"x\":1,",
            targetDataId: 14388,
            maxDelaySec: 8.0,
            npcAppeared: seen,
            actionVotes: votes),
        "truncated nested json line should be skipped, not throw");

    Equal(0, votes.Count, "no votes from malformed lines");
    Equal(0, seen.Count, "no appearances tracked from malformed lines");
}

static void RecordingScanner_SuppressesRepeatedOldRecordingWarnings()
{
    var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var gate = new object();
    var oldEx = new System.IO.InvalidDataException("古い録画フォーマット (plugin_version=0.0.1.0 < 0.1.0)");

    False(RecordingScanner.ShouldSuppressOldRecordingWarning(oldEx, "zoneA/a.jsonl", warned, gate),
        "first old-recording warning for a path should NOT be suppressed");
    True(RecordingScanner.ShouldSuppressOldRecordingWarning(oldEx, "zoneA/a.jsonl", warned, gate),
        "second old-recording warning for the same path should be suppressed");
    False(RecordingScanner.ShouldSuppressOldRecordingWarning(oldEx, "zoneA/b.jsonl", warned, gate),
        "a different path should still warn once");

    var ioEx = new System.IO.IOException("read failure");
    False(RecordingScanner.ShouldSuppressOldRecordingWarning(ioEx, "zoneA/a.jsonl", warned, gate),
        "non-old-recording exception must never be suppressed");
    False(RecordingScanner.ShouldSuppressOldRecordingWarning(ioEx, "zoneA/a.jsonl", warned, gate),
        "IOException stays unsuppressed even when repeated");
}

static void RecordingScanner_Signature_DetectsRecordingChanges()
{
    var t = DateTimeOffset.Parse("2026-05-29T00:00:00Z").UtcDateTime;
    var baseRec = new List<RecordingFileInfo> { new("z/1.jsonl", 100, t), new("z/2.jsonl", 200, t) };
    var reordered = new List<RecordingFileInfo> { new("z/2.jsonl", 200, t), new("z/1.jsonl", 100, t) };
    var fewer = new List<RecordingFileInfo> { new("z/1.jsonl", 100, t) };
    var resized = new List<RecordingFileInfo> { new("z/1.jsonl", 100, t), new("z/2.jsonl", 999, t) };

    Equal(RecordingScanner.ComputeRecordingSignature(baseRec),
        RecordingScanner.ComputeRecordingSignature(reordered),
        "順序が違っても同じ録画集合なら署名は同じ");
    True(RecordingScanner.ComputeRecordingSignature(baseRec) != RecordingScanner.ComputeRecordingSignature(fewer),
        "件数が変われば署名が変わる(キャッシュ無効化)");
    True(RecordingScanner.ComputeRecordingSignature(baseRec) != RecordingScanner.ComputeRecordingSignature(resized),
        "ファイルサイズが変われば署名が変わる(録画追記でキャッシュ無効化)");
}

static void AggregateFiles_WarnsOnlyForBelowMinimumPluginVersion()
{
    var dir = CreateTempDir();
    // 実機が録画に書き込む版は AssemblyVersion.ToString() = 4桁 "0.1.0.0"
    // （csproj <Version>0.1.0 が .NET SDK で 4成分に正規化される）。下限ちょうどの
    // 3桁 "0.1.0" ではなく、この実値で警告されないことを検証する（B1 修正の本来経路）。
    var current = Path.Combine(dir, "current.jsonl");
    File.WriteAllText(current,
        "{\"meta\":true,\"zone\":\"Z\",\"start_time\":\"2026-05-29T00:00:00.000Z\",\"plugin_version\":\"0.1.0.0\",\"party\":[]}\n");
    // 下限未満（旧録画）。
    var old = Path.Combine(dir, "old.jsonl");
    File.WriteAllText(old,
        "{\"meta\":true,\"zone\":\"Z\",\"start_time\":\"2026-05-28T00:00:00.000Z\",\"plugin_version\":\"0.0.1.0\",\"party\":[]}\n");
    // 下限の直下（0.0.9.9 < 0.1.0）も警告対象。
    var justBelow = Path.Combine(dir, "justbelow.jsonl");
    File.WriteAllText(justBelow,
        "{\"meta\":true,\"zone\":\"Z\",\"start_time\":\"2026-05-28T00:00:00.000Z\",\"plugin_version\":\"0.0.9.9\",\"party\":[]}\n");
    // plugin_version 欠落（最古フォーマット）は警告しない設計（TryGetProperty=false でゲートをスキップ）。
    var noVersion = Path.Combine(dir, "noversion.jsonl");
    File.WriteAllText(noVersion,
        "{\"meta\":true,\"zone\":\"Z\",\"start_time\":\"2026-05-01T00:00:00.000Z\",\"party\":[]}\n");

    var warnings = new List<(Exception Ex, string Path)>();
    RecordingAggregationReader.AggregateFiles(
        new[] { current, old, justBelow, noVersion },
        (ex, p) => warnings.Add((ex, p)));

    Equal(2, warnings.Count, "only below-minimum recordings (0.0.1.0, 0.0.9.9) should warn");
    True(warnings.TrueForAll(w => w.Ex is System.IO.InvalidDataException), "warnings must be InvalidDataException");
    True(warnings.Exists(w => w.Path == old), "old (0.0.1.0) must warn");
    True(warnings.Exists(w => w.Path == justBelow), "justBelow (0.0.9.9) must warn");
    False(warnings.Exists(w => w.Path == current), "current (real on-disk value 0.1.0.0) must NOT warn");
    False(warnings.Exists(w => w.Path == noVersion), "missing plugin_version (oldest format) must NOT warn");
}

static void RecordingAggregation_SuppressionWrapper_DedupesAcrossRepeatedAggregations()
{
    var dir = CreateTempDir();
    var old = Path.Combine(dir, "old.jsonl");
    File.WriteAllText(old,
        "{\"meta\":true,\"zone\":\"Z\",\"start_time\":\"2026-05-28T00:00:00.000Z\",\"plugin_version\":\"0.0.1.0\",\"party\":[]}\n");

    // RecordingScanner.OnAggregateWarning と同じ抑制ラッパ（共有 HashSet + gate）。
    var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var gate = new object();
    var warnings = new List<string>();
    Action<Exception, string> onWarning = (ex, p) =>
    {
        if (RecordingScanner.ShouldSuppressOldRecordingWarning(ex, p, warned, gate)) return;
        warnings.Add(p);
    };

    // Aggregate が戦闘中 / 設定ウィンドウ描画で高頻度に呼ばれる状況を再現：同一録画を3回集計。
    for (var i = 0; i < 3; i++)
    {
        RecordingAggregationReader.AggregateFiles(new[] { old }, onWarning);
    }

    Equal(1, warnings.Count, "repeated aggregations of the same old recording warn only once via the suppression wrapper");
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
    var observed = Single(agg.Events, e => e.Key.Type == "auto_attack");

    Equal("0xABCD", observed.Key.Id, "action id");
    Equal("Auto Attack", observed.Key.Name, "action name");
    Equal("Boss", observed.Key.Source, "action source");
    SequenceEqual(new[] { 7.25 }, observed.ObservedTimesSeconds, "action time");
}

static void ActionUsedEvent_SerializesTargetWorld()
{
    var start = DateTimeOffset.Parse("2026-05-06T00:00:00.000Z");
    var action = new ActionUsedEvent(
        start.AddSeconds(1.0),
        SourceId: 1001,
        SourceName: "Object",
        ActionId: 0x67ED,
        ActionName: "Object AoE",
        TargetId: 2001,
        IsAutoAttack: false,
        TargetWorld: new Vector3(101.25f, 0f, 96.75f));

    var json = EventSerializer.Serialize(action, start);

    True(json.Contains("\"target_x\":101.25", StringComparison.Ordinal), $"target_x serialized: {json}");
    True(json.Contains("\"target_y\":0", StringComparison.Ordinal), $"target_y serialized: {json}");
    True(json.Contains("\"target_z\":96.75", StringComparison.Ordinal), $"target_z serialized: {json}");
}

static void ActionUsedEvent_AttackNameAggregatesAsAutoAttack()
{
    var dir = CreateTempDir();
    var path = Path.Combine(dir, "battle.jsonl");
    File.WriteAllLines(path, new[]
    {
        """{"meta":true,"zone":"Zone A","start_time":"2026-05-09T00:00:00.000Z","plugin_version":"0.1.0","party":[{"name":"Self","object_id":2001}]}""",
        """{"time":3.0,"type":"action_used","source":"Boss","source_id":3001,"action_id":"0x6C73","action_name":"攻撃","auto_attack":false,"target_id":2001}""",
        """{"time":3.2,"type":"action_used","source":"Self","source_id":2001,"action_id":"0x7","action_name":"攻撃","auto_attack":false,"target_id":3001}""",
    });

    var agg = RecordingAggregationReader.AggregateFiles(new[] { path });
    var autoAttacks = agg.Events.Where(e => e.Key.Type == "auto_attack").ToArray();

    Equal(2, autoAttacks.Length, "attack-name auto attack count");
    True(autoAttacks.Any(e => e.Key.Source == "Boss" && !e.IsPartySource), "boss attack-name AA should remain enemy");
    True(autoAttacks.Any(e => e.Key.Source == "Self" && e.IsPartySource), "player attack-name AA should remain party");
}

static void ActionEffectCapture_MarksLuminaPlayerActionsAsPlayer()
{
    True(ActionEffectCapture.ShouldMarkActionAsPlayer(
            sourceIsPlayerObject: false,
            sourceIsPlayerOwnedPet: false,
            actionIsPlayerAction: true),
        "Lumina player action should set IsPlayer even when source ObjectTable lookup misses");

    True(ActionEffectCapture.ShouldMarkActionAsPlayer(
            sourceIsPlayerObject: true,
            sourceIsPlayerOwnedPet: false,
            actionIsPlayerAction: false),
        "PC source should still set IsPlayer");

    True(ActionEffectCapture.ShouldMarkActionAsPlayer(
            sourceIsPlayerObject: false,
            sourceIsPlayerOwnedPet: true,
            actionIsPlayerAction: false),
        "PC-owned pet source should set IsPlayer");

    False(ActionEffectCapture.ShouldMarkActionAsPlayer(
            sourceIsPlayerObject: false,
            sourceIsPlayerOwnedPet: false,
            actionIsPlayerAction: false),
        "enemy non-player action should not be marked IsPlayer");
}

static void AutoAttacks_AreSeparatedBySourceBeforePartyFiltering()
{
    var dir = CreateTempDir();
    var path = Path.Combine(dir, "battle.jsonl");
    File.WriteAllLines(path, new[]
    {
        """{"meta":true,"zone":"Zone A","start_time":"2026-05-06T00:00:00.000Z","plugin_version":"0.1.0","party":[{"name":"Self","object_id":2001}]}""",
        """{"time":5.0,"type":"action_used","source":"Self","source_id":2001,"action_id":"0x0007","action_name":"Auto Attack","auto_attack":true,"target_id":3001}""",
        """{"time":6.0,"type":"action_used","source":"Boss","source_id":3001,"action_id":"0x0007","action_name":"Auto Attack","auto_attack":true,"target_id":2001}""",
    });

    var agg = RecordingAggregationReader.AggregateFiles(new[] { path });
    var aaEvents = agg.Events.Where(e => e.Key.Type == "auto_attack").ToArray();
    Equal(2, aaEvents.Length, "auto attack aggregate count");
    True(aaEvents.Any(e => e.Key.Source == "Self" && e.IsPartySource), "player AA should be party source");
    True(aaEvents.Any(e => e.Key.Source == "Boss" && !e.IsPartySource), "boss AA should not be party source");

    var predictions = RecordingPredictionPlanner.BuildTimelinePredictions(
        agg,
        partyMembers: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Self" },
        includeAutoAttacks: true);

    Equal(1, predictions.Count, "only boss AA should remain after party filtering");
    Equal("Boss", predictions[0].Source, "remaining AA source");
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

static void ObjectAppearedEvent_SerializesEntityId()
{
    var start = DateTimeOffset.Parse("2026-05-06T00:00:00.000Z");
    var ev = new ObjectAppearedEvent(
        Timestamp: start.AddSeconds(3),
        ObjectId: 200745,
        ObjectName: "秘紋",
        DataId: 14388,
        Position: new Vector3(1, 0, 2),
        EntityId: 9001);
    var json = EventSerializer.Serialize(ev, start);

    True(json.Contains("\"object_id\":200745", StringComparison.Ordinal), $"object id serialized: {json}");
    True(json.Contains("\"entity_id\":9001", StringComparison.Ordinal), $"entity id serialized: {json}");
}

static void ObjectCapture_RepublishesObjectIdentityChanges()
{
    True(
        ObjectCapture.ShouldRepublishIdentityChange(
            previousName: "ゾディアークの幻影",
            previousDataId: 9020,
            currentName: "ケツァクワァトル",
            currentDataId: 9020),
        "existing object that becomes a named mechanic object should be published again");

    True(
        ObjectCapture.ShouldRepublishIdentityChange(
            previousName: "",
            previousDataId: 0,
            currentName: "ケツァクワァトル",
            currentDataId: 9020),
        "previously unnamed object gaining a mechanic identity should be published");

    False(
        ObjectCapture.ShouldRepublishIdentityChange(
            previousName: "ケツァクワァトル",
            previousDataId: 9020,
            currentName: "ケツァクワァトル",
            currentDataId: 9020),
        "unchanged object identity should not be republished every frame");

    False(
        ObjectCapture.ShouldRepublishIdentityChange(
            previousName: "ケツァクワァトル",
            previousDataId: 9020,
            currentName: "",
            currentDataId: 9020),
        "empty current names are not useful object AoE anchors");
}

static void AoeResolver_GuessesGimmicksConservatively()
{
    Equal("inner_circle", AoeResolver.GuessGimmick(2, 8), "target circle is center danger");
    Equal("inner_circle", AoeResolver.GuessGimmick(5, 25), "caster circle is center danger even when large");
    Equal("outer_ring", AoeResolver.GuessGimmick(6, 20), "donut is inner safe");
    Equal("outer_ring", AoeResolver.GuessGimmick(10, 20), "alternate donut is inner safe");
    Equal("cone", AoeResolver.GuessGimmick(3, 30), "cone cast is cone gimmick");
    Equal("cone", AoeResolver.GuessGimmick(4, 30), "line cast is represented as cone gimmick");
    Equal("cone", AoeResolver.GuessGimmick(12, 30), "target line cast is represented as cone gimmick");
    Equal("cone", AoeResolver.GuessGimmick(13, 30), "target cone cast is represented as cone gimmick");
}

static void AoeResolver_AddsCasterHitboxForCasterOriginShapes()
{
    var casterLine = new AoeResolver.AoeInfo(20f, 4, true, IncludeCasterHitbox: true);
    var targetCircle = new AoeResolver.AoeInfo(8f, 2, false, IncludeCasterHitbox: false);

    Equal(25f, AoeResolver.EffectiveRadius(casterLine, 5f), "caster-origin line should include hitbox");
    Equal(8f, AoeResolver.EffectiveRadius(targetCircle, 5f), "target circle should not include caster hitbox");
}

static void AoeResolver_RejectsOversizedRanges()
{
    True(AoeResolver.IsReliableEffectRange(50f), "50m is the largest reliable auto range");
    False(AoeResolver.IsReliableEffectRange(50.01f), "range above 50m should be skipped");
    False(AoeResolver.IsReliableEffectRange(80f), "80m should not produce guessed auto AoE");
    False(AoeResolver.IsReliableEffectRange(0f), "0m is not an AoE");
}

static void AoeResolver_BuildAoeInfo_PropagatesXAxisModifierAsLineHalfWidth()
{
    var rect = new ActionGeometry(
        Id: 1, Name: "Line", CastType: 4, EffectRangeM: 20f,
        XAxisModifierM: 4f, OmenId: 0, CastTimeMs: 0);
    var info = AoeResolver.BuildAoeInfo(rect);
    True(info is not null, "rect with positive range should resolve to AoeInfo");
    Equal(4f, info!.HalfWidthM, "XAxisModifier should propagate to AoeInfo.HalfWidthM");

    // 直線半幅の解決：XAxisModifier 由来の値があればそれを使い、無ければ既定 5m。
    Equal(4f, ActorTrackedAoeService.ResolveHalfWidthForShape(
            ActorTrackedAoeService.TrackedAoeShape.Rect,
            info.HalfWidthM > 0 ? info.HalfWidthM : (double?)null),
        "line half width should use XAxisModifier when present");
    Equal(AoeGeometryPolicy.DefaultLineHalfWidthM, ActorTrackedAoeService.ResolveHalfWidthForShape(
            ActorTrackedAoeService.TrackedAoeShape.Rect, null),
        "line half width falls back to default 5m when XAxisModifier absent");
}

static void AoeResolver_UsesOmenForShapeInference()
{
    Equal(ActorTrackedAoeService.TrackedAoeShape.Donut,
        ActorTrackedAoeService.InferShape(2, 53),
        "Omen donut should override cast type 2 circle");
    True(AoeResolver.IsDonutShape(2, 53), "Omen 53 is donut");
    Equal(0.30f, AoeResolver.DonutInnerRatio(53), "known donut inner ratio");
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

static void AutoSafeCallPlanner_TreatsDonutVariantsAsOuterRing()
{
    var type7 = AutoSafeCallPlanner.CreateVisual(new AoeResolver.AoeInfo(20, 7, true), "Variant 7");
    var type10 = AutoSafeCallPlanner.CreateVisual(new AoeResolver.AoeInfo(20, 10, true), "Variant 10");
    var omenDonut = AutoSafeCallPlanner.CreateVisual(new AoeResolver.AoeInfo(20, 2, false, OmenId: 53), "Omen Donut");

    NotNull(type7, "cast type 7 donut visual");
    NotNull(type10, "cast type 10 donut visual");
    NotNull(omenDonut, "omen donut visual");
    Equal("outer_ring", type7!.Gimmick, "type 7 gimmick");
    Equal("outer_ring", type10!.Gimmick, "type 10 gimmick");
    Equal("outer_ring", omenDonut!.Gimmick, "omen donut gimmick");
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

static void AutoSafeCallPlanner_SuppressesRaidWideMinimapCalls()
{
    var match = new MatchCondition { CastId = "0x6C60", CastName = "コキュートス" };

    True(AutoSafeCallPlanner.ShouldSuppressMinimap(match, (id, name) => id == 0x6C60),
        "raid-wide cast match should suppress minimap");
    False(AutoSafeCallPlanner.ShouldSuppressMinimap(match, (id, name) => false),
        "unmarked cast should keep minimap eligibility");
}

static void AutoSafeCallPlanner_IdMarkerDoesNotMatchSameNameDifferentId()
{
    // Id 付き marker は同名の別 cast_id を巻き込まないこと（0x2906「メテオ」marker が
    // 0x2903「メテオ」の安置 arena_view を誤抑止しない＝AutoSafeCallPlanner の name-fallback 修正）。
    var file = new TriggerFile
    {
        RaidWideMarkers = new List<RaidWideMarker>
        {
            new()
            {
                Id = "0x2906",
                Name = "メテオ",
                Source = "manual",
            },
        },
    };

    True(AutoSafeCallPlanner.IsRaidWide(file, 0x2906, "メテオ"),
        "Id 一致は raid-wide と判定されるべき");
    False(AutoSafeCallPlanner.IsRaidWide(file, 0x2903, "メテオ"),
        "同名でも別 Id は raid-wide ではない（Id 付き marker は名前 fallback しない）");
}

static void AutoSafeCallPlanner_UsesContentScopedRaidWideMarkers()
{
    var file = new TriggerFile
    {
        RaidWideMarkers = new List<RaidWideMarker>
        {
            new()
            {
                Id = "0x9E47",
                Name = "吸引",
                Source = "manual",
            },
        },
    };
    var match = new MatchCondition { CastId = "0x9E47", CastName = "吸引" };

    True(AutoSafeCallPlanner.IsRaidWide(file, 0x9E47, "吸引"),
        "content scoped marker should suppress matching cast");
    True(AutoSafeCallPlanner.ShouldSuppressMinimap(file, match),
        "content scoped marker should suppress minimap match");
    False(AutoSafeCallPlanner.IsRaidWide(new TriggerFile(), 0x9E47, "吸引"),
        "same cast should not be raid-wide in a different content file");
}

static void AutoSafeCallPlanner_IgnoresGlobalHpCorrelationRaidWideForSuppression()
{
    var autoDetected = new SafeCallDictionary.DictionaryEntry
    {
        RaidWide = true,
        Source = "hp_correlation",
    };
    var manual = new SafeCallDictionary.DictionaryEntry
    {
        RaidWide = true,
        Source = "manual",
    };

    False(AutoSafeCallPlanner.IsGlobalRaidWideSuppressionEntry(autoDetected),
        "old global HP-correlation entries must not suppress AoE across contents");
    True(AutoSafeCallPlanner.IsGlobalRaidWideSuppressionEntry(manual),
        "manual global overrides remain backward compatible");
}

static void AutoSafeCallPlanner_RemovesMinimapActionsForRaidWideSourceEvents()
{
    var actions = new List<ActionDefinition>
    {
        new() { Type = "tts", Text = "全体軽減" },
        new() { Type = "arena_view", Gimmick = "inner_circle", Callout = "外周安置" },
    };
    var source = new CastCompletedEvent(
        DateTimeOffset.UtcNow,
        100,
        "Boss",
        0x6C60,
        "コキュートス");

    var filtered = AutoSafeCallPlanner.RemoveMinimapActionsForRaidWideSource(
        actions,
        source,
        (id, name) => id == 0x6C60);

    Equal(1, filtered.Count, "raid-wide filtered action count");
    Equal("tts", filtered[0].Type, "raid-wide should keep non-minimap action");

    var unmarked = AutoSafeCallPlanner.RemoveMinimapActionsForRaidWideSource(
        actions,
        new CastStartedEvent(DateTimeOffset.UtcNow, 100, "Boss", 0x1111, "Targeted AoE", 5.0f, null),
        (id, name) => id == 0x6C60);

    Equal(2, unmarked.Count, "unmarked source should keep minimap action");
}

static void AutoSafeCallPlanner_RemovesMinimapActionsForRaidWideAttachedMatches()
{
    var actions = new List<ActionDefinition>
    {
        new() { Type = "tts", Text = "全体軽減" },
        new() { Type = "arena_view", Gimmick = "scatter", Callout = "散開" },
    };
    var match = new MatchCondition { CastId = "0x6C60", CastName = "コキュートス" };

    var filtered = AutoSafeCallPlanner.RemoveMinimapActionsForRaidWideMatch(
        actions,
        match,
        (id, name) => id == 0x6C60);

    Equal(1, filtered.Count, "raid-wide attached match filtered action count");
    Equal("tts", filtered[0].Type, "raid-wide attached match should keep non-minimap action");
}

static void ActionDispatcher_RemovesStaleAutoGeneratedArenaViews()
{
    var actions = new List<ActionDefinition>
    {
        new() { Type = "tts", Text = "line" },
        // gimmick も user layout も無い「完全に空」の旧自動予測 arena_view → 除去対象。
        new() { Type = "arena_view", Callout = "old empty auto safe call" },
        // gimmick(inner_circle/cone/outer_ring 等)を持つ正規の俯瞰図 → 残す。
        // ライブ構成 被検世界「シグマ」V4.0.json の28件はこの形（gimmick あり・user layout 無し）。
        new() { Type = "arena_view", Gimmick = "inner_circle", Callout = "内側へ" },
        // user layout(aoe_zones)を持つ arena_view → 残す。
        new()
        {
            Type = "arena_view",
            Gimmick = "user_layout",
            AoeZones = new List<StrategyAoeZone>
            {
                new() { Shape = "rect", RadiusM = 20 },
            },
        },
    };

    var filtered = ActionDispatcher.RemoveStaleAutoGeneratedArenaViews("auto_cast_67e5", actions);

    Equal(3, filtered.Count, "空の stale auto arena view のみ除去される");
    True(filtered.Any(a => string.Equals(a.Type, "tts", StringComparison.OrdinalIgnoreCase)), "tts should remain");
    True(filtered.Any(a => string.Equals(a.Gimmick, "inner_circle", StringComparison.OrdinalIgnoreCase)),
        "gimmick 付きの俯瞰図は残る（ライブの28件＝inner_circle/cone/outer_ring）");
    True(filtered.Any(a => (a.AoeZones?.Count ?? 0) > 0), "user layout arena view should remain");
    False(filtered.Any(a => string.Equals(a.Callout, "old empty auto safe call", StringComparison.Ordinal)),
        "gimmick も layout も無い空の arena_view は除去される");

    var manual = ActionDispatcher.RemoveStaleAutoGeneratedArenaViews("manual_trigger", actions);
    Equal(4, manual.Count, "manual trigger should keep arena views");
}

static void ActionDispatcher_DedupSuppressesRepeatedTextWithinWindow()
{
    var recent = new Dictionary<string, DateTimeOffset>();
    var t0 = DateTimeOffset.Parse("2026-05-29T00:00:00Z");

    True(ActionDispatcher.ShouldDispatchByDedup("tts", "もりもりサンダガ", t0, recent, 3.0),
        "first speak passes");
    False(ActionDispatcher.ShouldDispatchByDedup("tts", "もりもりサンダガ", t0.AddSeconds(2), recent, 3.0),
        "same text within window is suppressed (multi-caster の同名連呼)");
    True(ActionDispatcher.ShouldDispatchByDedup("tts", "波動弾", t0.AddSeconds(2), recent, 3.0),
        "different text passes");
    True(ActionDispatcher.ShouldDispatchByDedup("tts", "もりもりサンダガ", t0.AddSeconds(4), recent, 3.0),
        "same text after window passes again (正当な再来は読む)");
    True(ActionDispatcher.ShouldDispatchByDedup("arena_view", null, t0, recent, 3.0),
        "text-less action is never deduped");
    True(ActionDispatcher.ShouldDispatchByDedup("arena_view", null, t0, recent, 3.0),
        "text-less action passes repeatedly");
    True(ActionDispatcher.ShouldDispatchByDedup("overlay_text", "波動弾", t0.AddSeconds(2), recent, 3.0),
        "different type is a separate dedup key");
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

static void StrategyPlanResolver_SuppressesRaidWideMechanicMinimap()
{
    var profile = new StrategyProfile
    {
        Id = "default",
        Enabled = true,
        SpreadPositions = new List<StrategyPosition>
        {
            new() { Slot = "D1", Label = "D1", X = -8, Z = -8 },
        },
    };
    var mechanic = new MechanicStrategy
    {
        Id = "cocytus",
        Label = "コキュートス",
        AttachedTo = new MatchCondition { CastId = "0x6C60", CastName = "コキュートス" },
        WarningText = "全体軽減",
        Gimmick = "scatter",
        Positions = new List<string> { "D1" },
    };

    var actions = StrategyPlanResolver.BuildReminderActions(
        profile,
        mechanic,
        (id, name) => id == 0x6C60);

    Equal(1, actions.Count, "raid-wide mechanic action count");
    Equal("tts", actions[0].Type, "raid-wide mechanic should keep audio reminder");
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

static void StrategyDraftGenerator_CreatesMechanicDraftsFromLearnedTimeline()
{
    // object_appear は draft 対象外（ボス名 / オブジェクト名がタイムラインを汚染する問題への対策）
    // → cast_start のみが draft 化される
    var aggregate = new AggregatedEvents(new[]
    {
        new AggregatedEvent(new EventKey("cast_start", "0x1111", "Raidwide", "Boss", null), 2, 12)
        {
            Occurrences = new[]
            {
                new AggregatedOccurrence(0, 12.0, 2, new[] { 11.8, 12.2 }),
            },
        },
        new AggregatedEvent(new EventKey("object_appear", "4000", "Tower", "Tower", null), 1, 28)
        {
            Occurrences = new[]
            {
                new AggregatedOccurrence(0, 28.0, 1, new[] { 28.0 }),
            },
        },
    }, 2, 3, 2);
    var profile = new StrategyProfile { Id = "default", Name = "Default" };

    var result = StrategyDraftGenerator.Generate(aggregate, profile);

    // object_appear は draft 対象外 → cast のみ
    Equal(1, result.Generated.Count, "generated draft count (object_appear excluded)");
    Equal("0x1111", result.Generated[0].AttachedTo?.CastId, "cast draft id");
    Equal(0, result.Generated[0].OccurrenceIndex ?? -1, "cast occurrence index");
    Equal("cast_start", result.Generated[0].SourceEventType, "cast draft event type");
}

static void StrategyDraftGenerator_SkipsExistingAttachedMechanics()
{
    var aggregate = new AggregatedEvents(new[]
    {
        new AggregatedEvent(new EventKey("cast_start", "0x1111", "Raidwide", "Boss", null), 1, 12)
        {
            Occurrences = new[]
            {
                new AggregatedOccurrence(0, 12.0, 1, new[] { 12.0 }),
            },
        },
    }, 1, 1, 1);
    var profile = new StrategyProfile
    {
        Id = "default",
        Name = "Default",
        Mechanics = new List<MechanicStrategy>
        {
            new()
            {
                Id = "existing",
                Label = "Raidwide",
                AttachedTo = new MatchCondition { CastId = "0x1111", CastName = "Raidwide" },
                OccurrenceIndex = 0,
            },
        },
    };

    var result = StrategyDraftGenerator.Generate(aggregate, profile);

    Equal(0, result.Generated.Count, "duplicate draft count");
    Equal(1, result.Skipped.Count, "skipped draft count");
}

static void StrategyDraftGenerator_KeepsSameCastDifferentSourcesSeparate()
{
    var aggregate = new AggregatedEvents(new[]
    {
        new AggregatedEvent(new EventKey("cast_start", "0x7777", "Twin Cleave", "Boss A", null), 1, 10)
        {
            Occurrences = new[]
            {
                new AggregatedOccurrence(0, 10.0, 1, new[] { 10.0 }),
            },
        },
        new AggregatedEvent(new EventKey("cast_start", "0x7777", "Twin Cleave", "Boss B", null), 1, 10.2)
        {
            Occurrences = new[]
            {
                new AggregatedOccurrence(0, 10.2, 1, new[] { 10.2 }),
            },
        },
    }, 1, 2, 1);
    var profile = new StrategyProfile { Id = "default", Name = "Default" };

    var result = StrategyDraftGenerator.Generate(aggregate, profile);

    Equal(2, result.Generated.Count, "two source-specific drafts");
    SequenceEqual(new[] { "Boss A", "Boss B" },
        result.Generated.Select(m => m.AttachedTo?.Source ?? "").ToArray(),
        "draft sources");
}

static void StrategyDraftGenerator_InheritsArenaShapeFromProfile()
{
    // 「正方形のプロファイルなのに自動生成 mechanic のマップが円形」事故対策の回帰テスト。
    var aggregate = new AggregatedEvents(new[]
    {
        new AggregatedEvent(new EventKey("cast_start", "0x2222", "Spread", "Boss", null), 1, 8)
        {
            Occurrences = new[]
            {
                new AggregatedOccurrence(0, 8.0, 1, new[] { 8.0 }),
            },
        },
    }, 1, 1, 1);
    var profile = new StrategyProfile
    {
        Id = "default",
        Name = "Default",
        ArenaShape = "square",
        ArenaWidth = 40.0,
        ArenaDepth = 40.0,
        ArenaCenterX = 100.6,
        ArenaCenterZ = 100.4,
    };

    var result = StrategyDraftGenerator.Generate(aggregate, profile);

    Equal(1, result.Generated.Count, "generated draft count");
    var draft = result.Generated[0];
    Equal("square", draft.ArenaShape, "draft inherits arena shape");
    Equal(40.0, draft.ArenaWidth ?? -1, "draft inherits arena width");
    Equal(40.0, draft.ArenaDepth ?? -1, "draft inherits arena depth");
    Equal(100.6, draft.ArenaCenterX ?? -1, "draft inherits arena center X");
    Equal(100.4, draft.ArenaCenterZ ?? -1, "draft inherits arena center Z");
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
            out var label,
            out _),
        "nearest sync point should be selected");
    Equal(2.0, offset, "sync offset");
    Equal("sync:second", label, "sync label");

    False(SyncOffsetTracker.TryFindBestSyncPointOffset(
            syncPoints,
            actualRelSec: 50.0,
            currentOffsetSec: 0.0,
            actualCastId: 0xAAAA,
            out _,
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

static void NoteReminderService_DropsStaleNotesOnReschedule()
{
    // BranchResolvedEvent / PhaseTransitionedEvent で Schedule() が再構築されたとき、
    // 既に過去になった前半ノートが _pending に再投入されて即時一斉発火する重複読み上げを防ぐ。
    // 後半フェーズ突入(nowRel=120)時点で、前半ノート(実イベント=50s)は stale として発火しない。
    False(NoteReminderService.ShouldFireDueNote(
            fireAtRelSec: 0.0,
            eventAtRelSec: 50.0,
            offsetSec: 0.0,
            nowRelSec: 120.0),
        "前半ノート(event=50, now=120)は stale として発火しない");

    // 後半フェーズの新規ノート(実イベント=130s)は通常どおり発火する。
    True(NoteReminderService.ShouldFireDueNote(
            fireAtRelSec: 120.0,
            eventAtRelSec: 130.0,
            offsetSec: 0.0,
            nowRelSec: 121.0),
        "後半ノート(event=130, now=121)は通常発火");

    // staleGrace 窓内(event=119.5, now=120, grace=1.0)はギリギリ発火する。
    True(NoteReminderService.ShouldFireDueNote(
            fireAtRelSec: 0.0,
            eventAtRelSec: 119.5,
            offsetSec: 0.0,
            nowRelSec: 120.0),
        "grace 窓内(event=119.5, now=120)は発火");

    // 発火予定時刻がまだ未来(fireAt+offset > now)のものは発火しない。
    False(NoteReminderService.ShouldFireDueNote(
            fireAtRelSec: 125.0,
            eventAtRelSec: 130.0,
            offsetSec: 0.0,
            nowRelSec: 120.0),
        "発火予定が未来(fireAt=125 > now=120)はまだ発火しない");
}

static void PhaseTransitionPolicy_BuildsPhaseOrdinals()
{
    var file = new TriggerFile
    {
        SyncPoints = new List<SyncPoint>
        {
            // 意図的に時刻逆順で登録 → expected_time 昇順で並べ替えられることを確認
            new() { Id = "p3", Type = "cast_start", CastId = "0xP3", ExpectedTime = 360, Phase = "P3" },
            new() { Id = "p2", Type = "cast_start", CastId = "0xP2", ExpectedTime = 180, Phase = "P2" },
            new() { Id = "nophase", Type = "cast_start", CastId = "0xNP", ExpectedTime = 90 },
        },
        StrategyProfiles = new List<StrategyProfile>
        {
            new()
            {
                Id = "default",
                Enabled = true,
                Mechanics = new List<MechanicStrategy>
                {
                    new()
                    {
                        Id = "enrage_phase",
                        Enabled = true,
                        Triggers = new List<MechanicTrigger>
                        {
                            new() { Type = "hp_pct", HpPctBelow = 20, Phase = "P5" },
                        },
                    },
                },
            },
        },
    };

    var ordinals = PhaseTransitionPolicy.BuildPhaseOrdinals(file);

    Equal(3, ordinals.Count, "sync_point 2件 + hp_pct 1件 = 3フェーズ");
    Equal(1, ordinals["P2"], "P2 は expected_time 最小なので ordinal 1");
    Equal(2, ordinals["P3"], "P3 は次なので ordinal 2");
    Equal(3, ordinals["P5"], "hp_pct フェーズは sync_point の後で ordinal 3");
    False(ordinals.ContainsKey("P1"), "phase 注釈の無い初期フェーズはマップに入らない");
}

static void PhaseTransitionPolicy_IsPastPhase_SuppressesEarlierPhasesOnly()
{
    var ordinals = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["P2"] = 1,
        ["P3"] = 2,
        ["P5"] = 3,
    };

    // 現在 P3（ordinal 2）にいるとき
    const int current = 2;

    // 初期フェーズ P1（マップ外 = ordinal 0）は過去 → 抑制
    True(PhaseTransitionPolicy.IsPastPhase("P1", current, ordinals), "初期フェーズ P1 は過去");
    // P2（ordinal 1）も過去 → 抑制
    True(PhaseTransitionPolicy.IsPastPhase("P2", current, ordinals), "P2 は過去");
    // 現在フェーズ P3（ordinal 2）は過去ではない → 表示
    False(PhaseTransitionPolicy.IsPastPhase("P3", current, ordinals), "現在フェーズ P3 は表示");
    // 未来フェーズ P5（ordinal 3）は過去ではない → 表示（先取り）
    False(PhaseTransitionPolicy.IsPastPhase("P5", current, ordinals), "未来フェーズ P5 は表示");
    // phase 無し（共通ギミック）は常に表示
    False(PhaseTransitionPolicy.IsPastPhase(null, current, ordinals), "phase 無しの共通ギミックは常に表示");
}

static void PhaseTransitionPolicy_NoPhasesAnnotated_FilterDisabled()
{
    var empty = new Dictionary<string, int>(StringComparer.Ordinal);

    // フェーズ未注釈（マップ空）のコンテンツでは、どのフェーズでも過去扱いしない（後方互換）。
    False(PhaseTransitionPolicy.IsPastPhase("P1", 5, empty), "マップ空なら絞り込み無効（P1）");
    False(PhaseTransitionPolicy.IsPastPhase(null, 5, empty), "マップ空なら絞り込み無効（共通）");
    Equal(0, PhaseTransitionPolicy.ResolvePhaseOrdinal("P1", empty), "マップ外は ordinal 0");
}

static void TimelineNoteResolver_SelectOccurrenceTime_UsesOccurrenceIndex()
{
    var times = new double[] { 92.0, 100.0 };

    // hintTime<=0（mechanic.Time 未設定）: occurrenceIndex で選ぶ。以前は常に times[0]=92 だった。
    Equal(92.0, TimelineNoteResolver.SelectOccurrenceTime(times, 0.0, 0), "occurrence[0]=前半=92");
    Equal(100.0, TimelineNoteResolver.SelectOccurrenceTime(times, 0.0, 1), "occurrence[1]=後半=100");
    Equal(100.0, TimelineNoteResolver.SelectOccurrenceTime(times, 0.0, 5), "範囲外は末尾にクランプ=100");

    // hintTime>0（明示 Time 付きノート）: 最も近い出現（従来挙動を維持）。
    Equal(100.0, TimelineNoteResolver.SelectOccurrenceTime(times, 98.0, 0), "hint=98 は最も近い 100");
    Equal(92.0, TimelineNoteResolver.SelectOccurrenceTime(times, 93.0, 1), "hint=93 は最も近い 92（hint優先）");

    // 単一出現
    Equal(50.0, TimelineNoteResolver.SelectOccurrenceTime(new double[] { 50.0 }, 0.0, 3), "単一出現はその時刻");
}

static void AddObjectAoe_MatchesObjectSource_RejectsShortPartial()
{
    // 完全一致は常に許可
    True(FfxivEchoes.Triggers.AddObjectAoeService.MatchesObjectSource("ケツァクウァトル", "ケツァクウァトル"),
        "完全一致は許可");
    // 長い名前同士の部分一致は許可
    True(FfxivEchoes.Triggers.AddObjectAoeService.MatchesObjectSource("ケツァクウァトルの分身", "ケツァクウァトル"),
        "3文字以上の部分一致は許可");
    // 短い名前("炎")が長い名前に誤マッチしない
    False(FfxivEchoes.Triggers.AddObjectAoeService.MatchesObjectSource("炎の精霊", "炎"),
        "短い名前(2文字未満扱い)は部分一致で誤マッチしない");
    False(FfxivEchoes.Triggers.AddObjectAoeService.MatchesObjectSource("炎", "炎の精霊"),
        "逆方向でも短い名前は誤マッチしない");
    // null/空
    False(FfxivEchoes.Triggers.AddObjectAoeService.MatchesObjectSource(null, "炎の精霊"), "null source は false");
}

static void BossRelativePreset_Angle90_PointsToBossRight()
{
    // FFXIV: Rotation=0 は +Z(南)向き・+X=東。南を向くと右手は西(-X)。
    // 「正面=0 / 右=90 / 後=180 / 左=270」を満たすことを純粋ヘルパで検証する。
    // 以前は加算方向が逆で angle=90 が +X(東=ボスの左) を指しており安置が逆側になっていた。
    var front = BossRelativePreset.ComputeOffset(bossFacingRad: 0f, angleDeg: 0f, distance: 10f);
    NearlyEqual(0f, front.X, "正面(0) の X は ~0", 0.001f);
    NearlyEqual(10f, front.Z, "正面(0) は +Z", 0.001f);

    var right = BossRelativePreset.ComputeOffset(0f, 90f, 10f);
    NearlyEqual(-10f, right.X, "右(90) は -X(西=南向き時の右)", 0.001f);
    NearlyEqual(0f, right.Z, "右(90) の Z は ~0", 0.001f);

    var back = BossRelativePreset.ComputeOffset(0f, 180f, 10f);
    NearlyEqual(0f, back.X, "後(180) の X は ~0", 0.001f);
    NearlyEqual(-10f, back.Z, "後(180) は -Z", 0.001f);

    var left = BossRelativePreset.ComputeOffset(0f, 270f, 10f);
    NearlyEqual(10f, left.X, "左(270) は +X(東=南向き時の左)", 0.001f);
    NearlyEqual(0f, left.Z, "左(270) の Z は ~0", 0.001f);
}

static void BranchObserver_IsWithinBranchWindow_UsesGroupActivation()
{
    var start = DateTimeOffset.UnixEpoch;
    True(BranchObserverService.IsWithinBranchWindow(start.AddSeconds(20), start, 30),
        "起点から20s（< window30）は判定可");
    False(BranchObserverService.IsWithinBranchWindow(start.AddSeconds(31), start, 30),
        "起点から31s（> window30）は判定不可");

    // 後半フェーズの分岐：起点がフェーズ遷移時刻にリセットされていれば、戦闘開始から
    // 190s 経過していても「フェーズ起点から10s」として window 内になる（FIX-10 の核心）。
    var phaseStart = start.AddSeconds(180);
    True(BranchObserverService.IsWithinBranchWindow(phaseStart.AddSeconds(10), phaseStart, 30),
        "フェーズ起点から10s後は window 内（戦闘開始基準なら190s>30で永久Pendingだった）");
    False(BranchObserverService.IsWithinBranchWindow(phaseStart.AddSeconds(40), phaseStart, 30),
        "フェーズ起点から40s後は window 超過");
}

static void UpcomingTimelinePolicy_FiltersBranchRejected_TriggersPath()
{
    var file = new TriggerFile
    {
        ActiveStrategyProfileId = "default",
        StrategyProfiles = new List<StrategyProfile>
        {
            new()
            {
                Id = "default",
                Enabled = true,
                Mechanics = new List<MechanicStrategy>
                {
                    // 新パス（Triggers）で branch_b に紐づくキャスト（AttachedTo は使わない）
                    new()
                    {
                        Id = "b1",
                        Enabled = true,
                        BranchId = "branch_b",
                        Triggers = new List<MechanicTrigger>
                        {
                            new() { Type = "cast", Match = new MatchCondition { CastId = "0xBEEF" } },
                        },
                    },
                },
            },
        },
    };
    var predictions = new List<RecordingTimelinePrediction>
    {
        new("cast_start", 15, "AttackB", "0xBEEF", "Boss", null, 5, 0, 5, 1.0, 0.3),
        new("cast_start", 20, "Common", "0xCAFE", "Boss", null, 5, 0, 5, 1.0, 0.1),
    };

    // branch_b が Rejected（active は branch_a のみ）
    var filtered = UpcomingTimelinePolicy.FilterBranchRejectedPredictions(
        predictions, file, id => id is null or "branch_a");

    Equal(1, filtered.Count, "Triggers パスで branch_b に紐づくキャストも除外される");
    False(filtered.Any(p => p.Id == "0xBEEF"), "branch_b(Triggers) のキャストは除外");
    True(filtered.Any(p => p.Id == "0xCAFE"), "未紐づけ共通キャストは残る");
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

static void PredictedCastReminderService_DrawsInferredVisualsUnlessSuppressed()
{
    True(PredictedCastReminderService.ShouldDrawAutoInferredPredictionVisual(suppressMinimap: false),
        "recording predictions should draw inferred minimap/floor AoE visuals when not suppressed");
    False(PredictedCastReminderService.ShouldDrawAutoInferredPredictionVisual(suppressMinimap: true),
        "suppressed predictions should avoid inferred visuals");
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

static void AutoAttackTimingService_CreatesUniqueTimerTriggerIds()
{
    var t0 = new DateTimeOffset(2026, 5, 12, 0, 0, 0, TimeSpan.Zero);
    var first = AutoAttackTimingService.BuildTimerTriggerId(1001, 2001, t0.AddSeconds(3.0));
    var second = AutoAttackTimingService.BuildTimerTriggerId(1001, 2001, t0.AddSeconds(6.0));

    True(first.StartsWith("__auto_attack_timer_1001_2001_", StringComparison.Ordinal), "timer id prefix");
    False(string.Equals(first, second, StringComparison.Ordinal), "repeated AA timers should be distinct");
}

static void AutoAttackTimingService_WarningTextIncludesSource()
{
    var t0 = new DateTimeOffset(2026, 5, 12, 0, 0, 0, TimeSpan.Zero);
    var one = new[]
    {
        new AutoAttackWarning(1001, 2001, t0, 0.5, 2.5, 1.0),
    };
    var two = new[]
    {
        new AutoAttackWarning(1001, 2001, t0, 0.5, 2.5, 1.0),
        new AutoAttackWarning(1002, 2001, t0, 0.5, 2.5, 1.0),
    };

    Equal("AA: ゾディアーク", AutoAttackTimingService.BuildWarningText(one,
        id => id == 1001 ? "ゾディアーク" : null), "single AA source name");
    Equal("AA x2: ゾディアーク / ベヒーモス", AutoAttackTimingService.BuildWarningText(two,
        id => id == 1001 ? "ゾディアーク" : id == 1002 ? "ベヒーモス" : null), "multi AA source names");
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
    Equal(AttackDisplayDecision.None,
        AttackDisplayPolicy.Decide(settings, new AttackDisplayRequest(false, false, false, true)),
        "non-aoe cast should not draw guessed attack pulses");

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

static void AutoAoeDisplayPolicy_GatesByZoneSetting()
{
    False(AutoAoeDisplayPolicy.IsEnabled(null), "auto AoE should stay off without a zone file");
    // 既定 ON：プラグインの主目的が「敵キャストの自動 AoE 表示」のため
    True(AutoAoeDisplayPolicy.IsEnabled(new TriggerFile()), "auto AoE should default ON for new files");
    False(AutoAoeDisplayPolicy.IsEnabled(new TriggerFile
    {
        AutoSettings = new AutoSettings { ShowAutoTelegraphs = false },
    }), "auto AoE should follow show_auto_telegraphs (off)");
}

static void AutoAoeDisplayPolicy_AllowsInstantActionImpactTelegraphs()
{
    var file = new TriggerFile();

    True(AutoAoeDisplayPolicy.ShouldDrawInstantActionTelegraph(file),
        "instant action_used AoE should draw a short impact feedback when auto telegraphs are enabled");
    False(AutoAoeDisplayPolicy.ShouldDrawInstantActionTelegraph(new TriggerFile
    {
        AutoSettings = new AutoSettings { ShowAutoTelegraphs = false },
    }), "disabled auto telegraphs should suppress instant impact feedback");
}

static void TriggerStore_KeepsMeaningfulObjectMechanicsEnabled()
{
    var meaningful = new MechanicStrategy
    {
        SourceEventType = "object_appear",
        Enabled = true,
        AoeZones = new List<StrategyAoeZone>
        {
            new() { Shape = "rect", RadiusM = 20, HalfWidthM = 2.5 },
        },
    };
    var emptyLegacyNoise = new MechanicStrategy
    {
        SourceEventType = "object_appear",
        Enabled = true,
    };

    False(TriggerStore.IsLegacyAutoNoiseMechanic(meaningful),
        "object mechanics with explicit AoE zones should not be disabled on reload");
    True(TriggerStore.IsLegacyAutoNoiseMechanic(emptyLegacyNoise),
        "empty object_appear auto drafts can still be treated as legacy noise");
}

static void MechanicTriggerService_WaitsForStableObjectGroups()
{
    False(MechanicTriggerService.ShouldFireObjectGroup(
            count: 2,
            minCount: 2,
            maxCount: null,
            secondsSinceLastAdd: 0.1,
            secondsSinceOldest: 0.1,
            windowSec: 1.5),
        "object group should wait for a short quiet period after the latest add");

    True(MechanicTriggerService.ShouldFireObjectGroup(
            count: 2,
            minCount: 2,
            maxCount: null,
            secondsSinceLastAdd: 0.4,
            secondsSinceOldest: 0.4,
            windowSec: 1.5),
        "object group can fire after the quiet period");

    False(MechanicTriggerService.ShouldFireObjectGroup(
            count: 2,
            minCount: 2,
            maxCount: 4,
            secondsSinceLastAdd: 0.4,
            secondsSinceOldest: 0.4,
            windowSec: 1.5),
        "object group with an expected max should keep waiting inside the object window");

    True(MechanicTriggerService.ShouldFireObjectGroup(
            count: 2,
            minCount: 2,
            maxCount: 4,
            secondsSinceLastAdd: 1.6,
            secondsSinceOldest: 1.6,
            windowSec: 1.5),
        "object group should eventually fire even if the expected max never arrives");
}

static void MechanicTriggerService_DedupeKeyIncludesSource()
{
    var left = MechanicTriggerService.BuildFireDedupeKey("default", "line_aoe", "cast:1001");
    var right = MechanicTriggerService.BuildFireDedupeKey("default", "line_aoe", "cast:1002");
    var global = MechanicTriggerService.BuildFireDedupeKey("default", "line_aoe", null);

    False(string.Equals(left, right, StringComparison.Ordinal),
        "same mechanic from different sources should not dedupe each other");
    True(global.EndsWith("::global", StringComparison.Ordinal), "missing source should keep a stable global key");
}

static void AutoAoeDisplayPolicy_AllowsLearnedSingleObjectOnlyWhenEnabled()
{
    var enabled = new TriggerFile
    {
        AutoSettings = new AutoSettings { ShowAutoTelegraphs = true },
    };
    var disabled = new TriggerFile
    {
        AutoSettings = new AutoSettings { ShowAutoTelegraphs = false },
    };

    True(AutoAoeDisplayPolicy.ShouldDrawObjectGroup(enabled, objectCount: 2, hasLearnedAoe: false, minGroupSize: 2),
        "enabled grouped objects should draw with fallback radius");
    True(AutoAoeDisplayPolicy.ShouldDrawObjectGroup(enabled, objectCount: 1, hasLearnedAoe: true, minGroupSize: 2),
        "enabled learned single object should draw");
    False(AutoAoeDisplayPolicy.ShouldDrawObjectGroup(enabled, objectCount: 1, hasLearnedAoe: false, minGroupSize: 2),
        "unknown single object should not draw");
    False(AutoAoeDisplayPolicy.ShouldDrawObjectGroup(disabled, objectCount: 2, hasLearnedAoe: false, minGroupSize: 2),
        "disabled zone should suppress object AoE");
}

static void AutoAoeDisplayPolicy_UsesActiveProfileArenaCalibration()
{
    var file = new TriggerFile
    {
        ActiveStrategyProfileId = "p2",
        StrategyProfiles = new List<StrategyProfile>
        {
            new()
            {
                Id = "p1",
                ArenaRadius = 20,
                ArenaCenterX = 1,
                ArenaCenterZ = 2,
            },
            new()
            {
                Id = "p2",
                ArenaShape = "rect",
                ArenaRadius = 30,
                ArenaWidth = 70,
                ArenaDepth = 50,
                ArenaCenterX = 100,
                ArenaCenterZ = 200,
            },
        },
    };

    var arena = AutoAoeDisplayPolicy.ResolveArena(file);

    Equal("rect", arena.ArenaShape, "auto AoE arena shape");
    Equal(35.0, arena.ArenaRadius, "auto AoE arena radius should cover widest half extent");
    Equal(70.0, arena.ArenaWidth!.Value, "auto AoE arena width");
    Equal(50.0, arena.ArenaDepth!.Value, "auto AoE arena depth");
    Equal(new Vector3(100, 0, 200), arena.LockedArenaCenter!.Value, "auto AoE arena center");
}

static void AddObjectAoeService_SuppressesUnknownObjectGroupFallback()
{
    var arena = new AutoAoeArenaConfig(
        ArenaRadius: 20.0,
        ArenaShape: "square",
        ArenaWidth: 40.0,
        ArenaDepth: 40.0,
        LockedArenaCenter: null);

    False(AddObjectAoeService.TryBuildUnknownObjectFallbackAoeZone(arena, out _),
        "unknown object groups must not create guessed line AoEs");
}

static void AddObjectAoeService_SuppressesMixedObjectCohortFallback()
{
    var enabled = new TriggerFile
    {
        AutoSettings = new AutoSettings { ShowAutoTelegraphs = true },
    };

    False(
        AddObjectAoeService.ShouldUseFallbackCohort(
            enabled,
            objectCount: 2,
            namedGroupWillFire: false,
            secondsSinceCast: 4.0),
        "mixed object cohorts are too ambiguous to draw without learned geometry");
    False(
        AddObjectAoeService.ShouldUseFallbackCohort(
            enabled,
            objectCount: 2,
            namedGroupWillFire: true,
            secondsSinceCast: 4.0),
        "mixed fallback should not duplicate a named group that already fired");
    False(
        AddObjectAoeService.ShouldUseFallbackCohort(
            enabled,
            objectCount: 2,
            namedGroupWillFire: false,
            secondsSinceCast: 20.0),
        "combat-start resnapshot noise should not draw without a recent cast");
}

static void AddObjectAoeService_SuppressesRepeatedObjectAoeForDisplayDuration()
{
    var firedAt = new DateTimeOffset(2026, 5, 13, 12, 0, 0, TimeSpan.Zero);
    var suppressUntil = AddObjectAoeService.ComputeObjectAoeSuppressUntil(firedAt, displayDurationSec: 14.0);

    True(AddObjectAoeService.IsObjectAoeSuppressed(firedAt.AddSeconds(7), suppressUntil),
        "same object AoE should not be re-fired while the previous display is still visible");
    False(AddObjectAoeService.IsObjectAoeSuppressed(firedAt.AddSeconds(14.1), suppressUntil),
        "same object AoE may fire again after the display duration has elapsed");
}

static void AddObjectAoeService_SharesSuppressKeysBetweenAppearAndLiveScan()
{
    var fromAppear = AddObjectAoeService.MakeObjectAoeSuppressKey(9020, "ケツァクワァトル");
    var fromLiveScan = AddObjectAoeService.MakeLiveObjectAoeSuppressKey(9020, "ケツァクワァトル");

    Equal(fromAppear, fromLiveScan,
        "object_appear AoE and later cast-complete live scan must dedupe the same object");
}

static void AddObjectAoeService_ScansLiveObjectsAfterInstantMechanicActions()
{
    var ev = new ActionUsedEvent(
        Timestamp: DateTimeOffset.UtcNow,
        SourceId: 3001,
        SourceName: "ゾディアーク",
        ActionId: 0x67BF,
        ActionName: "パラデイグマ",
        TargetId: null,
        IsAutoAttack: false,
        IsPlayer: false);

    True(AddObjectAoeService.ShouldScheduleLiveObjectScanForInstantAction(
            ev,
            autoAoeEnabled: true,
            sourceIsPlayerOwned: false,
            isRaidWide: false),
        "instant boss mechanic actions should open a live object scan window");

    False(AddObjectAoeService.ShouldScheduleLiveObjectScanForInstantAction(
            ev with { IsPlayer = true },
            autoAoeEnabled: true,
            sourceIsPlayerOwned: false,
            isRaidWide: false),
        "player actions must not open object scan windows");

    False(AddObjectAoeService.ShouldScheduleLiveObjectScanForInstantAction(
            ev with { IsAutoAttack = true },
            autoAoeEnabled: true,
            sourceIsPlayerOwned: false,
            isRaidWide: false),
        "auto attacks must not open object scan windows");
}

static void AddObjectAoeService_DoesNotScanFromKnownPlayerActions()
{
    var ev = new ActionUsedEvent(
        Timestamp: DateTimeOffset.UtcNow,
        SourceId: 2001,
        SourceName: "Self",
        ActionId: 0x000F,
        ActionName: "ライオットソード",
        TargetId: 3001,
        IsAutoAttack: false,
        IsPlayer: false);

    False(AddObjectAoeService.ShouldScheduleLiveObjectScanForInstantAction(
            ev,
            autoAoeEnabled: true,
            sourceIsPlayerOwned: false,
            isRaidWide: false,
            actionIsPlayerAction: true),
        "known PC action should not start live object scans even if IsPlayer/source lookup missed");
}

static void AddObjectAoeService_SkipsOversizedLiveObjectCohorts()
{
    True(AddObjectAoeService.ShouldDrawLiveObjectGroup(4),
        "four mechanic objects should still draw");
    True(AddObjectAoeService.ShouldDrawLiveObjectGroup(8),
        "eight mechanic objects is the largest supported live cohort");
    False(AddObjectAoeService.ShouldDrawLiveObjectGroup(9),
        "oversized same-name live cohorts are likely boss parts/background noise");
    False(AddObjectAoeService.ShouldDrawLiveObjectGroup(0),
        "empty live cohorts should not draw");
}

static void AddObjectAoeService_SelectsRecordedObjectActionCandidatesBySource()
{
    var agg = new AggregatedEvents(new[]
    {
        new AggregatedEvent(new EventKey("action_used", "0x6651", "Action#26193", "ケツァクワァトル", null), 4, 32.2)
        {
            ObservedTimesSeconds = new[] { 32.2, 32.3, 32.4, 32.5 },
        },
        new AggregatedEvent(new EventKey("action_used", "0x67E1", "ケラノウス・エイドロン", "ケツァクワァトル", null), 2, 32.1)
        {
            ObservedTimesSeconds = new[] { 32.1, 32.2 },
        },
        new AggregatedEvent(new EventKey("action_used", "0x67BF", "パラデイグマ", "ゾディアーク", null), 1, 20.0)
        {
            ObservedTimesSeconds = new[] { 20.0 },
        },
    }, 1, 7, 1);

    var candidates = AddObjectAoeService.SelectRecordedObjectActionCandidates(agg, "ケツァクワァトル", excludedSourceName: "ゾディアーク");

    Equal(2, candidates.Count, "object action candidate count");
    Equal(0x6651u, candidates[0].ActionId, "most observed object action should be first");
    Equal(4, candidates[0].ObservationCount, "most observed object action count");
}

static void AddObjectAoeService_RejectsPlaceholderObjectPositions()
{
    var center = new Vector3(100, 0, 100);

    False(
        AddObjectAoeService.IsUsableObjectAoePosition(new Vector3(100.1f, 0, 99.9f), center),
        "combat-start object resnapshot at arena center should not create object AoE");
    True(
        AddObjectAoeService.IsUsableObjectAoePosition(new Vector3(112f, 0, 94f), center),
        "live object positions away from center should be usable");
}

static void ObjectAoeRuleResolver_MatchesProfileRules()
{
    var file = new TriggerFile
    {
        ActiveStrategyProfileId = "default",
        StrategyProfiles = new List<StrategyProfile>
        {
            new()
            {
                Id = "default",
                ObjectAoeRules = new List<ObjectAoeRule>
                {
                    new()
                    {
                        Id = "paradeigma-line",
                        ObjectName = "ケラノウス",
                        NameMatch = "contains",
                        DataId = 14387,
                        Shape = "line",
                        RadiusM = 40,
                        HalfWidthM = 6,
                        Label = "直線",
                        Source = "manual",
                    },
                },
            },
        },
    };
    var arena = new AutoAoeArenaConfig(20, "square", 40, 40, new Vector3(100, 0, 100));

    True(ObjectAoeRuleResolver.TryResolve(file, 14387, "ケラノウス・エイドロン", arena, out var match),
        "object name + data id should resolve a profile-scoped object AoE rule");
    Equal("line", match.Zone.Shape, "object AoE shape");
    Equal("each_matched_object", match.Zone.Anchor!, "object AoE anchor");
    Equal(40.0, match.Zone.RadiusM, "object AoE radius");
    Equal(6.0, match.Zone.HalfWidthM!.Value, "object AoE line half width");
    Equal("manual", match.Source, "object AoE source");
}

static void ObjectAoeRuleResolver_MatchesJapaneseObjectNameVariants()
{
    var rule = new ObjectAoeRule
    {
        Enabled = true,
        ObjectName = "ケツァクウァトル",
        NameMatch = "contains",
        Shape = "donut",
        RadiusM = 15,
    };

    True(ObjectAoeRuleResolver.Matches(rule, dataId: 0, "ケツァクワァトル"),
        "object AoE rules should tolerate the observed ウァ/ワァ name variant");
}

static void ObjectAoeRuleResolver_IgnoresAutoLearnedSourceActorRules()
{
    var file = new TriggerFile
    {
        ActiveStrategyProfileId = "default",
        StrategyProfiles = new List<StrategyProfile>
        {
            new()
            {
                Id = "default",
                ObjectAoeRules = new List<ObjectAoeRule>
                {
                    new()
                    {
                        Id = "bad-zodiark-auto",
                        Enabled = true,
                        ObjectName = "ゾディアーク",
                        NameMatch = "contains",
                        Source = "recording_action",
                        Shape = "circle",
                        RadiusM = 5,
                    },
                },
                Mechanics = new List<MechanicStrategy>
                {
                    new()
                    {
                        Id = "paradeigma",
                        Label = "パラデイグマ",
                        AttachedTo = new MatchCondition
                        {
                            CastId = "0x67BF",
                            CastName = "パラデイグマ",
                            Source = "ゾディアーク",
                        },
                    },
                },
            },
        },
    };
    var arena = new AutoAoeArenaConfig(20, "square", 40, 40, new Vector3(100, 0, 100));

    False(ObjectAoeRuleResolver.TryResolve(file, 0, "ゾディアーク", arena, out _),
        "recording_action object rules that target a known boss/source actor should be ignored");

    file.StrategyProfiles[0].ObjectAoeRules[0].Source = "manual";
    True(ObjectAoeRuleResolver.TryResolve(file, 0, "ゾディアーク", arena, out _),
        "manual object AoE rules should remain available even when the name matches a source actor");
}

static void AddObjectAoeService_DoesNotPersistRecordingActionRulesForKnownEventSources()
{
    var file = new TriggerFile
    {
        ActiveStrategyProfileId = "default",
        StrategyProfiles = new List<StrategyProfile>
        {
            new()
            {
                Id = "default",
                Mechanics = new List<MechanicStrategy>
                {
                    new()
                    {
                        Id = "paradeigma",
                        Label = "パラデイグマ",
                        AttachedTo = new MatchCondition
                        {
                            CastId = "0x67BF",
                            CastName = "パラデイグマ",
                            Source = "ゾディアーク",
                        },
                    },
                },
            },
        },
    };

    False(ObjectAoeRuleResolver.ShouldPersistLearnedObjectRule(file, "ゾディアーク", "recording_action"),
        "recording_action learning should not persist boss/source actors as object AoE rules");
    True(ObjectAoeRuleResolver.ShouldPersistLearnedObjectRule(file, "ケツァクワァトル", "recording_action"),
        "recording_action learning may persist actual mechanic objects");
    True(ObjectAoeRuleResolver.ShouldPersistLearnedObjectRule(file, "ゾディアーク", "manual"),
        "manual/user-authored rules are explicit and should not be suppressed");
}

static void ObjectAoeRuleResolver_PreservesShapeFields()
{
    var arena = new AutoAoeArenaConfig(20, "circle", null, null, null);
    var donut = ObjectAoeRuleResolver.BuildZone(
        new ObjectAoeRule
        {
            Id = "donut",
            ObjectName = "ベヒーモス",
            Shape = "donut",
            RadiusM = 12,
            InnerRadiusM = 5,
        },
        arena);
    Equal("donut", donut.Shape, "donut object AoE shape");
    Equal(5.0, donut.InnerRadiusM!.Value, "donut inner radius");

    var cone = ObjectAoeRuleResolver.BuildZone(
        new ObjectAoeRule
        {
            Id = "cone",
            ObjectName = "メテオロス",
            Shape = "cone",
            RadiusM = 30,
            FanDeg = 120,
        },
        arena);
    Equal("cone", cone.Shape, "cone object AoE shape");
    Equal(120.0, cone.FanDeg!.Value, "cone fan");

    var line = ObjectAoeRuleResolver.BuildZone(
        new ObjectAoeRule
        {
            Id = "line",
            ObjectName = "秘紋",
            Shape = "line",
            RadiusM = 40,
        },
        arena);
    Equal(AoeGeometryPolicy.DefaultLineHalfWidthM, (float)line.HalfWidthM!.Value,
        "line object AoE should get readable default width");
}

static void ObjectAoeRuleResolver_LearnedNamedRulesIgnoreDataIdDrift()
{
    var zone = new StrategyAoeZone
    {
        Label = "直線",
        Shape = "line",
        RadiusM = 40,
        HalfWidthM = 5,
        DurationSec = 12,
        Color = "#FF6464",
        LiveFloorPaint = true,
    };

    var rule = ObjectAoeRuleResolver.CreateLearnedRule(
        dataId: 14387,
        objectName: "ケラノウス・エイドロン",
        zone,
        source: "recording");

    Null(rule.DataId, "learned named object rule should not pin data id when the name is available");
    True(ObjectAoeRuleResolver.Matches(rule, 99999, "ケラノウス・エイドロン"),
        "learned named object rule should survive data id drift");
    Equal(12.0, rule.DurationSec!.Value, "learned object AoE duration");
}

// ユーザーがルールを手動で無効化した時、recording_action 学習の重複防止が機能して
// 同名 active ルールが自動再追加されないことを保証する。
// 退行ガード：これが壊れるとユーザーの「ケツァクウァトル を無効化したのに AoE がまた出る」体感が再発する。
static void ObjectAoeRuleResolver_MatchesIgnoringEnabled_TrueForDisabledRule()
{
    var disabled = new ObjectAoeRule
    {
        Enabled = false,
        ObjectName = "ケツァクウァトル",
        NameMatch = "contains",
        Shape = "donut",
        RadiusM = 15,
        Source = "recording_action",
    };

    False(ObjectAoeRuleResolver.Matches(disabled, dataId: 0, "ケツァクウァトル"),
        "Matches should skip disabled rules (the existing semantics for active lookup)");

    True(ObjectAoeRuleResolver.MatchesIgnoringEnabled(disabled, dataId: 0, "ケツァクウァトル"),
        "MatchesIgnoringEnabled should still recognize disabled rules so duplicate-detection treats them as existing");

    True(ObjectAoeRuleResolver.MatchesIgnoringEnabled(disabled, dataId: 0, "ケツァクワァトル"),
        "MatchesIgnoringEnabled should also tolerate the ウァ/ワァ name variant");

    var unrelated = new ObjectAoeRule
    {
        Enabled = false,
        ObjectName = "別の何か",
        NameMatch = "contains",
        Shape = "donut",
        RadiusM = 15,
    };
    False(ObjectAoeRuleResolver.MatchesIgnoringEnabled(unrelated, dataId: 0, "ケツァクウァトル"),
        "MatchesIgnoringEnabled must not over-match when the name actually differs");
}

static void AddObjectAoeService_PreservesResolvedActionGeometry()
{
    var donut = AddObjectAoeService.CreateAoeZoneFromResolvedAction(
        new AoeResolver.AoeInfo(
            Radius: 20,
            CastType: 2,
            FromCaster: false,
            OmenId: 60,
            IncludeCasterHitbox: false),
        "ドーナツ");

    Equal("donut", donut.Shape, "omen donut should remain donut even when cast type is circle-like");
    Equal(8.0, donut.InnerRadiusM!.Value, "omen donut inner ratio should be preserved");

    var line = AddObjectAoeService.CreateAoeZoneFromResolvedAction(
        new AoeResolver.AoeInfo(
            Radius: 30,
            CastType: 4,
            FromCaster: true,
            OmenId: 0,
            IncludeCasterHitbox: true),
        "直線",
        sourceHitboxRadius: 4);

    Equal("rect", line.Shape, "line cast type should become rect zone");
    Equal(34.0, line.RadiusM, "resolved action should include source hitbox when supplied");
    Equal(AoeGeometryPolicy.DefaultLineHalfWidthM, (float)line.HalfWidthM!.Value,
        "line cast should keep readable default half width");
}

static void AddObjectAoeService_BuildsPerObjectStaticZones()
{
    var template = new StrategyAoeZone
    {
        Id = "line",
        Label = "直線",
        Shape = "line",
        Anchor = "each_matched_object",
        RadiusM = 40,
        HalfWidthM = 5,
        RotationOffsetDeg = 15,
        IncludeHitbox = true,
        DurationSec = 12,
        Color = "#FF6464",
        IsDanger = true,
    };

    var zone = AddObjectAoeService.BuildStaticAoeZone(
        template,
        new Vector3(110, 0, 100),
        new Vector3(100, 0, 100));

    Equal("static", zone.Anchor!, "object AoE should be materialized as static per-object zone");
    Equal(10.0, zone.X, "static object x relative to locked center");
    Equal(0.0, zone.Z, "static object z relative to locked center");
    Equal(180.0, zone.RotationDeg!.Value, "line object should point toward arena center");
    True(zone.IncludeHitbox, "static clone should preserve include hitbox");
    Equal(15.0, zone.RotationOffsetDeg!.Value, "static clone should preserve rotation offset");
    Equal(12.0, zone.DurationSec!.Value, "static clone should preserve duration");
}

static void KnownAoeGeometry_MapsSafeCallsToGeometry()
{
    var arena = new AutoAoeArenaConfig(20, "circle", null, null, new Vector3(100, 0, 100));
    var call = new AutoSafeCall(
        Gimmick: "cone",
        Callout: "直線回避",
        TtsText: "直線回避",
        FieldMarkerColor: "#FF6464",
        IsEstimate: false,
        FanDeg: 30);

    True(KnownAoeGeometry.TryCreate(call, radiusOverrideM: 42, arena, out var spec),
        "known cone with radius override should create geometry");
    Equal("cone", spec.Shape, "known cone shape");
    Equal(42f, spec.RadiusM, "known cone radius override");
    Equal(3, spec.CastType, "known cone cast type for minimap");
    Equal(30f, spec.FanDeg, "known cone fan");

    var half = new AutoSafeCall("half_plane", "半面", "半面", "#FF6464", false, 180);
    True(KnownAoeGeometry.TryCreate(half, radiusOverrideM: null, arena, out var halfSpec),
        "known half plane should create arena-sized floor geometry");
    Equal("half_plane", halfSpec.Shape, "half plane shape");
    Equal(40f, halfSpec.RadiusM, "half plane reaches across arena");
    Equal(20f, halfSpec.HalfWidthM, "half plane covers arena width");
}

static void AutoTelegraphService_PrefersActualAoeVisualShape()
{
    var staleSafeCall = new AutoSafeCall(
        Gimmick: "two_side_cleave",
        Callout: "old safe call",
        TtsText: "old",
        FieldMarkerColor: "#FF6464",
        IsEstimate: true,
        FanDeg: 180);
    var line = new AoeResolver.AoeInfo(
        Radius: 21f,
        CastType: 4,
        FromCaster: true);

    var visual = AutoTelegraphService.SelectVisualCall(line, staleSafeCall, "エソテリックダイアド");

    NotNull(visual, "visual call");
    Equal("cone", visual!.Gimmick, "line cast should use actual visual shape");
    Equal(30.0, visual.FanDeg, "line cast visual fan");
}

static void AutoTelegraphService_UsesTargetWorldSnapshot()
{
    var source = new Vector3(0, 0, 0);
    var snapshot = new Vector3(10, 0, 20);
    var liveTarget = new Vector3(99, 0, 99);
    var resolved = AutoTelegraphService.ResolveAoeWorldPosition(
        fromCaster: false,
        sourceWorld: source,
        targetId: 2001,
        targetWorld: snapshot,
        targetLookup: _ => liveTarget);

    Equal(snapshot, resolved!.Value, "target-centered AoE should prefer cast/action snapshot");
    Equal(source, AutoTelegraphService.ResolveAoeWorldPosition(true, source, 2001, snapshot, _ => liveTarget)!.Value,
        "caster-centered AoE should keep source");
}

static void AutoTelegraphService_SkipsPlayerActionUsedTelegraphs()
{
    var file = new TriggerFile
    {
        AutoSettings = new AutoSettings { ShowAutoTelegraphs = true },
    };
    var playerAction = new ActionUsedEvent(
        Timestamp: DateTimeOffset.UtcNow,
        SourceId: 2001,
        SourceName: "Self",
        ActionId: 0x1D3,
        ActionName: "ロイヤルアンソリティ",
        TargetId: 3001,
        IsAutoAttack: false,
        IsPlayer: true);

    True(AutoTelegraphService.ShouldSkipActionUsedTelegraph(
            file,
            playerAction,
            sourceIsFriendly: false,
            isRaidWide: false),
        "IsPlayer=true should suppress player skill AoE even when ObjectTable lookup missed the PC");

    False(AutoTelegraphService.ShouldSkipActionUsedTelegraph(
            file,
            playerAction with { IsPlayer = false, SourceName = "Boss" },
            sourceIsFriendly: false,
            isRaidWide: false),
        "non-player enemy action may continue to the AoE resolver");
}

static void AutoTelegraphService_SkipsActionUsedWithoutWorldPosition()
{
    False(
        AutoTelegraphService.ShouldDrawActionUsedAoeAtWorldPosition(null),
        "action_used with unresolved source/target position would render at minimap center and must be skipped");

    True(
        AutoTelegraphService.ShouldDrawActionUsedAoeAtWorldPosition(new Vector3(110, 0, 90)),
        "resolved object/source positions can be drawn");
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

static void ArenaProjection_KeepsDirectionalAoeLengthsBeyondArenaRadius()
{
    var cappedCircle = ArenaProjection.WorldRadiusToMap(40, 20, 80);
    var directional = ArenaProjection.WorldDirectionalLengthToMap(40, 20, 80);

    True(cappedCircle < 80, "circle radius stays capped to avoid filling the whole minimap");
    Equal(160f, directional, "directional length should keep the 40m world length on a 20m radius arena");
    True(ArenaProjection.IsDirectionalAoeCastType(4), "CastType 4 line should use directional length");
    True(ArenaProjection.IsDirectionalAoeCastType(13), "CastType 13 target cone should use directional length");
    False(ArenaProjection.IsDirectionalAoeCastType(2), "circle should keep radius conversion");
}

static void ArenaProjection_MapsRectRelativeCoordinates()
{
    var center = new Vector2(100, 100);
    var projected = ArenaProjection.ProjectRelativeToMap(
        center,
        mapRadius: 80,
        halfX: 20,
        halfZ: 10,
        relativeX: 10,
        relativeZ: 5);

    NearlyEqual(140, projected.X, "rect x projection");
    NearlyEqual(140, projected.Y, "rect z projection");
}

static void ArenaProjection_RectArena_WorldOriginMatchesRelativeDot()
{
    // 非正方アリーナ(halfX=20, halfZ=10)。同一ワールド点を AoE 原点(ProjectWorldToMap 軸独立版)と
    // プレイヤードット(ProjectRelativeToMap)の両経路で投影 → 同じ画面座標になるべき(P1-5)。
    var center = new Vector2(100, 100);
    var arenaCenter = new Vector3(5, 0, 3);
    var world = new Vector3(15, 0, 8); // arenaCenter から相対 (10, _, 5)

    var viaWorld = ArenaProjection.ProjectWorldToMap(center, 80, arenaCenter, 20f, 10f, world);
    var viaRelative = ArenaProjection.ProjectRelativeToMap(center, 80, 20f, 10f,
        world.X - arenaCenter.X, world.Z - arenaCenter.Z);

    NearlyEqual(viaRelative.X, viaWorld.X, "rect arena: world-origin X must match relative-dot X");
    NearlyEqual(viaRelative.Y, viaWorld.Y, "rect arena: world-origin Y must match relative-dot Y");
    // 短辺(halfZ=10)方向が長辺(halfX=20)と独立に正規化される証拠
    NearlyEqual(140f, viaWorld.X, "X: rel 10 / halfX 20 = 0.5 -> +40px");
    NearlyEqual(140f, viaWorld.Y, "Z: rel 5 / halfZ 10 = 0.5 -> +40px");
}

static void AoeGeometryPolicy_UsesReadableLineWidth()
{
    True(AoeGeometryPolicy.DefaultLineHalfWidthM >= 5.0f,
        "auto-generated line AoE should be wide enough to read on the minimap");
    Equal(AoeGeometryPolicy.DefaultLineHalfWidthM,
        AoeGeometryPolicy.ResolveLineHalfWidth(null),
        "missing line width should use readable default");
}

static void AoeGeometryPolicy_PointsObjectAoeTowardArenaCenter()
{
    NearlyEqual(180f, AoeGeometryPolicy.RotationDegTowardsArenaCenter(relativeX: 10f, relativeZ: 0f),
        "east object should point west toward center");
    NearlyEqual(-90f, AoeGeometryPolicy.RotationDegTowardsArenaCenter(relativeX: 0f, relativeZ: 10f),
        "south object should point north toward center");

    var ffxiv = AoeGeometryPolicy.FfxivRotationTowardsArenaCenter(
        objectWorld: new Vector3(110f, 0f, 100f),
        arenaCenter: new Vector3(100f, 0f, 100f));
    NearlyEqual(-MathF.PI / 2f, ffxiv, "east object ffxiv rotation should face west");
}

static void MarkerRelativePreset_AcceptsMarkerAliasA()
{
    var markers = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase)
    {
        ["marker_a"] = new(10, 0, 20),
    };

    True(MarkerRelativePreset.TryResolveMarker(markers, "A", out var pos), "A alias should resolve");
    Equal(new Vector3(10, 0, 20), pos, "resolved marker position");
}

static void AutoSettings_EnablesEarlyPredictionByDefault()
{
    var settings = new AutoSettings();

    Equal(AutoSettings.DefaultPredictAdvanceWarningSec, settings.PredictAdvanceWarningSec, "default prediction warning");
    True(settings.PredictAdvanceWarningSec >= 12.0, "default warning should be early enough to dodge");
    False(settings.ShowAllEnemyCasts, "unknown enemy casts should not draw guessed default circles");
}

static void ArenaRuler_ReachesFortyMeters()
{
    SequenceEqual(new[] { 5f, 10f, 15f, 20f, 25f, 30f, 35f, 40f },
        ArenaRulerPolicy.RadiiMeters,
        "ruler radii");
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

static void MinimapDisplayGrouping_LayersSimultaneousAutoAoes()
{
    True(MinimapDisplayGroupingPolicy.ShouldLayerAutoAoe(
            autoLuminaCastId: 0x67E5,
            aoeRadius: 21f,
            aoeCastType: 4,
            strategyPositionCount: 0,
            objectMarkerCount: 0,
            aoeZoneCount: 0),
        "plain Lumina auto AoE should be layerable");

    False(MinimapDisplayGroupingPolicy.ShouldLayerAutoAoe(
            autoLuminaCastId: null,
            aoeRadius: 21f,
            aoeCastType: 4,
            strategyPositionCount: 0,
            objectMarkerCount: 0,
            aoeZoneCount: 0),
        "manual/user layout entries should not be grouped as auto AoE");

    False(MinimapDisplayGroupingPolicy.ShouldLayerAutoAoe(
            autoLuminaCastId: 0x67E5,
            aoeRadius: 21f,
            aoeCastType: 4,
            strategyPositionCount: 0,
            objectMarkerCount: 0,
            aoeZoneCount: 1),
        "user-authored AoE zones should keep their own layout semantics");
}

static void UpcomingTimelinePolicy_KeepsRepeatedAutoAttacks()
{
    var predictions = new[]
    {
        new RecordingTimelinePrediction("auto_attack", 10.0, "Auto Attack", "0x0007", "Boss", null, 1, 0, 1, 1.0, 0.0),
        new RecordingTimelinePrediction("auto_attack", 12.4, "Auto Attack", "0x0007", "Boss", null, 1, 1, 1, 1.0, 0.0),
        new RecordingTimelinePrediction("auto_attack", 14.8, "Auto Attack", "0x0007", "Boss", null, 1, 2, 1, 1.0, 0.0),
        new RecordingTimelinePrediction("cast_start", 20.0, "Paradigma", "0x67BF", "Boss", null, 1, 0, 1, 1.0, 0.0),
    };

    var visible = UpcomingTimelinePolicy.FilterDisplayPredictions(predictions);

    Equal(4, visible.Count, "visible prediction count");
    Equal(3, visible.Count(p => p.EventType == "auto_attack"), "visible auto attack count");
    False(
        UpcomingTimelinePolicy.ShouldDeduplicateLabelPair("auto_attack", "auto_attack"),
        "auto attacks with the same display label are normal repeated events");
    True(
        UpcomingTimelinePolicy.ShouldDeduplicateLabelPair("cast_start", "cast_start"),
        "casts with the same display label can still be deduped");
}

static void UpcomingTimelinePolicy_KeepsSameLabelFromDifferentSources()
{
    False(
        UpcomingTimelinePolicy.ShouldDeduplicateDisplayItem(
            "cast_start", "cast_start",
            "Twin Cleave", "Twin Cleave",
            "Boss A", "Boss B"),
        "same label from different bosses must not be collapsed");

    True(
        UpcomingTimelinePolicy.ShouldDeduplicateDisplayItem(
            "cast_start", "cast_start",
            "Twin Cleave", "Twin Cleave",
            "Boss A", "Boss A"),
        "same source duplicate cast labels can still be collapsed");
}

static void UpcomingTimelinePolicy_DedupesCommonConfiguredRowsAgainstSourcedPredictions()
{
    True(
        UpcomingTimelinePolicy.ShouldDeduplicateDisplayItem(
            "note", "cast_start",
            "パラデイグマ", "パラデイグマ",
            null, "ゾディアーク"),
        "configured common row and sourced recording prediction for the same cast should not both appear");
    False(
        UpcomingTimelinePolicy.ShouldDeduplicateDisplayItem(
            "cast_start", "cast_start",
            "メテオロス・エイドロン", "メテオロス・エイドロン",
            "ベヒーモス", "ピュトン"),
        "same label from two concrete bosses must stay separated for multi-boss phases");
}

static void UpcomingTimelinePolicy_StripsSourcePrefixFromRowLabels()
{
    Equal("メテオロス・エイドロン",
        UpcomingTimelinePolicy.FormatRowLabel("ベヒーモス: メテオロス・エイドロン", "ベヒーモス"),
        "row label should not repeat source when grouped by boss");
    Equal("ベヒーモス",
        UpcomingTimelinePolicy.SourceGroupName("ベヒーモス"),
        "known source should become group title");
    Equal("共通",
        UpcomingTimelinePolicy.SourceGroupName(null),
        "missing source should become common group");

    True(
        UpcomingTimelinePolicy.ShouldDeduplicateDisplayItem(
            "cast_start", "cast_start",
            "ベヒーモス: メテオロス・エイドロン", "メテオロス・エイドロン",
            "ベヒーモス", "ベヒーモス"),
        "source-prefixed and plain labels from same boss should be treated as duplicates");
    True(
        UpcomingTimelinePolicy.ShouldDeduplicateDisplayItem(
            "cast_start", "cast_start",
            "ベヒーモス: メテオロス・エイドロン", "メテオロス・エイドロン",
            null, "ベヒーモス"),
        "source-prefixed labels should infer source when old rows lack source metadata");
    Equal("ベヒーモス",
        UpcomingTimelinePolicy.SourceGroupName(null, "ベヒーモス: メテオロス・エイドロン"),
        "old source-prefixed labels should still group under the boss name");
}

static void UpcomingTimelinePolicy_HidesCommonSourceHeader()
{
    False(
        UpcomingTimelinePolicy.ShouldDrawSourceGroupHeader(null, "軽減"),
        "common timeline notes should not show a shared/common group heading");
    False(
        UpcomingTimelinePolicy.ShouldDrawSourceGroupHeader(null, null),
        "empty source metadata should not show a shared/common group heading");
    True(
        UpcomingTimelinePolicy.ShouldDrawSourceGroupHeader("ベヒーモス", "メテオロス・エイドロン"),
        "known boss source should still show a source heading");
    True(
        UpcomingTimelinePolicy.ShouldDrawSourceGroupHeader(null, "ベヒーモス: メテオロス・エイドロン"),
        "source-prefixed legacy labels should still show the inferred boss heading");
}

static void UpcomingTimelinePolicy_DropsDueAndPastItems()
{
    True(
        UpcomingTimelinePolicy.ShouldDisplayUpcomingItem(10.01, 10.0),
        "future items should remain visible");
    False(
        UpcomingTimelinePolicy.ShouldDisplayUpcomingItem(10.0, 10.0),
        "items at zero seconds should disappear");
    False(
        UpcomingTimelinePolicy.ShouldDisplayUpcomingItem(9.99, 10.0),
        "past items should disappear");
}

// --- プルセグメント分離（前半/後半）テスト用ヘルパー ---
static AggregatedEvent SegCastEv(string id, double firstSeen)
    => new AggregatedEvent(new EventKey("cast_start", id, "name_" + id, null, null), 1, firstSeen);

static AggregatedEvents SegAgg(params AggregatedEvent[] evs)
    => new AggregatedEvents(evs, evs.Length, evs.Length, evs.Length);

static BranchGroup SegGrp(string firstCastId, AggregatedEvents agg)
    => new BranchGroup
    {
        FirstCastId = firstCastId,
        FirstCastName = firstCastId,
        FileCount = 2,
        FilePaths = new[] { "a.jsonl", "b.jsonl" },
        Confidence = 0.5,
        AggregatedEvents = agg,
    };

static BranchDetectionResult SegResult(params BranchGroup[] groups)
    => new BranchDetectionResult { TotalFilesScanned = groups.Length * 2, Groups = groups };

static void RecordingSegmentBuilder_SeparatesPullsAndCommon()
{
    var front = SegAgg(SegCastEv("0x28D1", 10.2), SegCastEv("0x28CE", 25.0));   // 前半: マジックチャージ/めらめらファイガ
    var back = SegAgg(SegCastEv("0x28FA", 7.1), SegCastEv("0x2911", 13.3));     // 後半: 心ない天使/アルテマ
    var combined = SegAgg(
        SegCastEv("0x28D1", 10.2), SegCastEv("0x28CE", 25.0),
        SegCastEv("0x28FA", 7.1), SegCastEv("0x2911", 13.3));

    var seg = RecordingSegmentBuilder.BuildSegmentedAggregate(SegResult(SegGrp("0x28D1", front), SegGrp("0x28FA", back)), combined);
    Equal(2, seg.Segments.Count, "two segments detected");
    Equal(0, seg.CommonAgg.Events.Count, "no shared cast_id → empty CommonAgg (safe blank before resolve)");
    True(seg.Segments[0].CastIds.Contains("0x28D1") && seg.Segments[0].CastIds.Contains("0x28CE"), "front segment cast ids");
    False(seg.Segments[0].CastIds.Contains("0x28FA"), "front segment excludes back-only cast");

    // 共通 cast_id が 1 件ある場合：CommonAgg にその id のみ、最小 first-seen で入る。
    var frontC = SegAgg(SegCastEv("0x28D1", 10.2), SegCastEv("0x1000", 30.0));
    var backC = SegAgg(SegCastEv("0x28FA", 7.1), SegCastEv("0x1000", 15.0));
    var segC = RecordingSegmentBuilder.BuildSegmentedAggregate(SegResult(SegGrp("0x28D1", frontC), SegGrp("0x28FA", backC)), combined);
    Equal(1, segC.CommonAgg.Events.Count, "one shared cast in CommonAgg");
    Equal("0x1000", segC.CommonAgg.Events[0].Key.Id, "common cast id");
    Equal(15.0, segC.CommonAgg.Events[0].FirstSeenSeconds, "common uses earliest first-seen across segments");

    // Groups < 2（単群）→ Segments 空・Combined 透過（他コンテンツ回帰なし）。
    var segMono = RecordingSegmentBuilder.BuildSegmentedAggregate(SegResult(SegGrp("0x28D1", front)), combined);
    Equal(0, segMono.Segments.Count, "single group → no segments");
    True(ReferenceEquals(segMono.Combined, combined), "combined passthrough for single group");
    var segNull = RecordingSegmentBuilder.BuildSegmentedAggregate(null, combined);
    Equal(0, segNull.Segments.Count, "null branch result → no segments (fallback)");
}

static void UpcomingTimelinePolicy_TryResolveSegment_ResolvesByOpeningAndExclusive()
{
    var front = SegAgg(SegCastEv("0x28D1", 10.2), SegCastEv("0x28CE", 25.0));
    var back = SegAgg(SegCastEv("0x28FA", 7.1), SegCastEv("0x2911", 13.3));
    var combined = SegAgg(SegCastEv("0x28D1", 10.2), SegCastEv("0x28FA", 7.1));
    var segments = RecordingSegmentBuilder.BuildSegmentedAggregate(SegResult(SegGrp("0x28D1", front), SegGrp("0x28FA", back)), combined).Segments;
    var noParty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    True(UpcomingTimelinePolicy.TryResolveSegment(segments, 0x28D1u, noParty, "ケフカ", out var r1), "front opening cast resolves");
    Equal("0x28D1", r1, "resolved to front");
    True(UpcomingTimelinePolicy.TryResolveSegment(segments, 0x28FAu, noParty, "ケフカ", out var r2), "back opening cast resolves");
    Equal("0x28FA", r2, "resolved to back");

    // 排他フォールバック：後半固有 cast（アルテマ）でも後半に確定。
    True(UpcomingTimelinePolicy.TryResolveSegment(segments, 0x2911u, noParty, "ケフカ", out var r3), "back-only cast resolves via exclusive fallback");
    Equal("0x28FA", r3, "exclusive fallback → back");

    // PC（パーティ）の詠唱は無視。
    var party = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PlayerName" };
    False(UpcomingTimelinePolicy.TryResolveSegment(segments, 0x28D1u, party, "PlayerName", out _), "party member cast is ignored");

    // 未知 cast は確定しない。
    False(UpcomingTimelinePolicy.TryResolveSegment(segments, 0x9999u, noParty, "ケフカ", out _), "unknown cast does not resolve");
    // セグメント未分離なら確定しない。
    False(UpcomingTimelinePolicy.TryResolveSegment(System.Array.Empty<RecordingSegment>(), 0x28D1u, noParty, "ケフカ", out _), "no segments → no resolve");
}

static void UpcomingTimelinePolicy_SelectActiveAgg_PicksConfirmedSegment()
{
    var front = SegAgg(SegCastEv("0x28D1", 10.2), SegCastEv("0x28CE", 25.0));
    var back = SegAgg(SegCastEv("0x28FA", 7.1), SegCastEv("0x2911", 13.3));
    var combined = SegAgg(SegCastEv("0x28D1", 10.2), SegCastEv("0x28FA", 7.1), SegCastEv("0x2911", 13.3));
    var seg = RecordingSegmentBuilder.BuildSegmentedAggregate(SegResult(SegGrp("0x28D1", front), SegGrp("0x28FA", back)), combined);

    // Segments < 2 → Combined（後方互換）。
    var segMono = RecordingSegmentBuilder.BuildSegmentedAggregate(SegResult(SegGrp("0x28D1", front)), combined);
    True(ReferenceEquals(UpcomingTimelinePolicy.SelectActiveAgg(segMono, null), combined), "mono → combined");

    // 未確定 → CommonAgg（Sigma は空＝安全縮退）。
    Equal(0, UpcomingTimelinePolicy.SelectActiveAgg(seg, null).Events.Count, "unconfirmed → empty common (no wrong-half leak)");

    // 前半確定 → 前半のみ、後半技（心ない天使/アルテマ）を含まない。
    var frontAgg = UpcomingTimelinePolicy.SelectActiveAgg(seg, "0x28D1");
    True(frontAgg.Events.Any(e => e.Key.Id == "0x28D1"), "front-confirmed has front cast");
    False(frontAgg.Events.Any(e => e.Key.Id == "0x28FA"), "front-confirmed excludes 心ない天使");
    False(frontAgg.Events.Any(e => e.Key.Id == "0x2911"), "front-confirmed excludes アルテマ");

    // 後半確定 → 後半のみ、前半技を含まない。
    var backAgg = UpcomingTimelinePolicy.SelectActiveAgg(seg, "0x28FA");
    True(backAgg.Events.Any(e => e.Key.Id == "0x2911"), "back-confirmed has アルテマ");
    False(backAgg.Events.Any(e => e.Key.Id == "0x28D1"), "back-confirmed excludes マジックチャージ");
}

static void UpcomingTimelinePolicy_ResolveEmptyStateMessage_ContextAware()
{
    // 分離済み・未確定（開幕の最初のボス技待ち）→「録画しろ」ではなく「判定中」。
    Equal(UpcomingTimelinePolicy.EmptyWaitingForBossCast,
        UpcomingTimelinePolicy.ResolveEmptyStateMessage(2, segmentResolved: false, hasRecordedEvents: true),
        "segments detected but unconfirmed → waiting-for-boss-cast message");

    // 確定済みだが直近窓に予測なし（録画はある）→ 中立文言（誤誘導しない）。
    Equal(UpcomingTimelinePolicy.EmptyNoUpcoming,
        UpcomingTimelinePolicy.ResolveEmptyStateMessage(2, segmentResolved: true, hasRecordedEvents: true),
        "resolved but nothing upcoming → neutral message");

    // 単群コンテンツで録画はあるが直近なし → 中立文言。
    Equal(UpcomingTimelinePolicy.EmptyNoUpcoming,
        UpcomingTimelinePolicy.ResolveEmptyStateMessage(0, segmentResolved: false, hasRecordedEvents: true),
        "single-segment with recordings but nothing upcoming → neutral message");

    // 本当に録画ゼロ → 従来の「録画してから1戦」。
    Equal(UpcomingTimelinePolicy.EmptyNoRecordings,
        UpcomingTimelinePolicy.ResolveEmptyStateMessage(0, segmentResolved: false, hasRecordedEvents: false),
        "truly no recordings → record-first message");
}

static void UpcomingTimelinePolicy_DropsFarFutureBeyondHorizon()
{
    // 未来側の表示上限。フェーズ注釈の無いタイムラインで、後半技が遠い相対秒の予測として
    // 直近バーに混ざるのを防ぐ（前半中に後半項目が出る症状の緩和）。
    True(
        UpcomingTimelinePolicy.ShouldDisplayUpcomingItem(10.0 + 20.0, 10.0),
        "near-future items within the horizon should remain visible");
    True(
        UpcomingTimelinePolicy.ShouldDisplayUpcomingItem(10.0 + 45.0, 10.0),
        "items exactly at the horizon should remain visible");
    False(
        UpcomingTimelinePolicy.ShouldDisplayUpcomingItem(10.0 + 200.0, 10.0),
        "far-future items well beyond the horizon should be hidden");
    False(
        UpcomingTimelinePolicy.ShouldDisplayUpcomingItem(10.0 + 45.01, 10.0),
        "items just past the horizon should be hidden");
}

static void Minimap_HidesLiveDotsForUserAuthoredLayouts()
{
    False(MinimapLiveLayerPolicy.ShouldDrawLivePositionLayer("user_layout", 8, 0, 0),
        "user layout should not mix live player/boss dots");
    False(MinimapLiveLayerPolicy.ShouldDrawLivePositionLayer("outer_ring", 8, 0, 0),
        "strategy positions should suppress live player/boss dots");
    False(MinimapLiveLayerPolicy.ShouldDrawLivePositionLayer("outer_ring", 0, 1, 0),
        "user object markers should suppress live player/boss dots");
    False(MinimapLiveLayerPolicy.ShouldDrawLivePositionLayer("outer_ring", 0, 0, 1),
        "user AoE zones should suppress live player/boss dots");
    True(MinimapLiveLayerPolicy.ShouldDrawLivePositionLayer("outer_ring", 0, 0, 0),
        "plain auto minimap should still show live context");
}

static void Minimap_KeepsSelfDotForUserAuthoredLayouts()
{
    Equal(MinimapLiveLayerMode.SelfOnly,
        MinimapLiveLayerPolicy.GetLiveLayerMode("user_layout", 8, 0, 0),
        "user layout should still draw self position");
    Equal(MinimapLiveLayerMode.SelfOnly,
        MinimapLiveLayerPolicy.GetLiveLayerMode("outer_ring", 8, 0, 0),
        "strategy positions should still draw self position");
    Equal(MinimapLiveLayerMode.Full,
        MinimapLiveLayerPolicy.GetLiveLayerMode("outer_ring", 0, 0, 0),
        "plain auto minimap should draw full live context");
}

static void MechanicArenaDefaultsPolicy_AppliesProfileArenaToMechanic()
{
    var profile = new StrategyProfile
    {
        ArenaShape = "square",
        ArenaRadius = 20,
        ArenaWidth = 40,
        ArenaDepth = 40,
        ArenaCenterX = 99.6,
        ArenaCenterZ = 99.6,
    };
    var mechanic = new MechanicStrategy
    {
        ArenaShape = "circle",
        ArenaRadius = 22,
        ArenaWidth = 44,
        ArenaDepth = 44,
        ArenaCenterX = 101.4,
        ArenaCenterZ = 107.0,
    };

    var changed = MechanicArenaDefaultsPolicy.ApplyProfileArena(profile, mechanic);

    True(changed, "applying different profile arena should report changed");
    Equal("square", mechanic.ArenaShape!, "arena shape");
    Equal(20.0, mechanic.ArenaRadius!.Value, "arena radius");
    Equal(40.0, mechanic.ArenaWidth!.Value, "arena width");
    Equal(40.0, mechanic.ArenaDepth!.Value, "arena depth");
    Equal(99.6, mechanic.ArenaCenterX!.Value, "arena center x");
    Equal(99.6, mechanic.ArenaCenterZ!.Value, "arena center z");
}

static void MechanicArenaDefaultsPolicy_AppliesProfileArenaToAllMechanics()
{
    var profile = new StrategyProfile
    {
        ArenaShape = "rect",
        ArenaRadius = 20,
        ArenaWidth = 42,
        ArenaDepth = 38,
        ArenaCenterX = 100.5,
        ArenaCenterZ = 101.5,
        Mechanics = new List<MechanicStrategy>
        {
            new()
            {
                Id = "m1",
                ArenaShape = "circle",
                ArenaRadius = 18,
                ArenaCenterX = 0,
                ArenaCenterZ = 0,
            },
            new()
            {
                Id = "m2",
                ArenaShape = "square",
                ArenaWidth = 40,
                ArenaDepth = 40,
                ArenaCenterX = 99,
                ArenaCenterZ = 99,
            },
        },
    };

    var changedCount = MechanicArenaDefaultsPolicy.ApplyProfileArenaToAll(profile, profile.Mechanics);

    Equal(2, changedCount, "changed mechanics");
    foreach (var mechanic in profile.Mechanics)
    {
        Equal("rect", mechanic.ArenaShape!, $"{mechanic.Id} arena shape");
        Equal(20.0, mechanic.ArenaRadius!.Value, $"{mechanic.Id} arena radius");
        Equal(42.0, mechanic.ArenaWidth!.Value, $"{mechanic.Id} arena width");
        Equal(38.0, mechanic.ArenaDepth!.Value, $"{mechanic.Id} arena depth");
        Equal(100.5, mechanic.ArenaCenterX!.Value, $"{mechanic.Id} arena center x");
        Equal(101.5, mechanic.ArenaCenterZ!.Value, $"{mechanic.Id} arena center z");
    }
}

static void ArenaCenterDragPolicy_MovesCenterOnly()
{
    var profile = new StrategyProfile
    {
        ArenaCenterX = 100,
        ArenaCenterZ = 200,
        SpreadPositions = new List<StrategyPosition>
        {
            new() { Slot = "D1", X = -8, Z = -4 },
        },
    };
    var mechanic = new MechanicStrategy
    {
        Id = "m1",
        ArenaCenterX = 101,
        ArenaCenterZ = 107,
        ObjectMarkers = new List<StrategyObjectMarker>
        {
            new() { Id = "boss", X = 3, Z = 4 },
        },
        AoeZones = new List<StrategyAoeZone>
        {
            new() { Id = "line", X = 1, Z = 2 },
        },
    };

    ArenaCenterDragPolicy.ApplyMechanicCenterDelta(profile, mechanic, deltaX: 2.5, deltaZ: -1.5);

    Equal(103.5, mechanic.ArenaCenterX!.Value, "mechanic center x");
    Equal(105.5, mechanic.ArenaCenterZ!.Value, "mechanic center z");
    Equal(-8.0, profile.SpreadPositions[0].X, "spread x should stay relative");
    Equal(-4.0, profile.SpreadPositions[0].Z, "spread z should stay relative");
    Equal(3.0, mechanic.ObjectMarkers[0].X, "object marker x should stay relative");
    Equal(4.0, mechanic.ObjectMarkers[0].Z, "object marker z should stay relative");
    Equal(1.0, mechanic.AoeZones[0].X, "aoe x should stay relative");
    Equal(2.0, mechanic.AoeZones[0].Z, "aoe z should stay relative");
}

static void ArenaCenterDragPolicy_MovesProfileCenterOnly()
{
    var profile = new StrategyProfile
    {
        ArenaCenterX = 100,
        ArenaCenterZ = 200,
        SpreadPositions = new List<StrategyPosition>
        {
            new() { Slot = "D1", X = -8, Z = -4 },
        },
    };

    ArenaCenterDragPolicy.ApplyProfileCenterDelta(profile, deltaX: 2.5, deltaZ: -1.5);

    Equal(102.5, profile.ArenaCenterX!.Value, "profile center x");
    Equal(198.5, profile.ArenaCenterZ!.Value, "profile center z");
    Equal(-8.0, profile.SpreadPositions[0].X, "spread x should stay relative");
    Equal(-4.0, profile.SpreadPositions[0].Z, "spread z should stay relative");
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

static void WorldShapeRenderer_RecommendSegments_ScalesWithRadius()
{
    // 小半径：最低 32 にクランプ
    var s5 = WorldShapeRenderer.RecommendSegments(5f);
    True(s5 >= 24 && s5 <= 40, $"r=5 should stay cheap: got {s5}");
    // 中半径：視認性を保ちつつ 1 frame 予算を圧迫しない
    var s15 = WorldShapeRenderer.RecommendSegments(15f);
    True(s15 >= 36 && s15 <= 56, $"r=15 should be 36~56: got {s15}");
    // 大半径：巨大AoEでも 64 以下に抑える
    var s30 = WorldShapeRenderer.RecommendSegments(30f);
    True(s30 <= 64, $"r=30 should be <=64 for frame budget: got {s30}");
    // 超大半径：64 にクランプ
    var s100 = WorldShapeRenderer.RecommendSegments(100f);
    Equal(64, s100, "r=100 clamps to 64");
    // 負・0：最低値
    Equal(24, WorldShapeRenderer.RecommendSegments(0f), "r=0 → 24");
    var sNeg = WorldShapeRenderer.RecommendSegments(-5f);
    Equal(24, sNeg, "r=-5 (invalid) → min segment count");
}

static void TimelineBranch_JsonRoundtrip_PreservesFields()
{
    var branch = new TimelineBranch
    {
        Id = "pattern_aeroja",
        Label = "パターン1（エアロジャ系）",
        GroupId = "p1",
        Condition = new BranchCondition
        {
            Type = "first_cast",
            CastId = "0x7F3A",
            CastName = "エアロジャ",
            WindowSec = 25.0,
        },
    };
    var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
    var json = System.Text.Json.JsonSerializer.Serialize(branch, opts);
    True(json.Contains("\"id\":\"pattern_aeroja\""), $"id field: {json}");
    True(json.Contains("\"group_id\":\"p1\""), $"group_id field: {json}");
    True(json.Contains("\"type\":\"first_cast\""), $"type field: {json}");
    True(json.Contains("\"cast_id\":\"0x7F3A\""), $"cast_id field: {json}");
    True(json.Contains("\"window_sec\":25"), $"window_sec field: {json}");
    var rt = System.Text.Json.JsonSerializer.Deserialize<TimelineBranch>(json, opts);
    NotNull(rt, "deserialize");
    Equal("pattern_aeroja", rt!.Id, "rt id");
    Equal("p1", rt.GroupId, "rt group");
    Equal("first_cast", rt.Condition.Type, "rt condition type");
    Equal("0x7F3A", rt.Condition.CastId, "rt condition castId");
    Equal(25.0, rt.Condition.WindowSec, "rt window sec");
}

static void BranchObserver_ScopesRejectionToBranchGroup()
{
    var branches = new[]
    {
        new TimelineBranch { Id = "p1_a", GroupId = "p1" },
        new TimelineBranch { Id = "p1_b", GroupId = "p1" },
        new TimelineBranch { Id = "p2_a", GroupId = "p2" },
        new TimelineBranch { Id = "p2_b", GroupId = "p2" },
    };
    var statuses = BranchObserverService.ResolveMatchedBranchStatuses(branches, "p1_a");

    Equal(BranchStatus.Active, statuses["p1_a"], "matched branch active");
    Equal(BranchStatus.Rejected, statuses["p1_b"], "same group rejected");
    Equal(BranchStatus.Pending, statuses["p2_a"], "other group remains pending");
    Equal(BranchStatus.Pending, statuses["p2_b"], "other group remains pending");
}

static void StrategyPlanResolver_FindMechanicForPrediction_FiltersByBranch()
{
    var file = new TriggerFile
    {
        ActiveStrategyProfileId = "default",
        StrategyProfiles = new List<StrategyProfile>
        {
            new()
            {
                Id = "default",
                Enabled = true,
                Mechanics = new List<MechanicStrategy>
                {
                    new()
                    {
                        Id = "branch_b",
                        Label = "Rejected",
                        Enabled = true,
                        BranchId = "b",
                        Time = 10,
                        AttachedTo = new MatchCondition { CastId = "0x1234" },
                    },
                    new()
                    {
                        Id = "branch_a",
                        Label = "Active",
                        Enabled = true,
                        BranchId = "a",
                        Time = 10,
                        AttachedTo = new MatchCondition { CastId = "0x1234" },
                    },
                },
            },
        },
    };
    var prediction = new RecordingPrediction(
        RelativeSeconds: 10,
        Label: "Shared Cast",
        CastId: "0x1234",
        Source: "Boss",
        ObservedCount: 1,
        OccurrenceIndex: 0,
        OccurrenceSeenCount: 1,
        Confidence: 1,
        TimeJitterSeconds: 0,
        EarliestObservedSeconds: 10,
        LatestObservedSeconds: 10);

    var selected = StrategyPlanResolver.FindMechanicForPrediction(file, prediction, branchId => branchId is null or "a");

    Equal("branch_a", selected.Mechanic?.Id, "rejected branch mechanic should not supply prediction actions");
}

static void StrategyPlanResolver_FindMechanicForPrediction_SignalsBranchRejected()
{
    var file = new TriggerFile
    {
        ActiveStrategyProfileId = "default",
        StrategyProfiles = new List<StrategyProfile>
        {
            new()
            {
                Id = "default",
                Enabled = true,
                Mechanics = new List<MechanicStrategy>
                {
                    // 攻撃B 専用キャスト（分岐 b にのみ紐づく）
                    new()
                    {
                        Id = "branch_b_only",
                        Label = "AttackB",
                        Enabled = true,
                        BranchId = "b",
                        Time = 20,
                        AttachedTo = new MatchCondition { CastId = "0xB" },
                    },
                },
            },
        },
    };

    // 分岐 b が Rejected（active は a のみ）のとき、攻撃B キャストは
    // Mechanic=null かつ BranchRejected=true になる（= 抑制すべき、デフォルト警告を出さない）。
    var rejected = StrategyPlanResolver.FindMechanicForPrediction(
        file,
        new RecordingPrediction(20, "AttackB", "0xB", "Boss", 1, 0, 1, 1.0, 0.0, 20, 20),
        branchId => branchId is null or "a");
    Null(rejected.Mechanic, "棄却分岐のキャストは Mechanic=null");
    True(rejected.BranchRejected, "棄却分岐は BranchRejected=true（明示抑制すべき）");

    // mechanic がそもそも未定義なキャストは BranchRejected=false（デフォルト警告を出してよい）。
    var undefined = StrategyPlanResolver.FindMechanicForPrediction(
        file,
        new RecordingPrediction(30, "Unknown", "0xZZZ", "Boss", 1, 0, 1, 1.0, 0.0, 30, 30),
        branchId => branchId is null or "a");
    Null(undefined.Mechanic, "未定義キャストは Mechanic=null");
    False(undefined.BranchRejected, "未定義キャストは BranchRejected=false（デフォルト警告は許可）");
}

static void UpcomingTimelinePolicy_FiltersBranchRejectedPredictions()
{
    var file = new TriggerFile
    {
        ActiveStrategyProfileId = "default",
        StrategyProfiles = new List<StrategyProfile>
        {
            new()
            {
                Id = "default",
                Enabled = true,
                Mechanics = new List<MechanicStrategy>
                {
                    new() { Id = "a1", Enabled = true, BranchId = "branch_a",
                            AttachedTo = new MatchCondition { CastId = "0xAAAA" } },
                    new() { Id = "b1", Enabled = true, BranchId = "branch_b",
                            AttachedTo = new MatchCondition { CastId = "0xBBBB" } },
                    new() { Id = "common", Enabled = true, BranchId = null,
                            AttachedTo = new MatchCondition { CastId = "0xCCCC" } },
                },
            },
        },
    };
    var predictions = new List<RecordingTimelinePrediction>
    {
        new("cast_start", 10, "AttackA", "0xAAAA", "Boss", null, 5, 0, 5, 1.0, 0.2),
        new("cast_start", 15, "AttackB", "0xBBBB", "Boss", null, 5, 0, 5, 1.0, 0.3),
        new("cast_start", 20, "CommonAttack", "0xCCCC", "Boss", null, 5, 0, 5, 1.0, 0.1),
        new("cast_start", 25, "UnknownCast", "0xDDDD", "Boss", null, 3, 0, 3, 0.6, 0.5),
    };

    // branch_a が Active、branch_b が Rejected
    var filtered = UpcomingTimelinePolicy.FilterBranchRejectedPredictions(
        predictions, file, id => id is null or "branch_a");

    Equal(3, filtered.Count, "branch_b 専用キャストのみ除外され、他は残る");
    True(filtered.Any(p => p.Id == "0xAAAA"), "branch_a のキャストは残る");
    False(filtered.Any(p => p.Id == "0xBBBB"), "branch_b のキャストは除外される");
    True(filtered.Any(p => p.Id == "0xCCCC"), "共通キャストは残る");
    True(filtered.Any(p => p.Id == "0xDDDD"), "mechanic 未定義のキャストは残る");
}

static void UpcomingTimelinePolicy_KeepsPredictionWhenAnyBranchActive()
{
    var file = new TriggerFile
    {
        ActiveStrategyProfileId = "default",
        StrategyProfiles = new List<StrategyProfile>
        {
            new()
            {
                Id = "default",
                Enabled = true,
                Mechanics = new List<MechanicStrategy>
                {
                    // 同一 CastId が分岐A(Active)にも分岐B(Rejected)にも紐づく
                    new() { Id = "a1", Enabled = true, BranchId = "branch_a",
                            AttachedTo = new MatchCondition { CastId = "0x9999" } },
                    new() { Id = "b1", Enabled = true, BranchId = "branch_b",
                            AttachedTo = new MatchCondition { CastId = "0x9999" } },
                },
            },
        },
    };
    var predictions = new List<RecordingTimelinePrediction>
    {
        new("cast_start", 30, "SharedCast", "0x9999", "Boss", null, 5, 0, 5, 1.0, 0.2),
    };

    var filtered = UpcomingTimelinePolicy.FilterBranchRejectedPredictions(
        predictions, file, id => id is null or "branch_a");

    Equal(1, filtered.Count, "少なくとも1つの分岐が active なキャストは残す");
}

static void TriggerFile_JsonRoundtrip_PreservesRaidWideMarkers()
{
    var file = new TriggerFile
    {
        Zone = "月の底",
        RaidWideMarkers = new List<RaidWideMarker>
        {
            new()
            {
                Id = "0x6C60",
                Name = "コキュートス",
                Source = "manual",
                Callout = "全体: コキュートス",
                Tts = "コキュートス",
            },
        },
    };
    var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
    var json = System.Text.Json.JsonSerializer.Serialize(file, opts);

    True(json.Contains("\"raid_wide_markers\""), $"raid_wide_markers field: {json}");
    True(json.Contains("\"id\":\"0x6C60\""), $"raid-wide id field: {json}");

    var rt = System.Text.Json.JsonSerializer.Deserialize<TriggerFile>(json, opts);
    NotNull(rt, "deserialize trigger file");
    Equal(1, rt!.RaidWideMarkers.Count, "raid-wide marker count");
    Equal("0x6C60", rt.RaidWideMarkers[0].Id, "raid-wide marker id");
    Equal("コキュートス", rt.RaidWideMarkers[0].Name, "raid-wide marker name");
}

static void TriggerFile_JsonRoundtrip_PreservesObjectAoeRules()
{
    var file = new TriggerFile
    {
        Zone = "月の底",
        ActiveStrategyProfileId = "default",
        StrategyProfiles = new List<StrategyProfile>
        {
            new()
            {
                Id = "default",
                Name = "Default",
                ObjectAoeRules = new List<ObjectAoeRule>
                {
                    new()
                    {
                        Id = "paradeigma-keraunos",
                        ObjectName = "ケラノウス",
                        NameMatch = "contains",
                        DataId = 14387,
                        Label = "直線",
                        Source = "recording",
                        Shape = "line",
                        RadiusM = 40,
                        HalfWidthM = 5,
                        DurationSec = 12,
                        Color = "#FF6464",
                    },
                },
            },
        },
    };
    var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
    var json = System.Text.Json.JsonSerializer.Serialize(file, opts);

    True(json.Contains("\"object_aoe_rules\"", StringComparison.Ordinal), $"object_aoe_rules field: {json}");
    True(json.Contains("\"object_name\"", StringComparison.Ordinal), $"object_name field: {json}");

    var rt = System.Text.Json.JsonSerializer.Deserialize<TriggerFile>(json, opts);
    NotNull(rt, "deserialize trigger file");
    Equal(1, rt!.StrategyProfiles[0].ObjectAoeRules.Count, "object AoE rule count");
    var rule = rt.StrategyProfiles[0].ObjectAoeRules[0];
    Equal("ケラノウス", rule.ObjectName!, "object AoE object name");
    Equal("line", rule.Shape, "object AoE shape");
    Equal(5.0, rule.HalfWidthM!.Value, "object AoE half width");
    Equal(12.0, rule.DurationSec!.Value, "object AoE duration");
}

static void TriggerSchema_ExposesAdvancedAoeFields()
{
    var schemaPath = FindRepoFile("trigger-schema.json");
    var schema = File.ReadAllText(schemaPath);

    True(schema.Contains("\"actor_matcher\"", StringComparison.Ordinal), "schema should expose actor_matcher");
    True(schema.Contains("\"state_filter\"", StringComparison.Ordinal), "schema should expose state_filter");
    True(schema.Contains("\"rotation_source\"", StringComparison.Ordinal), "schema should expose rotation_source");
    True(schema.Contains("\"live_floor_paint\"", StringComparison.Ordinal), "schema should expose live_floor_paint");
    True(schema.Contains("\"duration_sec\"", StringComparison.Ordinal), "schema should expose duration_sec");
    True(schema.Contains("\"suppress_auto_aoe\"", StringComparison.Ordinal), "schema should expose suppress_auto_aoe");
    True(schema.Contains("\"include_hitbox\"", StringComparison.Ordinal), "schema should expose include_hitbox");
    True(schema.Contains("\"rotation_offset_deg\"", StringComparison.Ordinal), "schema should expose rotation_offset_deg");
    True(schema.Contains("\"aoe_sequence\"", StringComparison.Ordinal), "schema should expose aoe_sequence");
    True(schema.Contains("\"show_floor_paint\"", StringComparison.Ordinal), "schema should expose show_floor_paint");
    True(schema.Contains("\"group_id\"", StringComparison.Ordinal), "schema should expose branch group_id");
    True(schema.Contains("\"object_aoe_rules\"", StringComparison.Ordinal), "schema should expose object AoE rules");
    True(SchemaDefinitionHasProperty(schema, "object_aoe_rule", "object_name"),
        "object_aoe_rule schema should expose object_name");
    True(SchemaDefinitionHasProperty(schema, "object_aoe_rule", "duration_sec"),
        "object_aoe_rule schema should expose duration_sec");
    True(SchemaDefinitionHasProperty(schema, "mechanic_strategy", "branch_id"),
        "mechanic_strategy schema should expose branch_id");
    True(schema.Contains("\"cross\"", StringComparison.Ordinal), "schema should expose cross shape");
    True(schema.Contains("\"donut_cone\"", StringComparison.Ordinal), "schema should expose donut_cone shape");
    True(schema.Contains("\"half_plane\"", StringComparison.Ordinal), "schema should expose half_plane shape");
    True(SchemaPropertyHasDefaultBool(schema, "auto_settings", "show_auto_telegraphs", expected: true),
        "schema show_auto_telegraphs default should match AutoSettings default");
}

static bool SchemaPropertyHasDefaultBool(string schema, string parentProperty, string propertyName, bool expected)
{
    using var doc = System.Text.Json.JsonDocument.Parse(schema);
    var root = doc.RootElement;
    if (!root.TryGetProperty("properties", out var props)) return false;
    if (!props.TryGetProperty(parentProperty, out var parent)) return false;
    if (!parent.TryGetProperty("properties", out var parentProps)) return false;
    if (!parentProps.TryGetProperty(propertyName, out var prop)) return false;
    if (!prop.TryGetProperty("default", out var def)) return false;
    return def.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False &&
           def.GetBoolean() == expected;
}

static bool SchemaDefinitionHasProperty(string schema, string definitionName, string propertyName)
{
    using var doc = System.Text.Json.JsonDocument.Parse(schema);
    var root = doc.RootElement;
    if (!root.TryGetProperty("definitions", out var defs)) return false;
    if (!defs.TryGetProperty(definitionName, out var def)) return false;
    if (!def.TryGetProperty("properties", out var props)) return false;
    return props.TryGetProperty(propertyName, out _);
}

static string FindRepoFile(string fileName)
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        var path = Path.Combine(dir.FullName, fileName);
        if (File.Exists(path))
        {
            return path;
        }
        dir = dir.Parent;
    }

    throw new FileNotFoundException($"Could not find {fileName} from {Directory.GetCurrentDirectory()}");
}

/// <summary>テスト用：先頭が指定 cast の jsonl ファイルを作成。</summary>
static string CreateRecordingFile(string dir, string fileName, string firstCastId, string firstCastName, double firstCastTime = 5.0)
{
    var path = Path.Combine(dir, fileName);
    File.WriteAllLines(path, new[]
    {
        """{"meta":true,"zone":"TestZone","start_time":"2026-05-10T00:00:00.000Z","plugin_version":"0.1.0","party":[{"name":"Self","job":"PLD","role":"Tank"}]}""",
        $$"""{"time":{{firstCastTime.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"type":"cast_start","source":"Boss","cast_id":"{{firstCastId}}","cast_name":"{{firstCastName}}","cast_time":4.0}""",
        """{"time":15.0,"type":"cast_start","source":"Boss","cast_id":"0xCAFE","cast_name":"Common","cast_time":3.0}""",
    });
    return path;
}

static void RecordingBranchAnalyzer_TwoPatterns_DetectsBranch()
{
    var dir = CreateTempDir();
    var paths = new List<string>
    {
        CreateRecordingFile(dir, "battle1.jsonl", "0x7F3A", "エアロジャ"),
        CreateRecordingFile(dir, "battle2.jsonl", "0x7F3A", "エアロジャ"),
        CreateRecordingFile(dir, "battle3.jsonl", "0x7F3A", "エアロジャ"),
        CreateRecordingFile(dir, "battle4.jsonl", "0x7F4B", "フレア"),
        CreateRecordingFile(dir, "battle5.jsonl", "0x7F4B", "フレア"),
    };
    var result = FfxivEchoes.Recording.RecordingBranchAnalyzer.Analyze(paths);
    Equal(5, result.TotalFilesScanned, "scanned files");
    False(result.NotEnoughData, "has data");
    False(result.NoBranchDetected, "branches detected");
    Equal(2, result.Groups.Count, "two main groups");
    // 最大派閥が先頭
    Equal("0x7F3A", result.Groups[0].FirstCastId, "main group is aeroja");
    Equal(3, result.Groups[0].FileCount, "aeroja count");
    Equal("0x7F4B", result.Groups[1].FirstCastId, "second group is flare");
    Equal(2, result.Groups[1].FileCount, "flare count");
    True(result.Groups[0].Confidence > result.Groups[1].Confidence, "aeroja has higher confidence");
    Equal(0, result.OutlierGroups.Count, "no outliers");
    Equal(0, result.AdditionalGroups.Count, "no additional");
}

static void RecordingBranchAnalyzer_ThreePatterns_AllPrimary()
{
    var dir = CreateTempDir();
    var paths = new List<string>
    {
        CreateRecordingFile(dir, "a1.jsonl", "0xAAAA", "Aero"),
        CreateRecordingFile(dir, "a2.jsonl", "0xAAAA", "Aero"),
        CreateRecordingFile(dir, "a3.jsonl", "0xAAAA", "Aero"),
        CreateRecordingFile(dir, "b1.jsonl", "0xBBBB", "Flare"),
        CreateRecordingFile(dir, "b2.jsonl", "0xBBBB", "Flare"),
        CreateRecordingFile(dir, "c1.jsonl", "0xCCCC", "Holy"),
        CreateRecordingFile(dir, "c2.jsonl", "0xCCCC", "Holy"),
    };
    var result = FfxivEchoes.Recording.RecordingBranchAnalyzer.Analyze(paths);
    Equal(3, result.Groups.Count, "all three patterns stay selectable");
    Equal("0xAAAA", result.Groups[0].FirstCastId, "Aero #1");
    Equal("0xBBBB", result.Groups[1].FirstCastId, "Flare #2");
    Equal("0xCCCC", result.Groups[2].FirstCastId, "Holy #3");
    Equal(0, result.AdditionalGroups.Count, "no additional within primary limit");
}

static void RecordingBranchAnalyzer_EightPatterns_AllPrimary()
{
    var dir = CreateTempDir();
    var paths = new List<string>();
    for (var i = 0; i < 8; i++)
    {
        var id = $"0x{0xA000 + i:X4}";
        var name = $"Pattern{i + 1}";
        paths.Add(CreateRecordingFile(dir, $"p{i + 1}_a.jsonl", id, name));
        paths.Add(CreateRecordingFile(dir, $"p{i + 1}_b.jsonl", id, name));
    }

    var result = FfxivEchoes.Recording.RecordingBranchAnalyzer.Analyze(paths);

    Equal(8, result.Groups.Count, "eight branch patterns stay selectable");
    Equal(0, result.AdditionalGroups.Count, "no additional for eight-way branch");
    SequenceEqual(
        Enumerable.Range(0, 8).Select(i => $"0x{0xA000 + i:X4}").ToArray(),
        result.Groups.Select(g => g.FirstCastId).ToArray(),
        "branch ids");
}

static string CreateCommonFirstBranchRecordingFile(
    string dir, string fileName,
    string secondCastId, string secondCastName)
{
    var path = Path.Combine(dir, fileName);
    File.WriteAllLines(path, new[]
    {
        """{"meta":true,"zone":"TestZone","start_time":"2026-05-10T00:00:00.000Z","plugin_version":"0.1.0","party":[{"name":"Self","job":"PLD","role":"Tank"}]}""",
        """{"time":5.0,"type":"cast_start","source":"Boss","cast_id":"0x1000","cast_name":"Common Opener","cast_time":4.0}""",
        $$"""{"time":12.0,"type":"cast_start","source":"Boss","cast_id":"{{secondCastId}}","cast_name":"{{secondCastName}}","cast_time":3.0}""",
        """{"time":20.0,"type":"cast_start","source":"Boss","cast_id":"0xCAFE","cast_name":"Common Followup","cast_time":3.0}""",
    });
    return path;
}

static void RecordingBranchAnalyzer_CommonFirstCast_DifferentSecondCast()
{
    var dir = CreateTempDir();
    var paths = new List<string>
    {
        CreateCommonFirstBranchRecordingFile(dir, "left1.jsonl", "0x2001", "Left Pattern"),
        CreateCommonFirstBranchRecordingFile(dir, "left2.jsonl", "0x2001", "Left Pattern"),
        CreateCommonFirstBranchRecordingFile(dir, "right1.jsonl", "0x2002", "Right Pattern"),
        CreateCommonFirstBranchRecordingFile(dir, "right2.jsonl", "0x2002", "Right Pattern"),
    };

    var result = FfxivEchoes.Recording.RecordingBranchAnalyzer.Analyze(paths);

    False(result.NoBranchDetected, "branch after common opener should be detected");
    Equal(2, result.Groups.Count, "two second-cast branch groups");
    Equal("0x2001", result.Groups[0].FirstCastId, "left branch discriminant");
    Equal("0x2002", result.Groups[1].FirstCastId, "right branch discriminant");
}

static void RecordingBranchAnalyzer_AllSamePattern_NoBranchDetected()
{
    var dir = CreateTempDir();
    var paths = new List<string>
    {
        CreateRecordingFile(dir, "b1.jsonl", "0xAAAA", "Aero"),
        CreateRecordingFile(dir, "b2.jsonl", "0xAAAA", "Aero"),
        CreateRecordingFile(dir, "b3.jsonl", "0xAAAA", "Aero"),
    };
    var result = FfxivEchoes.Recording.RecordingBranchAnalyzer.Analyze(paths);
    True(result.NoBranchDetected, "no branch detected");
    Equal(0, result.Groups.Count, "no main groups");
}

static void RecordingBranchAnalyzer_SingleFileGroup_Excluded()
{
    var dir = CreateTempDir();
    var paths = new List<string>
    {
        CreateRecordingFile(dir, "a1.jsonl", "0xAAAA", "Aero"),
        CreateRecordingFile(dir, "a2.jsonl", "0xAAAA", "Aero"),
        CreateRecordingFile(dir, "b1.jsonl", "0xBBBB", "Flare"),
        CreateRecordingFile(dir, "b2.jsonl", "0xBBBB", "Flare"),
        CreateRecordingFile(dir, "rare.jsonl", "0xDEAD", "Rare"),  // 1 件のみ → outlier
    };
    var result = FfxivEchoes.Recording.RecordingBranchAnalyzer.Analyze(paths);
    Equal(2, result.Groups.Count, "two main groups");
    Equal(1, result.OutlierGroups.Count, "one outlier");
    Equal("0xDEAD", result.OutlierGroups[0].FirstCastId, "rare moved to outlier");
    Equal(1, result.OutlierGroups[0].FileCount, "outlier 1 file");
    // 信頼度は有効ファイル 4 件をベースに：2/4 = 0.5 ずつ
    True(Math.Abs(result.Groups[0].Confidence - 0.5) < 0.01, $"aero conf ~0.5: {result.Groups[0].Confidence}");
    True(Math.Abs(result.Groups[1].Confidence - 0.5) < 0.01, $"flare conf ~0.5: {result.Groups[1].Confidence}");
}

/// <summary>
/// 録画ファイルに「最初の cast」+ branch 固有の cast + 共通 cast を含むパターンを書く。
/// </summary>
static string CreateRichRecordingFile(
    string dir, string fileName, string firstCastId, string firstCastName,
    string branchSpecificCastId, string branchSpecificCastName,
    string commonCastId = "0xCCCC", string commonCastName = "Common")
{
    var path = Path.Combine(dir, fileName);
    File.WriteAllLines(path, new[]
    {
        """{"meta":true,"zone":"TestZone","start_time":"2026-05-10T00:00:00.000Z","plugin_version":"0.1.0","party":[{"name":"Self","job":"PLD","role":"Tank"}]}""",
        $$"""{"time":5.0,"type":"cast_start","source":"Boss","cast_id":"{{firstCastId}}","cast_name":"{{firstCastName}}","cast_time":4.0}""",
        $$"""{"time":20.0,"type":"cast_start","source":"Boss","cast_id":"{{branchSpecificCastId}}","cast_name":"{{branchSpecificCastName}}","cast_time":3.0}""",
        $$"""{"time":40.0,"type":"cast_start","source":"Boss","cast_id":"{{commonCastId}}","cast_name":"{{commonCastName}}","cast_time":3.0}""",
    });
    return path;
}

static void StrategyDraftGenerator_LooksLikePcSkillOrPet_CoversReportedJobNames()
{
    // ユーザー報告済の漏れ語彙を全て網羅していることを確認
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("忍隠", "status_gain"), "NIN 忍隠");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("旅神のメヌエット", "status_gain"), "BRD song");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("賢人のバラード", "status_gain"), "BRD ballad");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("軍神のパイオン", "status_gain"), "BRD paean");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("踏鳴", "status_gain"), "MNK chakra");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("ストームバイト", "status_gain"), "BRD wind DoT");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("ヴェノムバイト", "status_gain"), "BRD venom DoT");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("ウィンドバイト", "status_gain"), "BRD wind DoT");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("アクセラレーション", "status_gain"), "RDM/BRD speed");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("エーテルフロー", "status_gain"), "SCH aether");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("ブラッドソイル", "status_gain"), "RPR blood");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("ディアIII", "status_gain"), "WHM dia (current naming)");
    // 「ディア」単体ではマッチしない（ボス技に "ディア" を含む可能性を排除した厳密化）
    False(StrategyDraftGenerator.LooksLikePcSkillOrPet("ディア", "status_gain"), "ディア 単体は誤マッチ防止のため非該当");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("蠱毒法", "status_gain"), "VPR poison");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("コンバストIII", "status_gain"), "SMN combust (current naming)");
    // 「コンバ」「搭乗」単体ではマッチしない（ボス技名との誤マッチ防止）
    False(StrategyDraftGenerator.LooksLikePcSkillOrPet("コンバ", "status_gain"), "コンバ 単体は誤マッチ防止");
    False(StrategyDraftGenerator.LooksLikePcSkillOrPet("搭乗", "status_gain"), "搭乗 単体は誤マッチ防止");
}

static void StatusGainedEvent_IsPlayer_DefaultBackwardCompat()
{
    // 既存呼び出し互換性：IsPlayer 引数を省略できる（既定 false）
    var ev = new StatusGainedEvent(
        DateTimeOffset.UtcNow, 1u, "Boss", 100u, "Damage Down", 30f, 1, 0u);
    False(ev.IsPlayer, "default false");
    Equal("Boss", ev.TargetName, "TargetName preserved");

    // 明示指定
    var ev2 = new StatusGainedEvent(
        DateTimeOffset.UtcNow, 1u, "Self", 1834u, "グリットスタンス", 0f, 1, 1u, IsPlayer: true);
    True(ev2.IsPlayer, "explicit true");
}

static void ObjectAppearedEvent_IsPlayer_DefaultBackwardCompat()
{
    // 既存呼び出し互換性
    var ev = new ObjectAppearedEvent(
        DateTimeOffset.UtcNow, 100u, "Boss", 5000u, System.Numerics.Vector3.Zero);
    False(ev.IsPlayer, "default false");
    Equal("Boss", ev.ObjectName, "ObjectName preserved");

    // 明示指定（フェアリー・エオス想定）
    var ev2 = new ObjectAppearedEvent(
        DateTimeOffset.UtcNow, 200u, "フェアリー・エオス", 6500u,
        System.Numerics.Vector3.Zero, IsPlayer: true);
    True(ev2.IsPlayer, "Fairy IsPlayer=true");
}

/// <summary>
/// 回帰テスト：StatusCapture.IsPlayerStatus がボスデバフを誤フィルタしないことを契約として明示。
/// 旧バグ：「target=PC なら IsPlayer=true」を返していたため、ボスがタンクに付与する
/// 「マジックバルネラビリティアップ」「Damage Down」等の絶コンテンツ攻略上重要な debuff が
/// 録画から消失していた。修正後は source 判定のみを信頼するので、ボスを source とする
/// debuff は IsPlayer=false で録画される。
/// </summary>
static void StatusGainedEvent_BossDebuffOnPc_IsNotPlayer()
{
    // 絶コンテンツのタンクバスター debuff 想定：
    //   target = タンク PC、source = ボス
    //   Lumina ClassJobCategory = 1 (All Classes) なので LuminaPcDetector.IsPlayerStatus は false
    //   source がボス（IBattleNpc）なので IsPlayerOrPlayerOwned も false
    //   結果として StatusGainedEvent.IsPlayer = false でなければならない
    var bossDebuff = new StatusGainedEvent(
        Timestamp: DateTimeOffset.UtcNow,
        TargetId: 0x1000_0001u, // PC タンク (target)
        TargetName: "TankCharacter",
        StatusId: 1014u, // 「マジックバルネラビリティアップ」想定
        StatusName: "マジックバルネラビリティアップ",
        RemainingTime: 30f,
        Stacks: 1,
        SourceId: 0x4000_0001u, // ボス (source)
        IsPlayer: false); // ★ 旧バグでは true になっていた
    False(bossDebuff.IsPlayer, "Boss debuff on PC must be IsPlayer=false (so it gets recorded)");

    // ダメージダウン（ボスの強デバフ）
    var damageDown = new StatusGainedEvent(
        Timestamp: DateTimeOffset.UtcNow,
        TargetId: 0x1000_0002u,
        TargetName: "TankCharacter2",
        StatusId: 696u, // 「Damage Down」想定
        StatusName: "ダメージダウン",
        RemainingTime: 15f,
        Stacks: 1,
        SourceId: 0x4000_0001u,
        IsPlayer: false);
    False(damageDown.IsPlayer, "Damage Down on PC must be IsPlayer=false");
}

/// <summary>
/// 回帰テスト：PC self-buff（タンクスタンス・ジョブゲージ等）は IsPlayer=true で BattleRecorder
/// が録画ファイルから skip すること。
/// </summary>
static void StatusGainedEvent_PcSelfBuff_IsPlayer()
{
    // GNB の「ロイヤルガード」（タンクスタンス）想定：
    //   target = self (PC)、source = self (PC)
    //   Lumina ClassJobCategory = 41 (GNB) → IsPlayerStatus = true
    var pcStance = new StatusGainedEvent(
        Timestamp: DateTimeOffset.UtcNow,
        TargetId: 0x1000_0001u, // self (PC)
        TargetName: "MyTank",
        StatusId: 1833u, // ロイヤルガード
        StatusName: "ロイヤルガード",
        RemainingTime: 0f,
        Stacks: 0,
        SourceId: 0x1000_0001u, // self
        IsPlayer: true); // PC ジョブカテゴリ判定で true
    True(pcStance.IsPlayer, "PC self-buff stance is IsPlayer=true");
}

/// <summary>
/// 回帰テスト：HpChangedEvent.IsPlayer を省略しても録画フォーマットの後方互換が維持されること。
/// </summary>
static void HpChangedEvent_IsPlayer_DefaultBackwardCompat()
{
    var ev = new HpChangedEvent(
        DateTimeOffset.UtcNow, 100u, "Boss", 50f, 5000u, 10000u);
    False(ev.IsPlayer, "default false");
    Equal("Boss", ev.ActorName, "ActorName preserved");

    var ev2 = new HpChangedEvent(
        DateTimeOffset.UtcNow, 200u, "MyChar", 75f, 30000u, 40000u, IsPlayer: true);
    True(ev2.IsPlayer, "PC HP change explicit");
}

/// <summary>
/// 回帰テスト：raid-wide attack でボスが受ける反動 / フェーズ HP 変化は IsPlayer=false で
/// MechanicTriggerService.OnHpChanged に到達して hp_pct トリガーを評価できること。
/// 旧バグ：MechanicTriggerService が IsPlayer フィルタなしで PT 8 人分の HP 変化も毎フレーム
/// スキャンしていたため、本テストはその反対側（ボス HP は確実に通過する）を保証する。
/// </summary>
static void HpChangedEvent_BossRaidWide_IsNotPlayer()
{
    // ボス HP 79.5% → 80% 跨ぎ想定
    var ev = new HpChangedEvent(
        DateTimeOffset.UtcNow, 0x4000_0001u, "デュオダイナミス", 79.5f, 79500u, 100000u,
        IsPlayer: false);
    False(ev.IsPlayer, "Boss HP change must NOT be marked IsPlayer");

    // PT メンバーの HP 変化（IsPlayer=true）— hp_pct トリガーから除外される側
    var ptMember = new HpChangedEvent(
        DateTimeOffset.UtcNow, 0x1000_0002u, "FellowTank", 50f, 30000u, 60000u,
        IsPlayer: true);
    True(ptMember.IsPlayer, "PT member HP IS IsPlayer (skipped by hp_pct trigger)");
}

static void PcSkillNameFilter_RejectsNewReportedSkills()
{
    // 最新の漏れ報告（フェアリー / グリット / 英雄の影身）
    True(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("フェアリー・エオス", "object_appear"), "SCH 妖精 (object_appear)");
    True(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("フェアリー・セレネ", "object_appear"), "SCH 妖精 (alt name)");
    True(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("フェアリー", "hp_change"), "妖精 hp");
    True(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("グリットスタンス", "status_gain"), "GNB スタンス");
    True(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("英雄の影身", "object_appear"), "NIN/RPR 影身");
    True(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("英雄の影身", "status_gain"), "影身 status");
    True(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("オートマトン・クイーン", "cast_start"), "MCH オートマトン");

    // ボス系は維持（誤フィルタしない）
    False(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("メガフレア", "cast_start"), "ボスキャスト維持");
    False(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("脱出地点", "object_appear"), "環境オブジェクト維持");
}

static void PcSkillNameFilter_RejectsCastAndPetSkills()
{
    // ユーザー報告のタイムライン漏れを cast_start / action_used として検証
    True(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("ブリフルジェンス", "cast_start"), "PCT cast");
    True(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("トアクリーバー", "cast_start"), "DRK cast");
    True(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("クイーン・ローラーダッシュ", "cast_start"), "MCH Queen pet");
    True(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("夢幻三段", "cast_start"), "NIN Mug");
    True(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("サベッジクロウ", "cast_start"), "MNK savage");
    True(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("祖霊の蛇【参】", "cast_start"), "PCT 祖霊");
    True(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("ハンマーコンボ", "action_used"), "WAR action");

    // ボスキャストは通る
    False(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("メガフレア", "cast_start"), "ボスキャスト維持");
    False(FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet("ステュクス", "cast_start"), "ボス debuff キャスト");

    // 委譲先（StrategyDraftGenerator）も同じ結果
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("ブリフルジェンス", "cast_start"), "委譲動作確認");
}

static void StrategyDraftGenerator_LooksLikePcSkillOrPet_CoversAstPct()
{
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("イマジンスカイ", "status_gain"), "PCT イマジンスカイ");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("ディヴィネーション", "status_gain"), "AST ディヴィネーション");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("アーサリースター", "status_gain"), "AST アーサリースター");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("クリーチャーモチーフ", "status_gain"), "PCT クリーチャー");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("ハンマーモチーフ", "status_gain"), "PCT ハンマー");
}

static void Aggregation_FiltersSelfAppliedStatusEvents()
{
    // 自己付与 status の扱い：
    //  - party meta あり + source==target が PT メンバー → IsPartySource=true（filter される）
    //  - party meta あり + source==target が PT メンバー外（=ボス自己強化）→ IsPartySource=false（残る）
    //  - party meta 空 → 後方互換で source==target を一律 filter
    var dir = CreateTempDir();
    var path = Path.Combine(dir, "battle.jsonl");
    File.WriteAllLines(path, new[]
    {
        // party meta: PT メンバーは object_id=2001
        """{"meta":true,"zone":"Test","start_time":"2026-05-10T00:00:00.000Z","plugin_version":"0.1.0","party":[{"name":"Self","object_id":2001}]}""",
        // PT メンバー（id=2001）の self-buff → IsPartySource=true で除外
        """{"time":1.0,"type":"status_gain","source":"Self","source_id":2001,"target":"Self","target_id":2001,"status_id":1,"status_name":"忍隠","duration":30.0,"stacks":1}""",
        // ボス（id=5000、PT 外）の self-buff（エンレイジ等）→ IsPartySource=false で残る
        """{"time":2.0,"type":"status_gain","source":"Boss","source_id":5000,"target":"Boss","target_id":5000,"status_id":2,"status_name":"エンレイジ","duration":0.0,"stacks":1}""",
        // ボスのキャスト（残すべき）
        """{"time":3.0,"type":"cast_start","source":"Boss","cast_id":"0xABCD","cast_name":"BossSkill","cast_time":4.0}""",
    });

    var agg = RecordingAggregationReader.AggregateFiles(new[] { path });

    var ptSelfBuff = agg.Events.FirstOrDefault(e => e.Key.Type == "status_gain" && e.Key.Source == "Self");
    NotNull(ptSelfBuff, "PT self-buff aggregated");
    True(ptSelfBuff!.IsPartySource, "PT member self-buff IS flagged as party (filtered)");

    var bossSelfBuff = agg.Events.FirstOrDefault(e => e.Key.Type == "status_gain" && e.Key.Source == "Boss");
    NotNull(bossSelfBuff, "boss self-buff aggregated");
    False(bossSelfBuff!.IsPartySource, "boss self-buff (enrage) NOT flagged (must remain as mechanic candidate)");

    var bossCast = agg.Events.FirstOrDefault(e => e.Key.Type == "cast_start");
    NotNull(bossCast, "boss cast aggregated");
    False(bossCast!.IsPartySource, "boss cast NOT flagged as party (must remain)");
}

static void Aggregation_OldRecordingFallback_FiltersSelfAppliedWithoutPartyMeta()
{
    // party meta が空（古い録画 / アクセス失敗）の場合は source==target を一律 filter
    // （後方互換、PC 自己バフを最低限弾くフォールバック）
    var dir = CreateTempDir();
    var path = Path.Combine(dir, "battle.jsonl");
    File.WriteAllLines(path, new[]
    {
        """{"meta":true,"zone":"Test","start_time":"2026-05-10T00:00:00.000Z","plugin_version":"0.1.0","party":[]}""",
        // self-applied (party meta なし)
        """{"time":1.0,"type":"status_gain","source":"X","source_id":1234,"target":"X","target_id":1234,"status_id":1,"status_name":"忍隠","duration":30.0,"stacks":1}""",
    });

    var agg = RecordingAggregationReader.AggregateFiles(new[] { path });
    var ev = agg.Events.FirstOrDefault(e => e.Key.Type == "status_gain");
    NotNull(ev, "status event aggregated");
    True(ev!.IsPartySource, "fallback: self-applied filtered when party meta empty");
}

static void StrategyDraftGenerator_LooksLikePcSkillOrPet_RejectsKnownNames()
{
    // PC ジョブステータス → 除外
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("ハンマーコンボ実行可", "status_gain"), "WAR ハンマーコンボ");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("ロイエ実行可", "status_gain"), "WAR ロイエ");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("ルクス・ソラリス実行可", "status_gain"), "SGE ルクス・ソラリス");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("祖霊降ろし実行可", "status_gain"), "PCT 祖霊降ろし");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("弐の型：走竜", "status_gain"), "SAM の型");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("強化薬", "status_gain"), "強化薬");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("蛇鋭牙【穿裂】", "status_gain"), "VPR 蛇鋭牙");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("忍術", "status_gain"), "NIN 忍術");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("ハンマーコンボ実行可", "status_update"), "status_update も対象");

    // PC ペット名（hp_change） → 除外
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("カーバンクル", "hp_change"), "SMN カーバンクル");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("ソルバハムート", "hp_change"), "SMN ソルバハムート");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("ソル・バハムート", "hp_change"), "SMN ソル・バハムート");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("バハムート", "hp_change"), "SMN バハムート");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("ガルーダ・エギ", "hp_change"), "SMN ガルーダ・エギ");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("妖精ノクターナ", "hp_change"), "SCH 妖精");
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("オートタレット", "hp_change"), "MCH タレット");

    // ボススキル / ボス AoE → 維持（false が返るべき）
    False(StrategyDraftGenerator.LooksLikePcSkillOrPet("メガフレア", "cast_start"), "ボスキャストは通る（cast_start は対象外）");
    False(StrategyDraftGenerator.LooksLikePcSkillOrPet("メガフレア", "status_gain"), "メガフレア（status_gain）は PC キーワード含まない");
    False(StrategyDraftGenerator.LooksLikePcSkillOrPet("ステュクス", "status_gain"), "ボス debuff は通る");
    False(StrategyDraftGenerator.LooksLikePcSkillOrPet("ゾディアーク", "hp_change"), "ボス HP は通る");
    False(StrategyDraftGenerator.LooksLikePcSkillOrPet("オメガ", "hp_change"), "ボス HP は通る");
    False(StrategyDraftGenerator.LooksLikePcSkillOrPet("", "status_gain"), "空文字列");
    False(StrategyDraftGenerator.LooksLikePcSkillOrPet(null, "status_gain"), "null");

    // status キーワード（「実行可」など）は cast_start には適用されない
    False(StrategyDraftGenerator.LooksLikePcSkillOrPet("実行可テスト", "cast_start"), "cast_start は status キーワード対象外");
    // cast_start のペット名フィルタは PcSkillNameFilter に統合され、ペット cast を catch する
    True(StrategyDraftGenerator.LooksLikePcSkillOrPet("カーバンクル", "cast_start"), "cast_start でペット名 catch");
}

static void TriggerBranchApplier_GenerateMechanics_TagsBranchExclusiveCasts()
{
    var dir = CreateTempDir();
    // パターン A：Aero (最初) + AeroSpecial + Common
    var aeroFiles = new List<string>
    {
        CreateRichRecordingFile(dir, "a1.jsonl", "0xAAAA", "Aero", "0xAAAB", "AeroSpecial"),
        CreateRichRecordingFile(dir, "a2.jsonl", "0xAAAA", "Aero", "0xAAAB", "AeroSpecial"),
    };
    // パターン B：Flare (最初) + FlareSpecial + Common
    var flareFiles = new List<string>
    {
        CreateRichRecordingFile(dir, "b1.jsonl", "0xBBBB", "Flare", "0xBBBC", "FlareSpecial"),
        CreateRichRecordingFile(dir, "b2.jsonl", "0xBBBB", "Flare", "0xBBBC", "FlareSpecial"),
    };
    var allFiles = aeroFiles.Concat(flareFiles).ToList();

    // 1. 分岐検出
    var detection = FfxivEchoes.Recording.RecordingBranchAnalyzer.Analyze(allFiles);
    Equal(2, detection.Groups.Count, "two groups detected");

    // 2. 全集約（generateMechanics に渡す）
    var fullAgg = FfxivEchoes.Recording.RecordingAggregationReader.AggregateFiles(allFiles);

    var file = new TriggerFile
    {
        Zone = "TestZone",
        ActiveStrategyProfileId = "p1",
        StrategyProfiles = new List<StrategyProfile>
        {
            new StrategyProfile { Id = "p1", Name = "Test", Enabled = true },
        },
    };

    // 3. generateMechanics=true で適用
    var party = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var applyResult = TriggerBranchApplier.Apply(
        file, detection, detection.Groups,
        generateMechanics: true,
        fullAggregate: fullAgg,
        partyMembers: party);

    Equal(2, applyResult.BranchesAdded, "2 branches added");
    True(applyResult.MechanicsGenerated > 0, $"some mechanics generated: got {applyResult.MechanicsGenerated}");

    // 4. 生成された mechanic をラベルで検査
    var mechanics = file.StrategyProfiles[0].Mechanics;

    // Aero (branch A の最初 cast) → branch_id = pattern_aero_*
    var aeroMech = mechanics.FirstOrDefault(m =>
        m.AttachedTo?.CastId == "0xAAAA" || m.Label?.Contains("Aero", StringComparison.OrdinalIgnoreCase) == true && !(m.Label?.Contains("Special") ?? false));
    NotNull(aeroMech, "Aero mechanic generated");
    True(aeroMech!.BranchId is { } && aeroMech.BranchId.Contains("aero", StringComparison.OrdinalIgnoreCase),
        $"Aero mechanic has branch_id with 'aero': got {aeroMech.BranchId}");

    // FlareSpecial (branch B のみ) → branch_id = pattern_flare_*
    var flareSpecialMech = mechanics.FirstOrDefault(m =>
        m.AttachedTo?.CastId == "0xBBBC");
    NotNull(flareSpecialMech, "FlareSpecial mechanic generated");
    True(flareSpecialMech!.BranchId is { } && flareSpecialMech.BranchId.Contains("flare", StringComparison.OrdinalIgnoreCase),
        $"FlareSpecial has branch_id with 'flare': got {flareSpecialMech.BranchId}");

    // Common (両 branch にある) → branch_id = null
    var commonMech = mechanics.FirstOrDefault(m => m.AttachedTo?.CastId == "0xCCCC");
    NotNull(commonMech, "Common mechanic generated");
    Null(commonMech!.BranchId, "Common mechanic stays branch_id=null");
}

static void TriggerBranchApplier_DoesNotOverwriteExistingBranchId()
{
    // 検出結果（手動構築：分析を経ずに Apply の挙動だけ検証）
    var groupA = new FfxivEchoes.Recording.BranchGroup
    {
        FirstCastId = "0x7F3A",
        FirstCastName = "Aero",
        FileCount = 3,
        FilePaths = new[] { "a1.jsonl", "a2.jsonl", "a3.jsonl" },
        Confidence = 0.6,
    };
    var groupB = new FfxivEchoes.Recording.BranchGroup
    {
        FirstCastId = "0x7F4B",
        FirstCastName = "Flare",
        FileCount = 2,
        FilePaths = new[] { "b1.jsonl", "b2.jsonl" },
        Confidence = 0.4,
    };
    var detection = new FfxivEchoes.Recording.BranchDetectionResult
    {
        TotalFilesScanned = 5,
        Groups = new[] { groupA, groupB },
    };

    var file = new TriggerFile
    {
        Zone = "TestZone",
        ActiveStrategyProfileId = "p1",
        StrategyProfiles = new List<StrategyProfile>
        {
            new StrategyProfile
            {
                Id = "p1", Name = "Test", Enabled = true,
                Mechanics = new List<MechanicStrategy>
                {
                    // 既存：手動で branch_id 設定済 → 上書きされない
                    new MechanicStrategy { Id = "m1", Label = "Manual", Enabled = true, BranchId = "manual_set" },
                    // 既存：branch_id なし、AttachedTo もなし
                    new MechanicStrategy { Id = "m2", Label = "NoCast", Enabled = true },
                },
            },
        },
    };

    var result = TriggerBranchApplier.Apply(file, detection, new[] { groupA, groupB });
    Equal(2, result.BranchesAdded, "2 branches added");
    // 既存 m1 の branch_id は変わらない
    Equal("manual_set", file.StrategyProfiles[0].Mechanics[0].BranchId, "m1 manual branch_id preserved");
    // m2 は AttachedTo が無いので付与されない
    Null(file.StrategyProfiles[0].Mechanics[1].BranchId, "m2 stays null (no AttachedTo)");
    // branches[] が 2 件追加されている
    Equal(2, file.Branches.Count, "branches written");
    // 同一 cast_id で 2 回 Apply しても重複しない
    var result2 = TriggerBranchApplier.Apply(file, detection, new[] { groupA, groupB });
    Equal(0, result2.BranchesAdded, "duplicate apply adds nothing");
    Equal(2, file.Branches.Count, "branches still 2 (no dup)");
}

static void StrategyPlanResolver_BuildTimelineNotes_FiltersByBranch()
{
    var file = new TriggerFile
    {
        Zone = "TestZone",
        ActiveStrategyProfileId = "p1",
        StrategyProfiles = new List<StrategyProfile>
        {
            new StrategyProfile
            {
                Id = "p1",
                Name = "Test",
                Enabled = true,
                Mechanics = new List<MechanicStrategy>
                {
                    new MechanicStrategy { Id = "common", Label = "Common", Enabled = true, Time = 10, BranchId = null },
                    new MechanicStrategy { Id = "branch_a", Label = "PatternA", Enabled = true, Time = 20, BranchId = "pattern_a" },
                    new MechanicStrategy { Id = "branch_b", Label = "PatternB", Enabled = true, Time = 30, BranchId = "pattern_b" },
                },
            },
        },
    };

    // 引数なし（branchActiveCheck = null）：全 mechanic 含まれる
    var allNotes = StrategyPlanResolver.BuildTimelineNotes(file);
    Equal(3, allNotes.Count, "no filter → 3 mechanics");

    // pattern_a を active 扱い、pattern_b を rejected 扱い
    Func<string?, bool> activeA = bid => bid is null || bid == "pattern_a";
    var aNotes = StrategyPlanResolver.BuildTimelineNotes(file, activeA);
    Equal(2, aNotes.Count, "active=A → common + A");
    True(aNotes.Any(n => n.Label == "Common"), "common shown");
    True(aNotes.Any(n => n.Label == "PatternA"), "A shown");
    False(aNotes.Any(n => n.Label == "PatternB"), "B hidden");

    // 全 branch pending（共通のみ表示）
    Func<string?, bool> commonOnly = bid => string.IsNullOrEmpty(bid);
    var commonNotes = StrategyPlanResolver.BuildTimelineNotes(file, commonOnly);
    Equal(1, commonNotes.Count, "all pending → only common");
    Equal("Common", commonNotes[0].Label, "common label");
}

static void ActorTrackedAoe_ComputePhaseAlpha_ReturnsPerPhaseAlphas()
{
    var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Predicted（PredictedFireAt 未設定の fallback）: 固定 (0x55, 0xC0)
    var (fp, sp) = ActorTrackedAoeService.ComputePhaseAlpha(
        ActorTrackedAoeService.AoePhase.Predicted, t0, t0);
    Equal((byte)0x55, fp, "Predicted fill (no fireAt)");
    Equal((byte)0xC0, sp, "Predicted stroke (no fireAt)");

    // Predicted カウントダウン：着弾時刻が近づくほど濃くなる
    // 10s 前：base 0x55（控えめ表示）
    var (fp10, _) = ActorTrackedAoeService.ComputePhaseAlpha(
        ActorTrackedAoeService.AoePhase.Predicted, t0, t0, t0.AddSeconds(10));
    Equal((byte)0x55, fp10, "Predicted 10s before fire (base alpha)");

    // 3s 前：0x70-0x90 範囲
    var (fp3, _) = ActorTrackedAoeService.ComputePhaseAlpha(
        ActorTrackedAoeService.AoePhase.Predicted, t0, t0, t0.AddSeconds(3));
    True(fp3 > 0x70 && fp3 < 0x90, $"Predicted 3s before fire ramping up: got 0x{fp3:X2}");

    // 1s 前：0x90+ （Impact 直前の緊急感）
    var (fp1, sp1) = ActorTrackedAoeService.ComputePhaseAlpha(
        ActorTrackedAoeService.AoePhase.Predicted, t0, t0, t0.AddSeconds(1));
    True(fp1 >= 0x90, $"Predicted 1s before fire near-peak: got 0x{fp1:X2}");
    Equal((byte)0xFF, sp1, "Predicted 1s before fire stroke at max");

    // 着弾時刻到達：最濃度
    var (fp0, sp0) = ActorTrackedAoeService.ComputePhaseAlpha(
        ActorTrackedAoeService.AoePhase.Predicted, t0, t0, t0);
    True(fp0 >= 0xA0, $"Predicted at fire time near-peak: got 0x{fp0:X2}");
    Equal((byte)0xFF, sp0, "Predicted at fire stroke max");

    // 単調増加（着弾が近づくと濃くなる）
    True(fp10 <= fp3 && fp3 <= fp1 && fp1 <= fp0,
        $"Predicted alpha must monotonically increase: 0x{fp10:X2} → 0x{fp3:X2} → 0x{fp1:X2} → 0x{fp0:X2}");

    // Confirmed: パルスは [0x70, 0xC0] 範囲内（snap-in 0.4s ピーク + base 0x90 + sin パルス）
    for (var dt = 0.0; dt < 2.0; dt += 0.1)
    {
        var (fc, sc) = ActorTrackedAoeService.ComputePhaseAlpha(
            ActorTrackedAoeService.AoePhase.Confirmed, t0, t0.AddSeconds(dt));
        True(fc >= 0x70 && fc <= 0xC0, $"Confirmed fill in [0x70, 0xC0] at t={dt}: got 0x{fc:X2}");
        Equal((byte)0xFF, sc, $"Confirmed stroke at t={dt}");
    }

    // Confirmed t=0：snap-in でピーク（0xB0 付近、base 0x90 + boost 0x20）
    var (fcStart, _) = ActorTrackedAoeService.ComputePhaseAlpha(
        ActorTrackedAoeService.AoePhase.Confirmed, t0, t0);
    True(fcStart >= 0xA0, $"Confirmed t=0 should be near-peak for snap-in: got 0x{fcStart:X2}");

    // Confirmed t=0.5（snap-in 終了後）：base 値付近に落ち着く
    var (fcSettled, _) = ActorTrackedAoeService.ComputePhaseAlpha(
        ActorTrackedAoeService.AoePhase.Confirmed, t0, t0.AddSeconds(0.5));
    True(fcSettled < fcStart, $"Confirmed t=0.5 should settle below t=0: got 0x{fcSettled:X2} vs start 0x{fcStart:X2}");

    // Impact 0 秒：ピーク (0x90, 0xFF)
    var (fi0, si0) = ActorTrackedAoeService.ComputePhaseAlpha(
        ActorTrackedAoeService.AoePhase.Impact, t0, t0);
    Equal((byte)0x90, fi0, "Impact peak fill");
    Equal((byte)0xFF, si0, "Impact peak stroke");

    // Impact ImpactFlashSec 直前：まだピーク
    var (fi1, _) = ActorTrackedAoeService.ComputePhaseAlpha(
        ActorTrackedAoeService.AoePhase.Impact, t0, t0.AddSeconds(ActorTrackedAoeService.ImpactFlashSec - 0.05));
    Equal((byte)0x90, fi1, "Impact pre-flash-end fill");

    // Impact ImpactFlashSec 経過後：fade 値（fc 値より低い）
    var (fi2, _) = ActorTrackedAoeService.ComputePhaseAlpha(
        ActorTrackedAoeService.AoePhase.Impact, t0,
        t0.AddSeconds(ActorTrackedAoeService.ImpactFlashSec + 0.5));
    True(fi2 < 0x90, $"Impact post-flash fill should be lower: got 0x{fi2:X2}");

    // Fade 0 秒：(0x60, 0xE0)
    var (ff0, sf0) = ActorTrackedAoeService.ComputePhaseAlpha(
        ActorTrackedAoeService.AoePhase.Fade, t0, t0);
    Equal((byte)0x60, ff0, "Fade start fill");
    Equal((byte)0xE0, sf0, "Fade start stroke");

    // Fade FadeDurationSec 経過後：(0, 0)
    var (ff1, sf1) = ActorTrackedAoeService.ComputePhaseAlpha(
        ActorTrackedAoeService.AoePhase.Fade, t0, t0.AddSeconds(ActorTrackedAoeService.FadeDurationSec + 0.1));
    Equal((byte)0x00, ff1, "Fade end fill");
    Equal((byte)0x00, sf1, "Fade end stroke");

    // Fade 中間（半分経過）：約半分の値
    var (ffMid, sfMid) = ActorTrackedAoeService.ComputePhaseAlpha(
        ActorTrackedAoeService.AoePhase.Fade, t0, t0.AddSeconds(ActorTrackedAoeService.FadeDurationSec * 0.5));
    True(ffMid > 0x20 && ffMid < 0x50, $"Fade mid fill ~ half of 0x60: got 0x{ffMid:X2}");
    True(sfMid > 0x50 && sfMid < 0x90, $"Fade mid stroke ~ half of 0xE0: got 0x{sfMid:X2}");
}

static void ActorTrackedAoe_ParseZoneShape_CoversAllStringForms()
{
    // 既存 5 種 + 新 3 種 + 別綴り + 不明
    Equal(ActorTrackedAoeService.TrackedAoeShape.Circle, ActorTrackedAoeService.ParseZoneShape("circle"), "circle");
    Equal(ActorTrackedAoeService.TrackedAoeShape.Donut, ActorTrackedAoeService.ParseZoneShape("donut"), "donut");
    Equal(ActorTrackedAoeService.TrackedAoeShape.Cone, ActorTrackedAoeService.ParseZoneShape("cone"), "cone");
    Equal(ActorTrackedAoeService.TrackedAoeShape.Rect, ActorTrackedAoeService.ParseZoneShape("rect"), "rect");
    Equal(ActorTrackedAoeService.TrackedAoeShape.Rect, ActorTrackedAoeService.ParseZoneShape("line"), "line → rect");
    Equal(ActorTrackedAoeService.TrackedAoeShape.Cross, ActorTrackedAoeService.ParseZoneShape("cross"), "cross");
    Equal(ActorTrackedAoeService.TrackedAoeShape.DonutCone, ActorTrackedAoeService.ParseZoneShape("donut_cone"), "donut_cone");
    Equal(ActorTrackedAoeService.TrackedAoeShape.DonutCone, ActorTrackedAoeService.ParseZoneShape("donutcone"), "donutcone alias");
    Equal(ActorTrackedAoeService.TrackedAoeShape.HalfPlane, ActorTrackedAoeService.ParseZoneShape("half_plane"), "half_plane");
    Equal(ActorTrackedAoeService.TrackedAoeShape.HalfPlane, ActorTrackedAoeService.ParseZoneShape("halfplane"), "halfplane alias");
    // 大文字小文字 / 空白許容
    Equal(ActorTrackedAoeService.TrackedAoeShape.Circle, ActorTrackedAoeService.ParseZoneShape("CIRCLE"), "uppercase");
    Equal(ActorTrackedAoeService.TrackedAoeShape.Cone, ActorTrackedAoeService.ParseZoneShape("  cone  "), "whitespace");
    // 不明値
    Null(ActorTrackedAoeService.ParseZoneShape(""), "empty");
    Null(ActorTrackedAoeService.ParseZoneShape("triangle"), "triangle (unknown)");
    Null(ActorTrackedAoeService.ParseZoneShape(null), "null");
}

static void AoeAnchorResolver_ExtractSourceIdFromEvents()
{
    // CastStartedEvent → SourceId
    var castStart = new CastStartedEvent(DateTimeOffset.UtcNow, 12345u, "Boss", 0xABCD, "Skill", 5.0f, 99u);
    Equal(12345u, AoeAnchorResolver.ResolveSourceActorId(castStart), "cast_start → SourceId");
    // CastCanceledEvent → SourceId
    var castCancel = new CastCanceledEvent(DateTimeOffset.UtcNow, 12345u, "Boss", 0xABCD, "Skill");
    Equal(12345u, AoeAnchorResolver.ResolveSourceActorId(castCancel), "cast_cancel → SourceId");
    // ActionUsedEvent → SourceId
    var action = new ActionUsedEvent(DateTimeOffset.UtcNow, 999u, "Boss", 0x100, "Action", null, false);
    Equal(999u, AoeAnchorResolver.ResolveSourceActorId(action), "action_used → SourceId");
    // StatusGainedEvent → TargetId（status は target に付くので target を起点）
    var status = new StatusGainedEvent(DateTimeOffset.UtcNow, 7777u, "Player", 100u, "Bleed", 30f, 1, 12345u);
    Equal(7777u, AoeAnchorResolver.ResolveSourceActorId(status), "status_gain → TargetId");
    // ObjectAppearedEvent → EntityId（なければ ObjectId）
    var obj = new ObjectAppearedEvent(DateTimeOffset.UtcNow, 5555u, "Add", 0xDDDD, System.Numerics.Vector3.Zero);
    Equal(5555u, AoeAnchorResolver.ResolveSourceActorId(obj), "object_appear → ObjectId");
    var objWithEntity = new ObjectAppearedEvent(DateTimeOffset.UtcNow, 5555u, "Add", 0xDDDD,
        System.Numerics.Vector3.Zero, EntityId: 9999u);
    Equal(9999u, AoeAnchorResolver.ResolveSourceActorId(objWithEntity), "object_appear → EntityId");
    // 未対応イベントは 0
    var combat = new CombatStartedEvent(DateTimeOffset.UtcNow);
    Equal(0u, AoeAnchorResolver.ResolveSourceActorId(combat), "combat_start → 0");
    Equal(0u, AoeAnchorResolver.ResolveSourceActorId(null), "null → 0");
}

static void AoeAnchorResolver_UsesObjectEventPositions()
{
    var pos = new Vector3(12f, 0f, 34f);
    var ev = new ObjectAppearedEvent(DateTimeOffset.UtcNow, 5555u, "秘紋", 13711u, pos);
    var zone = new StrategyAoeZone { Anchor = "matched_object", Shape = "rect" };

    var anchors = AoeAnchorResolver.ResolveEventStaticAnchors(zone, Vector3.Zero, ev);

    Equal(1, anchors.Count, "object anchor count");
    False(anchors[0].ActorId.HasValue, "object event anchor should not depend on live actor");
    NotNull(anchors[0].StaticWorldPos, "object event anchor should use static world position");
    Equal(pos.X, anchors[0].StaticWorldPos!.Value.X, "object anchor x");
    Equal(pos.Z, anchors[0].StaticWorldPos!.Value.Z, "object anchor z");
}

static void AoeAnchorResolver_ExpandsObjectGroupPositions()
{
    var positions = new[]
    {
        new Vector3(90f, 0f, 100f),
        new Vector3(110f, 0f, 100f),
    };
    var ev = new ObjectGroupAppearedEvent(DateTimeOffset.UtcNow, "秘紋", 13711u, positions.Length, positions);
    var zone = new StrategyAoeZone { Anchor = "each_matched_object", Shape = "circle" };

    var anchors = AoeAnchorResolver.ResolveEventStaticAnchors(zone, Vector3.Zero, ev);

    Equal(2, anchors.Count, "object group anchor count");
    SequenceEqual(positions.Select(p => p.X).ToArray(),
        anchors.Select(a => a.StaticWorldPos!.Value.X).ToArray(),
        "object group x positions");
    SequenceEqual(positions.Select(p => p.Z).ToArray(),
        anchors.Select(a => a.StaticWorldPos!.Value.Z).ToArray(),
        "object group z positions");
}

static void AoeSequenceStep_ScheduleAccepts()
{
    // データクラスの基本動作確認（インスタンス化と JSON ラウンドトリップ可能性）
    var step = new AoeSequenceStep
    {
        DelaySec = 2.5,
        DurationSec = 3.0,
        Label = "step1",
        Zones = new List<StrategyAoeZone>
        {
            new StrategyAoeZone { Id = "z1", Shape = "circle", X = 0, Z = 0, RadiusM = 5.0 },
        },
    };
    var seq = new AoeSequence
    {
        Id = "seq1",
        CancelOnCastCancel = true,
        Steps = new List<AoeSequenceStep> { step },
    };
    Equal(1, seq.Steps.Count, "step count");
    Equal(2.5, seq.Steps[0].DelaySec, "delay");
    Equal("step1", seq.Steps[0].Label, "label");
    Equal(1, seq.Steps[0].Zones.Count, "zone count");
    True(seq.CancelOnCastCancel, "cancel_on_cast_cancel default");

    // JSON ラウンドトリップ：JsonPropertyName が正しく機能しているか
    var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
    var json = System.Text.Json.JsonSerializer.Serialize(seq, opts);
    True(json.Contains("\"delay_sec\":2.5"), $"delay_sec serialized in: {json}");
    True(json.Contains("\"cancel_on_cast_cancel\":true"), $"cancel_on_cast_cancel serialized in: {json}");
    var rt = System.Text.Json.JsonSerializer.Deserialize<AoeSequence>(json, opts);
    NotNull(rt, "deserialize");
    Equal(1, rt!.Steps.Count, "rt step count");
    Equal(2.5, rt.Steps[0].DelaySec, "rt delay");
}

static void ActorTrackedAoe_InferShape_CoversLuminaCastTypes()
{
    // Lumina の CastType: 2/5 = circle, 3/13 = cone, 4/12 = rect, 6/7/10 = donut, 11 = cross.
    Equal(ActorTrackedAoeService.TrackedAoeShape.Circle, ActorTrackedAoeService.InferShape(2), "type 2 → circle");
    Equal(ActorTrackedAoeService.TrackedAoeShape.Circle, ActorTrackedAoeService.InferShape(5), "type 5 → circle");
    Equal(ActorTrackedAoeService.TrackedAoeShape.Cone, ActorTrackedAoeService.InferShape(3), "type 3 → cone");
    Equal(ActorTrackedAoeService.TrackedAoeShape.Rect, ActorTrackedAoeService.InferShape(4), "type 4 → rect");
    Equal(ActorTrackedAoeService.TrackedAoeShape.Donut, ActorTrackedAoeService.InferShape(6), "type 6 → donut");
    Equal(ActorTrackedAoeService.TrackedAoeShape.Donut, ActorTrackedAoeService.InferShape(7), "type 7 → donut");
    Equal(ActorTrackedAoeService.TrackedAoeShape.Donut, ActorTrackedAoeService.InferShape(10), "type 10 → donut");
    Equal(ActorTrackedAoeService.TrackedAoeShape.Cross, ActorTrackedAoeService.InferShape(11), "type 11 → cross");
    Equal(ActorTrackedAoeService.TrackedAoeShape.Rect, ActorTrackedAoeService.InferShape(12), "type 12 → rect");
    Equal(ActorTrackedAoeService.TrackedAoeShape.Cone, ActorTrackedAoeService.InferShape(13), "type 13 → cone");
    Null(ActorTrackedAoeService.TryInferShape(99), "unknown cast type should not be guessed as circle");
    Null(ActorTrackedAoeService.TryInferShape(0), "cast type 0 should not draw guessed AoE");
}

static void ActorTrackedAoe_AppliesUserZoneHitboxAndRotationOffset()
{
    Equal(12f, ActorTrackedAoeService.ApplyHitboxRadius(10f, 2f, includeHitbox: true),
        "hitbox should extend user-authored actor AoE radius");
    Equal(10f, ActorTrackedAoeService.ApplyHitboxRadius(10f, 2f, includeHitbox: false),
        "hitbox should be opt-in for user-authored AoE");
    NearlyEqual(MathF.PI, ActorTrackedAoeService.ApplyRenderRotationOffset(MathF.PI / 2f, MathF.PI / 2f),
        "rotation offset should rotate render angle");
}

static void ActorTrackedAoe_HalfPlaneUsesDefaultWidth()
{
    Equal(AoeGeometryPolicy.DefaultLineHalfWidthM,
        ActorTrackedAoeService.ResolveHalfWidthForShape(ActorTrackedAoeService.TrackedAoeShape.HalfPlane, null),
        "half_plane should not collapse to zero width on the floor overlay");
    Equal(7.5f,
        ActorTrackedAoeService.ResolveHalfWidthForShape(ActorTrackedAoeService.TrackedAoeShape.HalfPlane, 7.5),
        "explicit half_plane width should be honored");
}

static void CastStartedEvent_SnapshotsTargetWorld()
{
    var target = new Vector3(10f, 0f, 20f);
    var ev = new CastStartedEvent(DateTimeOffset.UtcNow, 1, "Boss", 0x1234, "Ground AoE", 4.0f, 2, target);

    Equal(target, ev.TargetWorld!.Value, "cast start should carry target world snapshot");
}

static void ActorTrackedAoe_StaticSnapshotPolicy_UsesCasterOriginOnly()
{
    var sourceWorld = new Vector3(10f, 0f, 20f);

    Equal(sourceWorld, ActorTrackedAoeService.SelectStaticSourceSnapshot(fromCaster: true, sourceWorld),
        "caster-origin AoE should keep cast-start source position");
    Null(ActorTrackedAoeService.SelectStaticSourceSnapshot(fromCaster: false, sourceWorld),
        "target-origin AoE should keep target/live resolution");
    Null(ActorTrackedAoeService.SelectStaticSourceSnapshot(fromCaster: true, null),
        "missing source actor should not invent static position");
}

static void ActorTrackedAoe_RotationConversion_MatchesArenaProjection()
{
    // FFXIV rotation 0 (南) → 描画系 π/2（南）
    NearlyEqual(MathF.PI / 2f, ActorTrackedAoeService.ConvertFfxivRotationToRender(0f), "FFXIV 0 → render π/2 (south)");
    // FFXIV π/2 (西) → 描画系 0
    NearlyEqual(0f, ActorTrackedAoeService.ConvertFfxivRotationToRender(MathF.PI / 2f), "FFXIV π/2 → render 0 (east)");
    // FFXIV π (北) → 描画系 -π/2
    NearlyEqual(-MathF.PI / 2f, ActorTrackedAoeService.ConvertFfxivRotationToRender(MathF.PI), "FFXIV π → render -π/2 (north)");
    // FFXIV -π/2 (東) → 描画系 π
    NearlyEqual(MathF.PI, ActorTrackedAoeService.ConvertFfxivRotationToRender(-MathF.PI / 2f), "FFXIV -π/2 → render π (west)");
    // ArenaProjection.RotationToMapAngleRad と同一であることを保証
    var samples = new[] { 0f, 0.5f, 1.5f, -0.7f, MathF.PI, -MathF.PI };
    foreach (var rot in samples)
    {
        NearlyEqual(
            ArenaProjection.RotationToMapAngleRad(rot),
            ActorTrackedAoeService.ConvertFfxivRotationToRender(rot),
            $"sample rot={rot} matches ArenaProjection");
    }
}

static void RectShape_Contains_RotationMatchesGameFacing()
{
    // RectShape.RotationDeg は FanShape と同じ FFXIV rotation 系（0=南/+Z, 前方=(sinR,cosR)）。
    // Width=x ローカル軸, Depth=z ローカル軸（前方）。
    // 回転0：前方=南(+Z)。前方軸(±Depth/2)上の点は範囲内、幅軸(±Width/2)外は範囲外。
    var rect0 = new RectShape { Center = Vector3.Zero, Width = 2f, Depth = 20f, RotationDeg = 0f };
    True(rect0.Contains(new Vector3(0f, 0f, 8f)), "rot0: forward(south +Z) point inside");
    False(rect0.Contains(new Vector3(8f, 0f, 0f)), "rot0: east point outside (beyond width axis)");

    // 回転45°：前方=南東(+X,+Z)。前方軸上の点(6,6)は奥行内＝範囲内、
    // 直交する幅軸方向(6,-6)は幅±1しかないので範囲外であるべき。
    // 旧実装(rad=-RotationDeg)は鏡像になり left/right が反転し、この2点の内外が逆転する。
    var rect45 = new RectShape { Center = Vector3.Zero, Width = 2f, Depth = 20f, RotationDeg = 45f };
    True(rect45.Contains(new Vector3(6f, 0f, 6f)), "rot45: forward(SE) point inside");
    False(rect45.Contains(new Vector3(6f, 0f, -6f)), "rot45: width-axis(SW) point outside");
}

static void LinePerpendicularPreset_SideNormal_MatchesPlayerLeftRight()
{
    // from→to を向いた人の左右（XZ平面・俯瞰 +X=東/+Z=南、北=上）。
    // 地上の事実：北を向くと右手は東。右=(-dz,0,dx)、左=(dz,0,-dx)。
    var from = Vector3.Zero;

    // to が北(-Z)：右手=東(+X)、左手=西(-X)
    var toNorth = new Vector3(0f, 0f, -10f);
    var rightN = LinePerpendicularPreset.PerpendicularOffset(from, toNorth, "right", 5f);
    var leftN = LinePerpendicularPreset.PerpendicularOffset(from, toNorth, "left", 5f);
    NearlyEqual(5f, rightN.X - toNorth.X, "facing north: right is east (+X)");
    NearlyEqual(0f, rightN.Z - toNorth.Z, "facing north: right has no Z component");
    NearlyEqual(-5f, leftN.X - toNorth.X, "facing north: left is west (-X)");

    // to が東(+X)：右手=南(+Z)、左手=北(-Z)
    var toEast = new Vector3(10f, 0f, 0f);
    var rightE = LinePerpendicularPreset.PerpendicularOffset(from, toEast, "right", 5f);
    var leftE = LinePerpendicularPreset.PerpendicularOffset(from, toEast, "left", 5f);
    NearlyEqual(5f, rightE.Z - toEast.Z, "facing east: right is south (+Z)");
    NearlyEqual(0f, rightE.X - toEast.X, "facing east: right has no X component");
    NearlyEqual(-5f, leftE.Z - toEast.Z, "facing east: left is north (-Z)");
}

static void ActorTrackedAoe_TowardsTarget_FacesCardinalDirection()
{
    // dz > 0（target は南）→ FFXIV rotation 0（南向き）
    NearlyEqual(0f, ActorTrackedAoeService.ComputeTowardsTargetRotation(0f, 5f), "target south → 0");
    // dx > 0（target は東）→ FFXIV rotation π/2（東向き = atan2(dx, dz) で dx=正、dz=0 → π/2）
    NearlyEqual(MathF.PI / 2f, ActorTrackedAoeService.ComputeTowardsTargetRotation(5f, 0f), "target east → π/2");
    // dz < 0（target は北）→ FFXIV rotation π / -π
    var northRot = ActorTrackedAoeService.ComputeTowardsTargetRotation(0f, -5f);
    True(MathF.Abs(MathF.Abs(northRot) - MathF.PI) < 0.001f, $"target north → ±π (got {northRot})");
    // dx < 0（target は西）→ FFXIV rotation -π/2
    NearlyEqual(-MathF.PI / 2f, ActorTrackedAoeService.ComputeTowardsTargetRotation(-5f, 0f), "target west → -π/2");
}

static void InMemoryActionLookup_ExposesActionGeometry()
{
    // IActionLookup の最小契約を検証する純粋な POCO テスト。AoeResolver の overload に
    // 由来する Dalamud assembly ロードを避けるため、AoeResolver.Resolve は直接呼ばない
    // （AoeResolver はテスト project に Dalamud reference が無い構成で呼ぶと
    // 「Dalamud.dll が見つからない」で落ちる）。
    var donutGeom = new ActionGeometry(0x179Cu, "アルゲドン", 7, 6f, 2f, 53u, 0);
    var lookup = new InMemoryActionLookup
    {
        [0x179Cu] = donutGeom,
        [0x67BFu] = new ActionGeometry(0x67BFu, "パラデイグマ", 1, 0f, 0f, 0u, 5000),
    };

    var donut = lookup.TryGet(0x179Cu);
    NotNull(donut, "donut entry resolved");
    Equal(donutGeom, donut!, "donut geometry roundtrip");
    Equal(53u, donut!.OmenId, "omen id propagated");
    Equal(2f, donut.XAxisModifierM, "x axis modifier propagated");

    Null(lookup.TryGet(0xDEADu), "unknown id returns null");

    var all = new List<ActionGeometry>(lookup.ListAll());
    Equal(2, all.Count, "list all yields registered entries");
}

static void BuildSpawnId_DistinguishesObjectNameAndTriggerEvent()
{
    // T1 録画解析（out/aoe-bug-survey-月の底.md §3）で発覚した「spawn_67BF_233C が 3 重複」
    // の再発防止。同 cast_id + 同 data_id でも ObjectName/TriggerEvent が違えば別 ID を返すこと。
    var castStart = PredictedObjectSpawnLearner.BuildSpawnId("0x67BF", 0x233C, "ケツァクウァトル", "cast_start");
    var castComplete = PredictedObjectSpawnLearner.BuildSpawnId("0x67BF", 0x233C, "ケツァクウァトル", "cast_complete");
    NotEqual(castStart, castComplete, "trigger event suffix should differ");
    True(castComplete.EndsWith("_c", StringComparison.Ordinal), "cast_complete suffix is _c");

    var sameName = PredictedObjectSpawnLearner.BuildSpawnId("0x67BF", 0x233C, "ケツァクウァトル", "cast_start");
    Equal(castStart, sameName, "same key yields stable id");

    var differentName = PredictedObjectSpawnLearner.BuildSpawnId("0x67BF", 0x233C, "ゾディアーク", "cast_start");
    NotEqual(castStart, differentName, "different object name yields different id (T1 #spawn_67BF_233C dup)");

    var differentCast = PredictedObjectSpawnLearner.BuildSpawnId("0x67F3", 0x233C, "ケツァクウァトル", "cast_start");
    NotEqual(castStart, differentCast, "different cast id yields different id");

    True(castStart.StartsWith("spawn_67BF_233C_", StringComparison.OrdinalIgnoreCase),
        "id keeps cast_hex_dataIdHex prefix for compat with existing format");
}

static void BuildSpawnId_StableAcrossRuns()
{
    // FNV-1a を使うため、process 間で同一文字列に対し同一 ID を返すこと（永続化される ID 用）。
    // .NET の String.GetHashCode はランダム化されるため使えない。
    var id1 = PredictedObjectSpawnLearner.BuildSpawnId("0x67BF", 0x3834, "ケツァクウァトル", "cast_start");
    var id2 = PredictedObjectSpawnLearner.BuildSpawnId("0x67BF", 0x3834, "ケツァクウァトル", "cast_start");
    Equal(id1, id2, "id is stable for same input");
    True(id1.Length >= "spawn_67BF_3834_XXXX".Length, "id includes 4-hex name hash");
}

static void IsUsableObjectAoePosition_RejectsOutOfArenaPlaceholders()
{
    // 月の底ゾディアーク add の戦闘開始時 placeholder 位置を再現:
    //   actor 位置 = (100, 0, 79), arena center = (100.7, 0, 102.1), arena radius = 20m
    //   distance = sqrt(0.7² + 23.1²) ≈ 23.1m > 20 * 1.5 = 30m… いや 23.1 < 30
    // → 23.1 < 30 なので「アリーナ外」判定にはならない。実テストはより明確な外側で。
    var arenaCenter = new Vector3(100.7f, 0f, 102.1f);
    var placeholderPos = new Vector3(100f, 0f, 50f);  // 中心から ~52m → arena 20m の 2.6 倍

    True(
        AddObjectAoeService.IsUsableObjectAoePosition(placeholderPos, arenaCenter, arenaRadiusM: null),
        "arenaRadiusM=null だと既存ロジック通り valid 扱い");
    False(
        AddObjectAoeService.IsUsableObjectAoePosition(placeholderPos, arenaCenter, arenaRadiusM: 20.0),
        "arenaRadiusM=20 だと明らかにアリーナ外 (52m) を placeholder として除外");

    // 月の底実ケース：(100, 79) は中心 (100.7, 102.1) から ~23m
    var actualMonoBottomPlaceholder = new Vector3(100f, 0f, 79f);
    var actualMonoBottomCenter = new Vector3(100.7f, 0f, 102.1f);
    False(
        AddObjectAoeService.IsUsableObjectAoePosition(actualMonoBottomPlaceholder, actualMonoBottomCenter, arenaRadiusM: 15.0),
        "実 placeholder 位置はアリーナ半径 15m を超える距離なので除外 (15 * 1.5 = 22.5 < 23.1)");
}

static void IsUsableObjectAoePosition_KeepsValidEdgePositions()
{
    // アリーナ端 (radius ぎりぎり) の actor は正常扱いされる
    var arenaCenter = new Vector3(100f, 0f, 100f);
    var edgePos = new Vector3(118f, 0f, 100f);  // 中心から 18m、半径 20m の内側

    True(
        AddObjectAoeService.IsUsableObjectAoePosition(edgePos, arenaCenter, arenaRadiusM: 20.0),
        "アリーナ内の actor は valid");

    // 1.5 倍の係数までは許容（撤退時の予測 actor がアリーナ外でも一部正常）
    var slightlyOutPos = new Vector3(125f, 0f, 100f);  // 中心から 25m、半径 20m の 1.25 倍
    True(
        AddObjectAoeService.IsUsableObjectAoePosition(slightlyOutPos, arenaCenter, arenaRadiusM: 20.0),
        "わずかにアリーナ外 (1.25 倍) は valid 扱い");
}

static void HasPlaceholderActionUsedTarget_DetectsPlaceholder()
{
    // T1 §2 症状 B: 月の底ケラノウス・エイドロン (0x67E1) は target=null かつ
    // target_world=(-0.015,-0.015,-0.015) として記録される。
    var placeholderEv = new ActionUsedEvent(
        Timestamp: DateTimeOffset.UtcNow,
        SourceId: 1073794237,
        SourceName: "ケツァクウァトル",
        ActionId: 0x67E1,
        ActionName: "ケラノウス・エイドロン",
        TargetId: null,
        IsAutoAttack: false,
        TargetWorld: new Vector3(-0.015f, -0.015f, -0.015f));
    True(AutoTelegraphService.HasPlaceholderActionUsedTarget(placeholderEv),
        "-0.015 placeholder は検出される");

    // (0,0,0) もキャッチ
    var zeroEv = placeholderEv with { TargetWorld = new Vector3(0f, 0f, 0f) };
    True(AutoTelegraphService.HasPlaceholderActionUsedTarget(zeroEv),
        "原点 placeholder も検出される");
}

static void PredictedObjectSpawnLearner_SeparatesCastStartAndComplete()
{
    // T1 §2 症状A: 月の底パラデイグマ (cast_id=0x67BF, cast_time=2.7s) で
    // cast_start delay = 14.9s、cast_complete delay = 12.2s が ObsKey 共有のため
    // 平均化されて 13.4s になっていた。
    //
    // ObsKey は private record struct のため直接テストできない。代わりに対外契約
    // (BuildSpawnId が cast_start / cast_complete で別 ID を返す) と、内部で生成される
    // PredictedObjectSpawn.TriggerEvent が key.CastEvent をそのまま使うことを spawn_id
    // 規則のテスト ([BuildSpawnId_Distinguishes...] 既存) と合わせて確認する。
    var castStartId = PredictedObjectSpawnLearner.BuildSpawnId("0x67BF", 14388, "ケツァクウァトル", "cast_start");
    var castCompleteId = PredictedObjectSpawnLearner.BuildSpawnId("0x67BF", 14388, "ケツァクウァトル", "cast_complete");

    NotEqual(castStartId, castCompleteId, "cast_start / cast_complete 別 ID");
    True(castCompleteId.EndsWith("_c", StringComparison.Ordinal), "cast_complete に _c suffix");
    False(castStartId.EndsWith("_c", StringComparison.Ordinal), "cast_start は無印");

    // 通常実装：ObsKey に CastEvent が含まれるため、同 cast の cast_start レコードと
    // cast_complete レコードはマージされず、各々独立した PredictedObjectSpawn になる。
    // 平均化が起きないので「13.4s ≈ (14.9 + 12.2) / 2 のズレ」は構造的に再発不能。
    //
    // 入出力テスト (jsonl → 学習結果) は Tests project が Dalamud.dll を持たないため
    // CreateForTesting() factory 経由でも Lumina 関連の type resolution で落ちる。
    // 別 PR で in-game integration test を整える想定。
}

static void ReconcileObjectAoeRules_UpdatesRecordingSourcedRules()
{
    // T1 §4 優先度1: 月の底 object_aoe_rules で「ケツァクウァトル radius=15, inner=4.5」
    // が学習されているが、predicted_object_spawns では「radius=6, inner=2」。
    // 同 actor で乖離があると実出現時の床描画が誤サイズに。
    // 修正: learn-spawns 実行時に Lumina 由来の predict 値で reconcile。
    var profile = new StrategyProfile
    {
        Id = "default", Name = "test",
        ObjectAoeRules = new List<ObjectAoeRule>
        {
            new()
            {
                Id = "obj_aoe_3834", ObjectName = "ケツァクウァトル",
                DataId = 14388, Shape = "donut", RadiusM = 15.0, InnerRadiusM = 4.5,
                Source = "recording_action", Enabled = true,
            },
        },
    };
    var spawns = new[]
    {
        new PredictedObjectSpawn
        {
            Id = "spawn_test", ObjectName = "ケツァクウァトル", ObjectDataId = 14388,
            Shape = "donut", RadiusM = 6.0, InnerRadiusM = 2.0, Confidence = 0.95,
        },
    };

    var updated = LearnSpawnsCommand.ReconcileObjectAoeRulesFromPredictedSpawns(profile, spawns);

    Equal(1, updated, "1 件 reconcile される");
    NearlyEqual(6.0f, (float)profile.ObjectAoeRules[0].RadiusM,
        "radius が spawn 値 (6m) に更新される");
    NearlyEqual(2.0f, (float)(profile.ObjectAoeRules[0].InnerRadiusM ?? 0),
        "inner が spawn 値 (2m) に更新される");
}

static void ReconcileObjectAoeRules_ProtectsManualAndDictionaryRules()
{
    // ユーザーが手で編集したルール、辞書由来のルールは触らない。
    var profile = new StrategyProfile
    {
        Id = "default", Name = "test",
        ObjectAoeRules = new List<ObjectAoeRule>
        {
            new()
            {
                Id = "manual_rule", ObjectName = "ケツァクウァトル",
                DataId = 14388, Shape = "donut", RadiusM = 12.0, InnerRadiusM = 3.0,
                Source = "manual", Enabled = true,
            },
            new()
            {
                Id = "dict_rule", ObjectName = "ボス", DataId = 1,
                Shape = "circle", RadiusM = 10.0, Source = "dictionary", Enabled = true,
            },
        },
    };
    var spawns = new[]
    {
        new PredictedObjectSpawn
        {
            Id = "s1", ObjectName = "ケツァクウァトル", ObjectDataId = 14388,
            Shape = "donut", RadiusM = 6.0, InnerRadiusM = 2.0, Confidence = 0.95,
        },
        new PredictedObjectSpawn
        {
            Id = "s2", ObjectName = "ボス", ObjectDataId = 1,
            Shape = "circle", RadiusM = 5.0, Confidence = 1.0,
        },
    };

    var updated = LearnSpawnsCommand.ReconcileObjectAoeRulesFromPredictedSpawns(profile, spawns);

    Equal(0, updated, "manual と dictionary は触らないので更新数 0");
    NearlyEqual(12.0f, (float)profile.ObjectAoeRules[0].RadiusM,
        "manual の radius は保護される");
    NearlyEqual(10.0f, (float)profile.ObjectAoeRules[1].RadiusM,
        "dictionary の radius は保護される");
}

static void HasPlaceholderActionUsedTarget_PassesValidTargets()
{
    // target_id があれば placeholder ではない
    var hasTarget = new ActionUsedEvent(
        Timestamp: DateTimeOffset.UtcNow,
        SourceId: 1001, SourceName: "Boss",
        ActionId: 0x1234, ActionName: "Normal",
        TargetId: 2001, IsAutoAttack: false,
        TargetWorld: new Vector3(0f, 0f, 0f));
    False(AutoTelegraphService.HasPlaceholderActionUsedTarget(hasTarget),
        "target_id ありは placeholder と判定しない");

    // target_world が有効座標
    var validWorld = new ActionUsedEvent(
        Timestamp: DateTimeOffset.UtcNow,
        SourceId: 1001, SourceName: "Boss",
        ActionId: 0x1234, ActionName: "Normal",
        TargetId: null, IsAutoAttack: false,
        TargetWorld: new Vector3(95f, 0f, 105f));
    False(AutoTelegraphService.HasPlaceholderActionUsedTarget(validWorld),
        "有効 target_world は placeholder と判定しない");

    // TargetWorld 自体が null
    var noWorld = new ActionUsedEvent(
        Timestamp: DateTimeOffset.UtcNow,
        SourceId: 1001, SourceName: "Boss",
        ActionId: 0x1234, ActionName: "Normal",
        TargetId: null, IsAutoAttack: false,
        TargetWorld: null);
    False(AutoTelegraphService.HasPlaceholderActionUsedTarget(noWorld),
        "TargetWorld null は placeholder と判定しない（snapshot 失敗ケース）");
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}.");
    }
}

static void NotEqual<T>(T notExpected, T actual, string label)
{
    if (EqualityComparer<T>.Default.Equals(notExpected, actual))
    {
        throw new InvalidOperationException($"{label}: expected NOT to equal {notExpected}, got {actual}.");
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

/// <summary>
/// <see cref="IActionLookup"/> の単純 in-memory モック。AoeResolver / 3 サービスを
/// Dalamud / Lumina 抜きでテストできるようにする目的。
/// </summary>
internal sealed class InMemoryActionLookup : IActionLookup
{
    private readonly Dictionary<uint, ActionGeometry> _map = new();

    public ActionGeometry this[uint id]
    {
        set => _map[id] = value;
    }

    public ActionGeometry? TryGet(uint actionId)
        => _map.TryGetValue(actionId, out var g) ? g : null;

    public IEnumerable<ActionGeometry> ListAll() => _map.Values;
}
