namespace Fahrenheit.Mods.Vrr;

internal enum HookFamily
{
    ModuleLifecycle, SimulationClock, RenderSubmissionIdentity, CameraPresentation, ActorPresentation,
    ActorMotion, ActorPhysics, ActorShading, AtelMotion, AtelWaits, EffectVm, ParticleManagers,
    ParticlePresentation, ParticleIntegrators, SpriteClocks, TextureAnimation, TextureGpu, MenuWater,
    UiClocks, InputLatch, DevicePresentation, Media, AssetOverride, Diagnostics, GameState
}

internal enum HookPurpose { NativeTimingCorrection, IndependentPresentation, Observation, AssetMediaSubstitution, ResourceLifecycle }

// These policies describe source registration requirements; they grant no native execution capability.
internal sealed record HookDescriptor(HookSiteId Site, HookFamily Family, HookPurpose Purpose,
    string Group, string ConfigPredicate, string Effect, IReadOnlyList<string> CoreContractIds,
    IReadOnlyList<HookSiteId> Dependencies, string OwnershipDomain, string Owner, bool IsLoopTemplate);

internal static partial class HookSites
{
    internal static HookDescriptor? Describe(HookSiteId site) => site switch
    {
        HookSiteId.AssetOverrideBigFileStreamCheckExists => D(site, HookFamily.AssetOverride, HookPurpose.AssetMediaSubstitution, "AssetOverrideBigFileStreamCheckExists", "AssetOverride && OverridesAvailable", "Substitutes file lookup and stream lifetime for validated replacements.", [], [], "", ""),
        HookSiteId.AssetOverrideBigFileStreamOpenFile => D(site, HookFamily.AssetOverride, HookPurpose.AssetMediaSubstitution, "AssetOverrideBigFileStreamOpenFile", "AssetOverride && OverridesAvailable", "Substitutes file lookup and stream lifetime for validated replacements.", [], [], "", ""),
        HookSiteId.AtelDeadlineSettersAllLevelsTemplate => D(site, HookFamily.AtelMotion, HookPurpose.NativeTimingCorrection, "AtelDeadlineSettersAllLevelsTemplate", "AtelWorkerMotion", "Changes ATEL motion records, deadlines, rates or integration.", [], [], "", "", true),
        HookSiteId.AtelDeadlineSettersOneLevelTemplate => D(site, HookFamily.AtelMotion, HookPurpose.NativeTimingCorrection, "AtelDeadlineSettersOneLevelTemplate", "AtelWorkerMotion", "Changes ATEL motion records, deadlines, rates or integration.", [], [], "", "", true),
        HookSiteId.AtelRateSettersAllLevelsTemplate => D(site, HookFamily.AtelMotion, HookPurpose.NativeTimingCorrection, "AtelRateSettersAllLevelsTemplate", "AtelWorkerMotion", "Changes ATEL motion records, deadlines, rates or integration.", [], [], "", "", true),
        HookSiteId.AtelRateSettersOneLevelTemplate => D(site, HookFamily.AtelMotion, HookPurpose.NativeTimingCorrection, "AtelRateSettersOneLevelTemplate", "AtelWorkerMotion", "Changes ATEL motion records, deadlines, rates or integration.", [], [], "", "", true),
        HookSiteId.AtelWorkerMotionATELMotionRecordDefaults => D(site, HookFamily.AtelMotion, HookPurpose.NativeTimingCorrection, "AtelWorkerMotionATELMotionRecordDefaults", "AtelWorkerMotion", "Changes ATEL motion records, deadlines, rates or integration.", [], [], "", ""),
        HookSiteId.AtelWorkerMotionATELMoveRecordReader => D(site, HookFamily.AtelMotion, HookPurpose.NativeTimingCorrection, "AtelWorkerMotionATELMoveRecordReader", "AtelWorkerMotion", "Changes ATEL motion records, deadlines, rates or integration.", [], [], "", ""),
        HookSiteId.AtelWorkerMotionATELRotationRecordReader => D(site, HookFamily.AtelMotion, HookPurpose.NativeTimingCorrection, "AtelWorkerMotionATELRotationRecordReader", "AtelWorkerMotion", "Changes ATEL motion records, deadlines, rates or integration.", [], [], "", ""),
        HookSiteId.AtelWorkerMotionATELWorkerPositionIntegrator => D(site, HookFamily.AtelMotion, HookPurpose.NativeTimingCorrection, "AtelWorkerMotionATELWorkerPositionIntegrator", "AtelWorkerTranslation", "Changes ATEL motion records, deadlines, rates or integration.", [], [], "", ""),
        HookSiteId.AtelWorkerMotionSetGravity0094h => D(site, HookFamily.AtelMotion, HookPurpose.NativeTimingCorrection, "AtelWorkerMotionSetGravity0094h", "AtelWorkerGravity", "Changes ATEL motion records, deadlines, rates or integration.", [], [], "", ""),
        HookSiteId.Av1AV1MovieDecodingWorker => D(site, HookFamily.Media, HookPurpose.AssetMediaSubstitution, "Av1AV1MovieDecodingWorker", "VideoOverride && ReplacementResourcesAvailable", "Substitutes movie decode, playback or audio resource ownership.", [], [], "", ""),
        HookSiteId.BattleEffectPauseMAG028ParticlePauseSetter => D(site, HookFamily.EffectVm, HookPurpose.NativeTimingCorrection, "BattleEffectPauseMAG028ParticlePauseSetter", "false (no production initializer)", "Controls effect advancement while preserving required draw dispatch.", ["effects.held_walk_scope"], [], "", ""),
        HookSiteId.BattleTOBtlCtrlLimitTimer => D(site, HookFamily.UiClocks, HookPurpose.NativeTimingCorrection, "BattleTOBtlCtrlLimitTimer", "BattleTimers", "Changes UI, movie or screen-sequence advancement.", [], [], "", ""),
        HookSiteId.BuoyancyCharacterJobBarrier => D(site, HookFamily.ActorPhysics, HookPurpose.NativeTimingCorrection, "BuoyancyCharacterJobBarrier", "Buoyancy", "Applies physics correction at the character worker barrier.", [], [], "", ""),
        HookSiteId.ChFacingChSetPitchTarget => D(site, HookFamily.ActorMotion, HookPurpose.NativeTimingCorrection, "ChFacingChSetPitchTarget", "ChPitchTurnRate", "Changes actor motion advancement or frame counters.", [], [], "", ""),
        HookSiteId.ChShadeRampsChSetRampTarget => D(site, HookFamily.ActorShading, HookPurpose.NativeTimingCorrection, "ChShadeRampsChSetRampTarget", "ChShadeRamps", "Changes actor shading ramp durations.", [], [], "", ""),
        HookSiteId.CrossFadeSgDrawFilter => D(site, HookFamily.UiClocks, HookPurpose.NativeTimingCorrection, "CrossFadeSgDrawFilter", "CrossFadeHold", "Changes UI, movie or screen-sequence advancement.", [], [HookSiteId.TimingSgAccSetAlpha], "", ""),
        HookSiteId.DeviceLockFairnessPhyreDeviceLockRelease => D(site, HookFamily.DevicePresentation, HookPurpose.ResourceLifecycle, "DeviceLockFairnessPhyreDeviceLockRelease", "IndependentPresentationEnabled", "Owns presenter, graphics epoch or device resource lifecycle.", [], [], "", ""),
        HookSiteId.DeviceLockFairnessPhyreDeviceLockTelemetry => D(site, HookFamily.DevicePresentation, HookPurpose.ResourceLifecycle, "DeviceLockFairnessPhyreDeviceLockTelemetry", "IndependentPresentationEnabled", "Owns presenter, graphics epoch or device resource lifecycle.", [], [], "", ""),
        HookSiteId.DiagnosticBindingsMainLoop => D(site, HookFamily.Diagnostics, HookPurpose.NativeTimingCorrection, "DiagnosticBindingsMainLoop", "NativePhaseProbe && DiagnosticSession && !IndependentPresentationEnabled", "Runs scheduler loop entry/exit as well as diagnostic observation.", [], [], "main_loop_callback", "diagnostic_loop"),
        HookSiteId.DiagnosticBindingsPadPollObservation => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "DiagnosticBindingsPadPollObservation", "NativePhaseProbe && DiagnosticSession", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.DiagnosticsViewCommit => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "DiagnosticsViewCommit", "NativePhaseProbe && DiagnosticSession", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.DiagnosticsViewportFinalize => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "DiagnosticsViewportFinalize", "NativePhaseProbe && DiagnosticSession", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.DxgiClearState => D(site, HookFamily.DevicePresentation, HookPurpose.ResourceLifecycle, "DxgiClearState", "IndependentPresentationEnabled", "Owns presenter, graphics epoch or device resource lifecycle.", [], [], "", ""),
        HookSiteId.DxgiDraw => D(site, HookFamily.DevicePresentation, HookPurpose.ResourceLifecycle, "DxgiDraw", "IndependentPresentationEnabled && DiagnosticSession && (NativePhaseProbe || EffectProbe)", "Owns presenter, graphics epoch or device resource lifecycle.", [], [], "", ""),
        HookSiteId.DxgiDrawIndexed => D(site, HookFamily.DevicePresentation, HookPurpose.ResourceLifecycle, "DxgiDrawIndexed", "IndependentPresentationEnabled && DiagnosticSession && (NativePhaseProbe || EffectProbe)", "Owns presenter, graphics epoch or device resource lifecycle.", [], [], "", ""),
        HookSiteId.DxgiFullscreen => D(site, HookFamily.DevicePresentation, HookPurpose.ResourceLifecycle, "DxgiFullscreen", "IndependentPresentationEnabled", "Owns presenter, graphics epoch or device resource lifecycle.", [], [], "", ""),
        HookSiteId.DxgiPresent => D(site, HookFamily.DevicePresentation, HookPurpose.ResourceLifecycle, "DxgiPresent", "IndependentPresentationEnabled", "Owns presenter, graphics epoch or device resource lifecycle.", [], [], "", ""),
        HookSiteId.DxgiRelease => D(site, HookFamily.DevicePresentation, HookPurpose.ResourceLifecycle, "DxgiRelease", "IndependentPresentationEnabled", "Owns presenter, graphics epoch or device resource lifecycle.", [], [], "", ""),
        HookSiteId.DxgiResize => D(site, HookFamily.DevicePresentation, HookPurpose.ResourceLifecycle, "DxgiResize", "IndependentPresentationEnabled", "Owns presenter, graphics epoch or device resource lifecycle.", [], [], "", ""),
        HookSiteId.DxgiSetTargets => D(site, HookFamily.DevicePresentation, HookPurpose.ResourceLifecycle, "DxgiSetTargets", "IndependentPresentationEnabled", "Owns presenter, graphics epoch or device resource lifecycle.", [], [], "", ""),
        HookSiteId.EffectsEternalEffectRunAfter => D(site, HookFamily.EffectVm, HookPurpose.NativeTimingCorrection, "EffectsEternalEffectRunAfter", "IndependentPresentationEnabled || (EffectHold && EternalEffectHold)", "Controls effect advancement while preserving required draw dispatch.", ["effects.eternal_mixed"], [], "", ""),
        HookSiteId.EffectsMsEffectProcess => D(site, HookFamily.EffectVm, HookPurpose.NativeTimingCorrection, "EffectsMsEffectProcess", "EffectHold || EffectProbe || IndependentPresentationEnabled", "Controls effect advancement while preserving required draw dispatch.", ["effects.dispatch_advance", "effects.dispatch_draw"], [], "", ""),
        HookSiteId.EternalCalmToDrawEternalCalmCopyRight => D(site, HookFamily.UiClocks, HookPurpose.NativeTimingCorrection, "EternalCalmToDrawEternalCalmCopyRight", "EternalCalmCard", "Changes UI, movie or screen-sequence advancement.", [], [], "", ""),
        HookSiteId.FarplaneDiagnosticsFarplaneBeginScene => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "FarplaneDiagnosticsFarplaneBeginScene", "NativePhaseProbe && DiagnosticSession", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.FarplaneDiagnosticsFarplaneClearColor => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "FarplaneDiagnosticsFarplaneClearColor", "NativePhaseProbe && DiagnosticSession", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.FarplaneDiagnosticsFarplaneEndScene => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "FarplaneDiagnosticsFarplaneEndScene", "NativePhaseProbe && DiagnosticSession", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.FarplaneDiagnosticsFarplaneFmvDraw => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "FarplaneDiagnosticsFarplaneFmvDraw", "NativePhaseProbe && DiagnosticSession", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.FieldParticlePauseFX010CameraShapeDisPosIntegrate => D(site, HookFamily.ParticleManagers, HookPurpose.NativeTimingCorrection, "FieldParticlePauseFX010CameraShapeDisPosIntegrate", "IndependentPresentationEnabled", "Controls particle age, producer, restart or pause advancement.", [], [], "", ""),
        HookSiteId.FieldParticlePauseFX010CameraShapeDisPosSpawn => D(site, HookFamily.ParticleManagers, HookPurpose.NativeTimingCorrection, "FieldParticlePauseFX010CameraShapeDisPosSpawn", "IndependentPresentationEnabled", "Controls particle age, producer, restart or pause advancement.", [], [], "", ""),
        HookSiteId.FieldParticlePauseFX010CameraShapeIntegrate => D(site, HookFamily.ParticleManagers, HookPurpose.NativeTimingCorrection, "FieldParticlePauseFX010CameraShapeIntegrate", "IndependentPresentationEnabled", "Controls particle age, producer, restart or pause advancement.", [], [], "", ""),
        HookSiteId.FieldParticlePauseFX010CameraShapeSpawn => D(site, HookFamily.ParticleManagers, HookPurpose.NativeTimingCorrection, "FieldParticlePauseFX010CameraShapeSpawn", "IndependentPresentationEnabled", "Controls particle age, producer, restart or pause advancement.", [], [], "", ""),
        HookSiteId.FieldParticlePauseFX010KeyedDrawOp => D(site, HookFamily.ParticleManagers, HookPurpose.NativeTimingCorrection, "FieldParticlePauseFX010KeyedDrawOp", "IndependentPresentationEnabled", "Controls particle age, producer, restart or pause advancement.", [], [], "", ""),
        HookSiteId.FieldParticlePresentationFieldParticleProducerDispatcher => D(site, HookFamily.ParticlePresentation, HookPurpose.IndependentPresentation, "FieldParticlePresentationFieldParticleProducerDispatcher", "IndependentPresentationEnabled", "Separates particle drawing from authoritative advancement.", [], [], "", ""),
        HookSiteId.FieldParticleRestartPppFpLoop => D(site, HookFamily.ParticleManagers, HookPurpose.NativeTimingCorrection, "FieldParticleRestartPppFpLoop", "FieldParticleRestartHold || IndependentPresentationEnabled", "Controls particle age, producer, restart or pause advancement.", [], [], "", ""),
        HookSiteId.FmvAudioMovieAudioScopeStop => D(site, HookFamily.Media, HookPurpose.AssetMediaSubstitution, "FmvAudioMovieAudioScopeStop", "VideoOverride && ReplacementResourcesAvailable", "Substitutes movie decode, playback or audio resource ownership.", [], [], "", ""),
        HookSiteId.FmvAudioMovieAudioTrackType => D(site, HookFamily.Media, HookPurpose.AssetMediaSubstitution, "FmvAudioMovieAudioTrackType", "VideoOverride && ReplacementResourcesAvailable", "Substitutes movie decode, playback or audio resource ownership.", [], [], "", ""),
        HookSiteId.FrameSequencesBattleEntryBlur => D(site, HookFamily.UiClocks, HookPurpose.NativeTimingCorrection, "FrameSequencesBattleEntryBlur", "BattleIntroBlur", "Changes UI, movie or screen-sequence advancement.", [], [], "", ""),
        HookSiteId.FrameSequencesGraphicDrawMainMenuWaterEffect => D(site, HookFamily.UiClocks, HookPurpose.NativeTimingCorrection, "FrameSequencesGraphicDrawMainMenuWaterEffect", "MenuWater", "Changes UI, movie or screen-sequence advancement.", [], [], "", ""),
        HookSiteId.FrameSequencesGraphicTextureVideoUpdate => D(site, HookFamily.UiClocks, HookPurpose.NativeTimingCorrection, "FrameSequencesGraphicTextureVideoUpdate", "TextureVideo", "Changes UI, movie or screen-sequence advancement.", [], [], "", ""),
        HookSiteId.FrameSequencesGraphicVideoUpdate => D(site, HookFamily.UiClocks, HookPurpose.NativeTimingCorrection, "FrameSequencesGraphicVideoUpdate", "Fmv", "Changes UI, movie or screen-sequence advancement.", [], [], "", ""),
        HookSiteId.FrameSequencesOldFormatTextureAnimationAdvance => D(site, HookFamily.UiClocks, HookPurpose.NativeTimingCorrection, "FrameSequencesOldFormatTextureAnimationAdvance", "TextureAnimation", "Changes UI, movie or screen-sequence advancement.", [], [], "", ""),
        HookSiteId.GameOverGameoverVfxDataUpdate => D(site, HookFamily.GameState, HookPurpose.Observation, "GameOverGameoverVfxDataUpdate", "Diagnostics && EffectProbe", "Observes game-over VFX inputs, results and submissions while retaining bounded capture state.", [], [], "", ""),
        HookSiteId.GameOverGameoverVfxUpdate => D(site, HookFamily.GameState, HookPurpose.Observation, "GameOverGameoverVfxUpdate", "Diagnostics && EffectProbe", "Observes game-over VFX inputs, results and submissions while retaining bounded capture state.", [], [], "", ""),
        HookSiteId.GraphicsLifetimeGraphicsFinalizeObservation => D(site, HookFamily.DevicePresentation, HookPurpose.ResourceLifecycle, "independent.graphics", "IndependentPresentationEnabled", "Owns presenter, graphics epoch or device resource lifecycle.", ["graphics.lifecycle_epoch"], [], "", ""),
        HookSiteId.GraphicsLifetimeGraphicsInitializeObservation => D(site, HookFamily.DevicePresentation, HookPurpose.ResourceLifecycle, "independent.graphics", "IndependentPresentationEnabled", "Owns presenter, graphics epoch or device resource lifecycle.", ["graphics.lifecycle_epoch"], [], "", ""),
        HookSiteId.GraphicsLifetimeGraphicsResetObservation => D(site, HookFamily.DevicePresentation, HookPurpose.ResourceLifecycle, "independent.graphics", "IndependentPresentationEnabled", "Owns presenter, graphics epoch or device resource lifecycle.", ["graphics.lifecycle_epoch"], [], "", ""),
        HookSiteId.GraphicsLifetimeOrdinaryRenderCompletion => D(site, HookFamily.DevicePresentation, HookPurpose.ResourceLifecycle, "independent.graphics", "IndependentPresentationEnabled", "Owns presenter, graphics epoch or device resource lifecycle.", ["presentation.ordinary_completion"], [], "", ""),
        HookSiteId.IdleSwayChIdleSway => D(site, HookFamily.ActorMotion, HookPurpose.NativeTimingCorrection, "IdleSwayChIdleSway", "IdleSway", "Changes actor motion advancement or frame counters.", [], [], "", ""),
        HookSiteId.LensSpriteKeLnsShpUpdate => D(site, HookFamily.SpriteClocks, HookPurpose.NativeTimingCorrection, "LensSpriteKeLnsShpUpdate", "LensSpriteClock", "Tracks sprite phase and particle object lifecycle.", [], [], "", ""),
        HookSiteId.MenuWaterDiagnosticsWaterAssetLoad => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "MenuWaterDiagnosticsWaterAssetLoad", "NativePhaseProbe && DiagnosticSession", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.MenuWaterDiagnosticsWaterClusterGet => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "MenuWaterDiagnosticsWaterClusterGet", "NativePhaseProbe && DiagnosticSession", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.MenuWaterDiagnosticsWaterClusterLoad => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "MenuWaterDiagnosticsWaterClusterLoad", "NativePhaseProbe && DiagnosticSession", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.MenuWaterDiagnosticsWaterShaderTexture => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "MenuWaterDiagnosticsWaterShaderTexture", "NativePhaseProbe && DiagnosticSession", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.MenuWaterDiagnosticsWaterTextureIterator => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "MenuWaterDiagnosticsWaterTextureIterator", "NativePhaseProbe && DiagnosticSession", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.ModuleStartupCookieInitialization => D(site, HookFamily.ModuleLifecycle, HookPurpose.ResourceLifecycle, "module.startup", "Present", "Verifies owned continuations and publishes prepared patches before CRT startup continues.", ["clock.pass_scope"], [], "startup_patch_publication", "module"),
        HookSiteId.ModuleFFXApplicationAnimate => D(site, HookFamily.ModuleLifecycle, HookPurpose.NativeTimingCorrection, "ModuleFFXApplicationAnimate", "Present", "Controls native frame pacing and vsync arguments.", [], [], "", ""),
        HookSiteId.ModuleSetFlipVSyncInterval => D(site, HookFamily.ModuleLifecycle, HookPurpose.NativeTimingCorrection, "ModuleSetFlipVSyncInterval", "KeepVsyncInterval", "Controls native frame pacing and vsync arguments.", [], [], "", ""),
        HookSiteId.MotionSequenceChSeqFrame => D(site, HookFamily.ActorMotion, HookPurpose.NativeTimingCorrection, "MotionSequenceChSeqFrame", "MotionSequenceCounters", "Changes actor motion advancement or frame counters.", [], [], "", ""),
        HookSiteId.MotionSequenceMotionCrossFade => D(site, HookFamily.ActorMotion, HookPurpose.NativeTimingCorrection, "MotionSequenceMotionCrossFade", "MotionSequenceCounters", "Changes actor motion advancement or frame counters.", [], [], "", ""),
        HookSiteId.MotionSurveyMotionAdvance => D(site, HookFamily.Diagnostics, HookPurpose.NativeTimingCorrection, "MotionSurveyMotionAdvance", "MotionSurvey || MotionAdvanceLendFlag || MotionHoldOptedOut", "May lend flags or hold motion advancement in addition to observation.", [], [], "", ""),
        HookSiteId.NeckTrackingChNeckTracking => D(site, HookFamily.ActorMotion, HookPurpose.NativeTimingCorrection, "NeckTrackingChNeckTracking", "NeckTracking", "Changes actor motion advancement or frame counters.", [], [], "", ""),
        HookSiteId.PadLatchPadLatch => D(site, HookFamily.InputLatch, HookPurpose.IndependentPresentation, "independent.pad_history", "IndependentPresentationEnabled", "Controls publication and edge visibility during owned passes.", ["clock.pass_scope"], [], "", ""),
        HookSiteId.PadLatchPressedHistory => D(site, HookFamily.InputLatch, HookPurpose.IndependentPresentation, "independent.pad_history", "IndependentPresentationEnabled", "Controls publication and edge visibility during owned passes.", ["clock.pass_scope"], [], "", ""),
        HookSiteId.PadLatchReleasedHistory => D(site, HookFamily.InputLatch, HookPurpose.IndependentPresentation, "independent.pad_history", "IndependentPresentationEnabled", "Controls publication and edge visibility during owned passes.", ["clock.pass_scope"], [], "", ""),
        HookSiteId.PadLatchRepeatHistory => D(site, HookFamily.InputLatch, HookPurpose.IndependentPresentation, "independent.pad_history", "IndependentPresentationEnabled", "Controls publication and edge visibility during owned passes.", ["clock.pass_scope"], [], "", ""),
        HookSiteId.PadLatchRepeat2History => D(site, HookFamily.InputLatch, HookPurpose.IndependentPresentation, "independent.pad_history", "IndependentPresentationEnabled", "Controls publication and edge visibility during owned passes.", ["clock.pass_scope"], [], "", ""),
        HookSiteId.PadLatchMenuCacheUpdate => D(site, HookFamily.InputLatch, HookPurpose.IndependentPresentation, "independent.pad_history", "IndependentPresentationEnabled", "Controls publication and edge visibility during owned passes.", ["clock.pass_scope"], [], "", ""),
        HookSiteId.PadLatchMenuPressed => D(site, HookFamily.InputLatch, HookPurpose.IndependentPresentation, "independent.pad_history", "IndependentPresentationEnabled", "Controls publication and edge visibility during owned passes.", ["clock.pass_scope"], [], "", ""),
        HookSiteId.PadLatchMenuRepeat => D(site, HookFamily.InputLatch, HookPurpose.IndependentPresentation, "independent.pad_history", "IndependentPresentationEnabled", "Controls publication and edge visibility during owned passes.", ["clock.pass_scope"], [], "", ""),
        HookSiteId.ParticlePresentationKernelTemplate => D(site, HookFamily.ParticlePresentation, HookPurpose.IndependentPresentation, "ParticlePresentationKernelTemplate", "IndependentPresentationEnabled", "Separates particle drawing from authoritative advancement.", [], [], "", "", true),
        HookSiteId.ParticlePresentationParticlePresentationDD => D(site, HookFamily.ParticlePresentation, HookPurpose.IndependentPresentation, "ParticlePresentationParticlePresentationDD", "IndependentPresentationEnabled", "Separates particle drawing from authoritative advancement.", [], [], "", ""),
        HookSiteId.ParticlePresentationParticlePresentationWorker => D(site, HookFamily.ParticlePresentation, HookPurpose.IndependentPresentation, "ParticlePresentationParticlePresentationWorker", "IndependentPresentationEnabled", "Separates particle drawing from authoritative advancement.", [], [], "", ""),
        HookSiteId.ParticleSpriteClockParticleManagerFree => D(site, HookFamily.SpriteClocks, HookPurpose.ResourceLifecycle, "ParticleSpriteClockParticleManagerFree", "ParticleSpriteClock || IndependentPresentationEnabled", "Invalidates particle histories and tracks object/manager lifecycle.", [], [], "", ""),
        HookSiteId.ParticleSpriteClockParticleObjectCreation => D(site, HookFamily.SpriteClocks, HookPurpose.ResourceLifecycle, "ParticleSpriteClockParticleObjectCreation", "ParticleSpriteClock || IndependentPresentationEnabled", "Invalidates particle histories and tracks object/manager lifecycle.", [], [], "", ""),
        HookSiteId.ParticleSpriteClockParticleObjectDeletion => D(site, HookFamily.SpriteClocks, HookPurpose.ResourceLifecycle, "ParticleSpriteClockParticleObjectDeletion", "ParticleSpriteClock || IndependentPresentationEnabled", "Invalidates particle histories and tracks object/manager lifecycle.", [], [], "", ""),
        HookSiteId.ParticleSpriteClockParticleObjectLoopReset => D(site, HookFamily.SpriteClocks, HookPurpose.ResourceLifecycle, "ParticleSpriteClockParticleObjectLoopReset", "ParticleSpriteClock || IndependentPresentationEnabled", "Invalidates particle histories and tracks object/manager lifecycle.", [], [], "", ""),
        HookSiteId.ParticleSpriteClockParticleShapeSpriteClock => D(site, HookFamily.SpriteClocks, HookPurpose.NativeTimingCorrection, "ParticleSpriteClockParticleShapeSpriteClock", "ParticleSpriteClock", "Tracks sprite phase and particle object lifecycle.", [], [], "", ""),
        HookSiteId.ParticleSpriteClockParticleShapeXSpriteClock => D(site, HookFamily.SpriteClocks, HookPurpose.NativeTimingCorrection, "ParticleSpriteClockParticleShapeXSpriteClock", "ParticleSpriteClock", "Tracks sprite phase and particle object lifecycle.", [], [], "", ""),
        HookSiteId.ParticleStepKernelTemplate => D(site, HookFamily.ParticleIntegrators, HookPurpose.NativeTimingCorrection, "ParticleStepKernelTemplate", "ParticleKernelHold || ParticleIntegratorScale", "Changes particle integrator or UV clock advancement.", [], [], "", "", true),
        HookSiteId.ParticleUvPppDrawMdlTs2UVClock => D(site, HookFamily.ParticleIntegrators, HookPurpose.NativeTimingCorrection, "ParticleUvPppDrawMdlTs2UVClock", "ParticleUvClock", "Changes particle integrator or UV clock advancement.", [], [], "", ""),
        HookSiteId.ParticleUvPppDrawMdlTs3UVClock => D(site, HookFamily.ParticleIntegrators, HookPurpose.NativeTimingCorrection, "ParticleUvPppDrawMdlTs3UVClock", "ParticleUvClock", "Changes particle integrator or UV clock advancement.", [], [], "", ""),
        HookSiteId.ParticlesPppPartLoop => D(site, HookFamily.ParticleManagers, HookPurpose.NativeTimingCorrection, "ParticlesPppPartLoop", "ParticleTimeline", "Controls particle age, producer, restart or pause advancement.", [], [], "", ""),
        HookSiteId.ParticlesPppRunPart => D(site, HookFamily.ParticleManagers, HookPurpose.NativeTimingCorrection, "ParticlesPppRunPart", "ParticleTimeline || IndependentPresentationEnabled || ParticleSpriteClock", "Controls particle age, producer, restart or pause advancement.", [], [], "", ""),
        HookSiteId.ParticlesPppRunPartFp => D(site, HookFamily.ParticleManagers, HookPurpose.NativeTimingCorrection, "ParticlesPppRunPartFp", "ParticleTimeline", "Controls particle age, producer, restart or pause advancement.", [], [], "", ""),
        HookSiteId.ParticlesPppStartPart => D(site, HookFamily.ParticleManagers, HookPurpose.NativeTimingCorrection, "ParticlesPppStartPart", "ParticleTimeline || IndependentPresentationEnabled || ParticleSpriteClock", "Controls particle age, producer, restart or pause advancement.", [], [], "", ""),
        HookSiteId.PresenterD3D11CreateDeviceAndSwapChain => D(site, HookFamily.DevicePresentation, HookPurpose.ResourceLifecycle, "PresenterD3D11CreateDeviceAndSwapChain", "IndependentPresentationEnabled", "Owns presenter, graphics epoch or device resource lifecycle.", [], [], "", ""),
        HookSiteId.RenderActorsActorModelWorldGroup => D(site, HookFamily.ActorPresentation, HookPurpose.IndependentPresentation, "independent.actors", "IndependentPresentationEnabled", "Captures actor completion and temporarily substitutes render state.", ["actors.capture_completion"], [], "", ""),
        HookSiteId.RenderActorsActorRendererWorldInstance => D(site, HookFamily.ActorPresentation, HookPurpose.IndependentPresentation, "independent.actors", "IndependentPresentationEnabled", "Captures actor completion and temporarily substitutes render state.", ["actors.capture_completion"], [], "", ""),
        HookSiteId.RenderActorsActorWorldGroup => D(site, HookFamily.ActorPresentation, HookPurpose.IndependentPresentation, "independent.actors", "IndependentPresentationEnabled", "Captures actor completion and temporarily substitutes render state.", ["actors.capture_completion"], [], "", ""),
        HookSiteId.RenderCameraRenderCameraFinalize => D(site, HookFamily.CameraPresentation, HookPurpose.IndependentPresentation, "RenderCameraRenderCameraFinalize", "IndependentPresentationEnabled", "Captures and temporarily substitutes camera presentation state.", [], [], "", ""),
        HookSiteId.RenderImagesPApplicationFrame => D(site, HookFamily.RenderSubmissionIdentity, HookPurpose.IndependentPresentation, "independent.images", "IndependentPresentationEnabled", "Associates submission identities with image lifetimes.", ["presentation.image_identity"], [], "", ""),
        HookSiteId.RenderImagesRenderFlipAction => D(site, HookFamily.RenderSubmissionIdentity, HookPurpose.IndependentPresentation, "independent.images", "IndependentPresentationEnabled", "Associates submission identities with image lifetimes.", ["presentation.image_identity"], [], "", ""),
        HookSiteId.RenderImagesRenderFlipSlot => D(site, HookFamily.RenderSubmissionIdentity, HookPurpose.IndependentPresentation, "independent.images", "IndependentPresentationEnabled", "Associates submission identities with image lifetimes.", ["presentation.image_identity"], [], "", ""),
        HookSiteId.RenderImagesRendererFlipIdentity => D(site, HookFamily.RenderSubmissionIdentity, HookPurpose.IndependentPresentation, "independent.images", "IndependentPresentationEnabled", "Associates submission identities with image lifetimes.", ["presentation.image_identity"], [], "", ""),
        HookSiteId.RenderSchedulerRenderCalculationRate => D(site, HookFamily.SimulationClock, HookPurpose.IndependentPresentation, "independent.scheduler", "IndependentPresentationEnabled", "Changes native timing arguments or owns pass clock callbacks.", ["clock.calculation_rate"], [], "", ""),
        HookSiteId.RenderSchedulerRenderExecCount => D(site, HookFamily.SimulationClock, HookPurpose.IndependentPresentation, "independent.scheduler", "IndependentPresentationEnabled", "Changes native timing arguments or owns pass clock callbacks.", ["clock.effective_exec"], [], "", ""),
        HookSiteId.RenderSchedulerRenderMainLoop => D(site, HookFamily.SimulationClock, HookPurpose.IndependentPresentation, "independent.scheduler", "IndependentPresentationEnabled", "Changes native timing arguments or owns pass clock callbacks.", ["clock.pass_scope"], [], "main_loop_callback", "independent_scheduler"),
        HookSiteId.RenderSchedulerRenderParticleEmission => D(site, HookFamily.SimulationClock, HookPurpose.IndependentPresentation, "independent.scheduler", "IndependentPresentationEnabled", "Changes native timing arguments or owns pass clock callbacks.", ["particle.emission_gate"], [], "", ""),
        HookSiteId.SurveyChTextureAnimSetEnable => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "SurveyChTextureAnimSetEnable", "SurveyTextureAnimation", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.SurveyChrTexanimDraw => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "SurveyChrTexanimDraw", "SurveyTextureAnimation", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.SurveyScreenTextureAnimationDraw => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "SurveyScreenTextureAnimationDraw", "SurveyTextureAnimation", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.SurveySetMaterialUVScroll => D(site, HookFamily.Diagnostics, HookPurpose.Observation, "SurveySetMaterialUVScroll", "SurveyTextureAnimation", "Observes native calls and retains bounded diagnostic state.", [], [], "", ""),
        HookSiteId.TextureAnimationNewFormatCharacterTextureAdvance => D(site, HookFamily.TextureAnimation, HookPurpose.NativeTimingCorrection, "TextureAnimationNewFormatCharacterTextureAdvance", "TextureAnimationStep", "Tracks texture lifetimes and changes presentation or frame advancement.", [], [], "", ""),
        HookSiteId.TextureBlendProbeResolve => D(site, HookFamily.TextureGpu, HookPurpose.ResourceLifecycle, "TextureBlendProbeResolve", "TextureBlendProbe", "Owns texture drawing, GPU resources or device generation.", [], [], "", ""),
        HookSiteId.TextureDeviceTextureEngineDeviceObserver => D(site, HookFamily.TextureGpu, HookPurpose.ResourceLifecycle, "TextureDeviceTextureEngineDeviceObserver", "TextureBlendProbe || TextureFrameInterpolation", "Owns texture drawing, GPU resources or device generation.", [], [], "", ""),
        HookSiteId.TextureDrawIndexed => D(site, HookFamily.TextureGpu, HookPurpose.ResourceLifecycle, "TextureDrawIndexed", "TextureFrameInterpolation || TextureBlendProbe", "Owns texture drawing, GPU resources or device generation.", [], [], "", ""),
        HookSiteId.TextureDrawPlain => D(site, HookFamily.TextureGpu, HookPurpose.ResourceLifecycle, "TextureDrawPlain", "TextureFrameInterpolation || TextureBlendProbe", "Owns texture drawing, GPU resources or device generation.", [], [], "", ""),
        HookSiteId.TextureFrameInterpolationTextureFrameInterpolation => D(site, HookFamily.TextureAnimation, HookPurpose.NativeTimingCorrection, "TextureFrameInterpolationTextureFrameInterpolation", "TextureFrameInterpolation", "Tracks texture lifetimes and changes presentation or frame advancement.", [], [], "", ""),
        HookSiteId.TexturePresentationTextureFinalImage => D(site, HookFamily.TextureAnimation, HookPurpose.NativeTimingCorrection, "TexturePresentationTextureFinalImage", "TextureAnimationStep && (IndependentPresentationEnabled || TextureFrameInterpolation)", "Tracks texture lifetimes and changes presentation or frame advancement.", [], [], "", ""),
        HookSiteId.TexturePresentationTextureSlotLifetime => D(site, HookFamily.TextureAnimation, HookPurpose.NativeTimingCorrection, "TexturePresentationTextureSlotLifetime", "TextureAnimationStep && (IndependentPresentationEnabled || TextureFrameInterpolation)", "Tracks texture lifetimes and changes presentation or frame advancement.", [], [], "", ""),
        HookSiteId.TexturePresentationTextureUVOutput => D(site, HookFamily.TextureAnimation, HookPurpose.NativeTimingCorrection, "TexturePresentationTextureUVOutput", "TextureAnimationStep && (IndependentPresentationEnabled)", "Tracks texture lifetimes and changes presentation or frame advancement.", [], [], "", ""),
        HookSiteId.TexturePresentationTextureWorkLifetime => D(site, HookFamily.TextureAnimation, HookPurpose.NativeTimingCorrection, "TexturePresentationTextureWorkLifetime", "TextureAnimationStep && (IndependentPresentationEnabled || TextureFrameInterpolation)", "Tracks texture lifetimes and changes presentation or frame advancement.", [], [], "", ""),
        HookSiteId.TimingATELWaitExec => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "native.atel_waits", "AtelWaits", "Changes native timing arguments or owns pass clock callbacks.", [], [], "", ""),
        HookSiteId.TimingATELWaitInit => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "native.atel_waits", "AtelWaits", "Changes native timing arguments or owns pass clock callbacks.", [], [], "", ""),
        HookSiteId.TimingChCalcMain => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "TimingChCalcMain", "CharacterDelta || Buoyancy || BinaryDiagnostics", "Changes native timing arguments or owns pass clock callbacks.", [], [], "", ""),
        HookSiteId.TimingChSetMotionSpeed => D(site, HookFamily.SimulationClock, HookPurpose.Observation, "TimingChSetMotionSpeed", "Motion", "Counts motion speed calls; forwards speed without persistent correction.", [], [], "", ""),
        HookSiteId.TimingChTextureSetAnimTimer => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "TimingChTextureSetAnimTimer", "Motion", "Changes native timing arguments or owns pass clock callbacks.", [], [], "", ""),
        HookSiteId.TimingISyncGetData => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "TimingISyncGetData", "SyncDataDisable", "Changes native timing arguments or owns pass clock callbacks.", [], [], "", ""),
        HookSiteId.TimingMovieFrameDelta => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "native.atel_waits", "AtelWaits", "Changes native timing arguments or owns pass clock callbacks.", [], [], "", ""),
        HookSiteId.TimingMsCameraMoveAcc => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "native.camera", "Camera", "Changes native timing arguments or owns pass clock callbacks.", [], [], "", ""),
        HookSiteId.TimingMsCameraMoveFrame => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "native.camera", "Camera", "Changes native timing arguments or owns pass clock callbacks.", [], [], "", ""),
        HookSiteId.TimingMsEffectSetSpeed => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "TimingMsEffectSetSpeed", "Motion", "Changes native timing arguments or owns pass clock callbacks.", [], [], "", ""),
        HookSiteId.TimingMsSetChrMotionParamF => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "TimingMsSetChrMotionParamF", "Motion", "Changes native timing arguments or owns pass clock callbacks.", [], [], "", ""),
        HookSiteId.TimingSgAccSetAlpha => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "TimingSgAccSetAlpha", "Fades || CrossFadeHold", "Changes native timing arguments or owns pass clock callbacks.", [], [], "", ""),
        HookSiteId.TimingSgFadeCommon => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "native.fades", "Fades", "Changes native timing arguments or owns pass clock callbacks.", [], [], "", ""),
        HookSiteId.TimingSgFlash => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "native.fades", "Fades", "Changes native timing arguments or owns pass clock callbacks.", [], [], "", ""),
        HookSiteId.TimingTkSetFadeOut => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "native.fades", "Fades", "Changes native timing arguments or owns pass clock callbacks.", [], [], "", ""),
        HookSiteId.TimingVerticalBlankCounters => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "TimingVerticalBlankCounters", "VBlankRate", "Changes native timing arguments or owns pass clock callbacks.", ["clock.simulation_step"], [], "", ""),
        HookSiteId.TimingYiAnimInfoInit => D(site, HookFamily.SimulationClock, HookPurpose.NativeTimingCorrection, "TimingYiAnimInfoInit", "EventClockOnVblank", "Changes native timing arguments or owns pass clock callbacks.", [], [], "", ""),
        HookSiteId.UpstreamUiUI014MinimapDraw => D(site, HookFamily.UiClocks, HookPurpose.NativeTimingCorrection, "UpstreamUiUI014MinimapDraw", "IndependentPresentationEnabled", "Changes UI, movie or screen-sequence advancement.", [], [], "", ""),
        HookSiteId.UpstreamUiUI020SaveLoadListInput => D(site, HookFamily.UiClocks, HookPurpose.NativeTimingCorrection, "UpstreamUiUI020SaveLoadListInput", "IndependentPresentationEnabled", "Changes UI, movie or screen-sequence advancement.", [], [], "", ""),
        HookSiteId.UpstreamUiUIBTLL1PartySwapEdge => D(site, HookFamily.UiClocks, HookPurpose.NativeTimingCorrection, "UpstreamUiUIBTLL1PartySwapEdge", "IndependentPresentationEnabled", "Changes UI, movie or screen-sequence advancement.", [], [], "", ""),
        HookSiteId.UpstreamUiUISPH001SphereGridUpdate => D(site, HookFamily.UiClocks, HookPurpose.NativeTimingCorrection, "UpstreamUiUISPH001SphereGridUpdate", "IndependentPresentationEnabled", "Changes UI, movie or screen-sequence advancement.", [], [], "", ""),
        HookSiteId.VideoOverridePhyFMVPlayerManagerInitialize => D(site, HookFamily.Media, HookPurpose.AssetMediaSubstitution, "VideoOverridePhyFMVPlayerManagerInitialize", "VideoOverride && ReplacementResourcesAvailable", "Substitutes movie decode, playback or audio resource ownership.", [], [], "", ""),
        HookSiteId.VideoOverridePhyFMVPlayerManagerPlayStart => D(site, HookFamily.Media, HookPurpose.AssetMediaSubstitution, "VideoOverridePhyFMVPlayerManagerPlayStart", "VideoOverride && ReplacementResourcesAvailable", "Substitutes movie decode, playback or audio resource ownership.", [], [], "", ""),
        HookSiteId.PadSnapshotProducer => D(site, HookFamily.InputLatch, HookPurpose.IndependentPresentation, "independent.pad_snapshot", "IndependentPresentationEnabled", "Publishes a simulation-owned pad snapshot independently of diagnostic capture.", ["clock.pass_scope", "clock.effective_exec"], [], "", ""),
        _ => null
    };

    private static HookDescriptor D(HookSiteId site, HookFamily family, HookPurpose purpose, string group,
        string config, string effect, string[] contracts, HookSiteId[] dependencies, string domain, string owner, bool template = false)
        => new(site, family, purpose, group, config, effect, Array.AsReadOnly(contracts),
            Array.AsReadOnly(dependencies), domain, owner, template);
}


internal sealed record HookSelection(HookSiteId Site, bool Enabled = true, string Owner = "module", int? ExpectedInstances = null,
    bool Required = true, bool Deferred = false, IReadOnlyList<nint>? ExpectedTargets = null);

// GroupComplete means registration was queued; the stock hook API does not report native commit.
internal enum HookRegistrationState { Preflighted, Queued, GroupComplete, Refused, Disposed }

internal sealed partial class HookRegistrationPlan : IDisposable
{
    private readonly Dictionary<HookSiteId, HookSelection> _selected;
    private readonly HashSet<(HookSiteId Site, nint Target, long Generation)> _queued = [];
    private readonly HashSet<(HookSiteId Site, nint Target, long Generation)> _attempted = [];
    private readonly object _gate = new();
    private sealed record ContinuationReceipt(Delegate Callback, Func<bool> Probe);
    private readonly Dictionary<(HookSiteId Site, nint Target, long Generation), ContinuationReceipt> _continuations = [];
    private (HookSiteId Site, nint Target, long Generation)? _currentAttempt;
    private ContinuationReceipt? _pendingContinuation;
    private int _installationThread;
    private bool _verifyingContinuations;

    internal HookRegistrationState State { get; private set; } = HookRegistrationState.Preflighted;
    internal string? RefusalReason { get; private set; }

    private HookRegistrationPlan(Dictionary<HookSiteId, HookSelection> selected) => _selected = selected;

    internal static bool TryCreate(IEnumerable<HookSelection> selections, IEnumerable<string> availableContracts,
        out HookRegistrationPlan? plan, out string? error, IEnumerable<string>? requiredGroups = null)
    {
        plan = null;
        error = null;
        var selected = new Dictionary<HookSiteId, HookSelection>();
        foreach (HookSelection requested in selections)
        {
            HookSelection selection = requested with { ExpectedTargets = requested.ExpectedTargets is null
                ? null : Array.AsReadOnly(requested.ExpectedTargets.ToArray()) };
            if (HookSites.Describe(selection.Site) is null)
            { error = $"Unknown hook site {selection.Site}."; return false; }
            if (!selected.TryAdd(selection.Site, selection))
            { error = $"Duplicate hook selection {selection.Site}."; return false; }
            if (selection.ExpectedTargets is { } targets && (targets.Count == 0 || targets.Any(t => t == 0)
                || targets.Distinct().Count() != targets.Count
                || selection.ExpectedInstances is { } count && count != targets.Count))
            { error = $"Invalid expected target inventory for {selection.Site}."; return false; }
            if (selection.ExpectedInstances is <= 0)
            { error = $"Invalid expected instance count for {selection.Site}."; return false; }
            if (string.IsNullOrWhiteSpace(selection.Owner))
            { error = $"Missing owner for {selection.Site}."; return false; }
        }
        HookDescriptor[] active = selected.Values.Where(s => s.Enabled).Select(s => HookSites.Describe(s.Site)!).ToArray();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (HookDescriptor descriptor in active)
        {
            if (descriptor.OwnershipDomain.Length == 0) continue;
            string owner = selected[descriptor.Site].Owner + ":" + descriptor.Owner;
            if (owners.TryGetValue(descriptor.OwnershipDomain, out string? previous) && previous != owner)
            { error = $"Conflicting owner of {descriptor.OwnershipDomain}."; return false; }
            owners[descriptor.OwnershipDomain] = owner;
        }
        var contracts = new HashSet<string>(availableContracts, StringComparer.Ordinal);
        foreach (HookDescriptor descriptor in active)
        {
            foreach (string contract in descriptor.CoreContractIds)
                if (!contracts.Contains(contract))
                { error = $"Missing contract {contract} for {descriptor.Site}."; return false; }
            foreach (HookSiteId dependency in descriptor.Dependencies)
                if (!selected.TryGetValue(dependency, out HookSelection? dependencySelection) || !dependencySelection.Enabled)
                { error = $"Missing dependency {dependency} for {descriptor.Site}."; return false; }
        }
        HookDescriptor[] manifest = Enum.GetValues<HookSiteId>().Where(HookSites.IsDeclared)
            .Select(s => HookSites.Describe(s)!).ToArray();
        var groups = new HashSet<string>(active.Select(d => d.Group), StringComparer.Ordinal);
        if (requiredGroups is not null) groups.UnionWith(requiredGroups);
        foreach (string group in groups)
        {
            HookDescriptor[] members = manifest.Where(d => d.Group == group).ToArray();
            if (members.Length == 0 || members.Any(d => !selected.TryGetValue(d.Site, out HookSelection? s) || !s.Enabled))
            { error = $"Incomplete required group {group}."; return false; }
        }
        plan = new HookRegistrationPlan(selected);
        return true;
    }

    internal bool Install(HookSiteId site, Func<bool> install) => Install(site, 0, 0, install);

    internal bool Install(HookSiteId site, nint target, long generation, Func<bool> install)
    {
        lock (_gate) return InstallCore(site, target, generation, install);
    }

    private bool InstallCore(HookSiteId site, nint target, long generation, Func<bool> install)
    {
        if (State is HookRegistrationState.Refused or HookRegistrationState.Disposed) return false;
        if (_currentAttempt is not null || _verifyingContinuations)
            return Refuse("Hook registration cannot reenter registration or continuation verification.");
        if (!_selected.TryGetValue(site, out HookSelection? selection) || !selection.Enabled
            || _attempted.Contains((site, target, generation)) || generation < 0
            || _queued.Any(q => q.Site == site && q.Generation != generation)
            || (HookSites.Describe(site)!.IsLoopTemplate && target == 0)
            || (selection.ExpectedTargets is { } targets && !targets.Contains(target))
            || (ExpectedCount(selection) is { } expected && _queued.Count(q => q.Site == site) >= expected))
            return Refuse($"Unselected, disabled, duplicate or invalid hook instance {site}.");
        var instance = (site, target, generation);
        _attempted.Add(instance);
        _currentAttempt = instance;
        System.Threading.Volatile.Write(ref _installationThread, Environment.CurrentManagedThreadId);
        try
        {
            if (!install()) return InstallationFailed(selection, $"Hook registration failed for {site}.");
            if (State is HookRegistrationState.Refused or HookRegistrationState.Disposed) return false;
            _queued.Add(instance);
            if (_pendingContinuation is not null) _continuations.Add(instance, _pendingContinuation);
            State = _selected.Values.Where(s => s.Enabled).All(HasExpectedInstances)
                ? HookRegistrationState.GroupComplete : HookRegistrationState.Queued;
            return true;
        }
        catch
        {
            InstallationFailed(selection, $"Hook registration threw for {site}.");
            throw;
        }
        finally
        {
            System.Threading.Volatile.Write(ref _installationThread, 0);
            _currentAttempt = null;
            _pendingContinuation = null;
        }
    }

    /// <summary>Records the exact callback and its lookup probe for the current synchronous installer.</summary>
    /// <remarks>Call before native insertion; only a successful queue attempt retains the receipt.</remarks>
    internal bool RecordContinuation(Delegate callback, Func<bool> probe)
    {
        if (System.Threading.Volatile.Read(ref _installationThread) != Environment.CurrentManagedThreadId) return false;
        lock (_gate)
        {
            if (_currentAttempt is null || State is HookRegistrationState.Refused or HookRegistrationState.Disposed) return false;
            if (callback is null || probe is null) return Refuse("Continuation evidence requires an exact callback and probe.");
            // The SDK keys owned next links by delegate equality, including equal separate delegate objects.
            if (_pendingContinuation is not null || _continuations.Values.Any(r => r.Callback.Equals(callback)))
                return Refuse("Duplicate continuation callback or receipt.");
            _pendingContinuation = new(callback, probe);
            return true;
        }
    }

    /// <summary>Rechecks required, nondeferred continuations without granting runtime activation.</summary>
    /// <remarks>Probes must only look up their recorded callback, never invoke it or register hooks.</remarks>
    internal bool TryVerifyRequiredContinuations(out string? reason)
    {
        lock (_gate)
        {
            reason = null;
            if (State is HookRegistrationState.Refused or HookRegistrationState.Disposed)
            { reason = RefusalReason ?? $"Hook registration is {State}."; return false; }
            if (_currentAttempt is not null)
            { reason = "Cannot verify continuations during hook registration."; return false; }
            if (_verifyingContinuations)
            {
                reason = "Continuation verification cannot reenter itself.";
                return Refuse(reason);
            }
            HookSelection[] required = _selected.Values.Where(s => s.Enabled && s.Required && !s.Deferred).ToArray();
            if (required.Length == 0)
            { reason = "No required startup continuations were selected."; return false; }
            if (!required.All(HasExpectedInstances))
            { reason = "Required hook registration is incomplete."; return false; }
            _verifyingContinuations = true;
            try
            {
                foreach (var instance in _queued.Where(q => _selected[q.Site].Required && !_selected[q.Site].Deferred)
                    .OrderBy(q => q.Site).ThenBy(q => q.Target).ThenBy(q => q.Generation))
                {
                    string identity = $"{instance.Site} at 0x{instance.Target:X}, generation {instance.Generation}";
                    if (!_continuations.TryGetValue(instance, out ContinuationReceipt? receipt))
                    { reason = $"Missing owned continuation for {identity}."; return false; }
                    bool available;
                    try { available = receipt.Probe(); }
                    catch (Exception error)
                    { reason = $"Continuation probe for {identity} threw {error.GetType().Name}: {error.Message}"; return false; }
                    if (State is HookRegistrationState.Refused or HookRegistrationState.Disposed)
                    { reason = RefusalReason ?? $"Hook registration became {State} during continuation verification."; return false; }
                    if (!available)
                    { reason = $"Owned continuation is unavailable for {identity}."; return false; }
                }
                return true;
            }
            finally { _verifyingContinuations = false; }
        }
    }


    internal bool IsGroupComplete(string group)
    {
        lock (_gate) return IsGroupCompleteCore(group);
    }

    private bool IsGroupCompleteCore(string group)
    {
        if (State is HookRegistrationState.Refused or HookRegistrationState.Disposed) return false;
        HookSiteId[] members = _selected.Values.Where(s => s.Enabled && HookSites.Describe(s.Site)!.Group == group)
            .Select(s => s.Site).ToArray();
        return members.Length > 0 && members.All(site => HasExpectedInstances(_selected[site]));
    }

    private static int? ExpectedCount(HookSelection selection)
        => selection.ExpectedTargets?.Count ?? selection.ExpectedInstances
            ?? (selection.Deferred || HookSites.Describe(selection.Site)!.IsLoopTemplate ? null : 1);

    private bool HasExpectedInstances(HookSelection selection)
        => ExpectedCount(selection) is { } expected && _queued.Count(q => q.Site == selection.Site) == expected;

    private bool InstallationFailed(HookSelection selection, string reason)
        => selection.Required && !selection.Deferred ? Refuse(reason) : false;

    private bool Refuse(string reason)
    {
        RefusalReason ??= reason;
        if (State != HookRegistrationState.Disposed) State = HookRegistrationState.Refused;
        return false;
    }

    public void Dispose() { lock (_gate) State = HookRegistrationState.Disposed; }
}


internal sealed partial class HookRegistrationPlan
{
    // A source contract inventory is not a live capability inventory or permission to arm.
    private static readonly string[] SourceContracts =
    [
        "clock.pass_scope", "clock.effective_exec", "clock.calculation_rate", "particle.emission_gate",
        "clock.native_owner_handover", "clock.simulation_step", "effects.held_walk_scope",
        "effects.dispatch_draw", "effects.dispatch_advance", "effects.eternal_mixed",
        "presentation.image_identity", "actors.capture_completion", "graphics.lifecycle_epoch",
        "presentation.ordinary_completion"
    ];

    internal bool IsSelected(HookSiteId site) => _selected.TryGetValue(site, out var selection) && selection.Enabled;

    internal bool RequiredRegistrationsComplete
    {
        get
        {
            lock (_gate) return State is not (HookRegistrationState.Refused or HookRegistrationState.Disposed)
                && _selected.Values.Where(s => s.Enabled && s.Required && !s.Deferred).All(HasExpectedInstances);
        }
    }

    internal static bool TryCreateResolved(VrrConfig config, bool independent, bool diagnostics, bool binaryDiagnostics,
        IReadOnlyDictionary<HookSiteId, IReadOnlyList<nint>>? templateTargets,
        out HookRegistrationPlan? plan, out string? error)
    {
        plan = null;
        error = null;
        if (!config.Present)
        { error = "Hook registration requires Present."; return false; }
        if (independent && (!config.VBlankRate || !config.CharacterDelta || !config.ParticleTimeline))
        { error = "Independent registration requires VBlankRate, CharacterDelta and ParticleTimeline."; return false; }
        var selections = new List<HookSelection>();
        foreach (HookSiteId site in Enum.GetValues<HookSiteId>().Where(HookSites.IsDeclared))
        {
            HookSelection selection = ResolveSelection(site, config, independent, diagnostics, binaryDiagnostics, templateTargets);
            if (selection.Enabled && HookSites.Describe(site)!.IsLoopTemplate
                && (selection.ExpectedTargets is null || selection.ExpectedTargets.Count == 0))
            { error = $"Missing template target inventory for {site}."; return false; }
            selections.Add(selection);
        }
        return TryCreate(selections, SourceContracts, out plan, out error);
    }

    internal static HookSelection ResolveSelection(HookSiteId site, VrrConfig config, bool independent,
        bool diagnostics, bool binaryDiagnostics,
        IReadOnlyDictionary<HookSiteId, IReadOnlyList<nint>>? templateTargets = null)
    {
        bool enabled = ConfigSelects(site, config, independent, diagnostics, binaryDiagnostics);
        HookDescriptor descriptor = HookSites.Describe(site)!;
        IReadOnlyList<nint>? targets = null;
        templateTargets?.TryGetValue(site, out targets);
        bool deferred = descriptor.Purpose is HookPurpose.Observation or HookPurpose.AssetMediaSubstitution
            || site is HookSiteId.TextureBlendProbeResolve
                or HookSiteId.DeviceLockFairnessPhyreDeviceLockRelease
                or HookSiteId.DeviceLockFairnessPhyreDeviceLockTelemetry
                or HookSiteId.UpstreamUiUI014MinimapDraw or HookSiteId.UpstreamUiUI020SaveLoadListInput
                or HookSiteId.UpstreamUiUIBTLL1PartySwapEdge or HookSiteId.UpstreamUiUISPH001SphereGridUpdate
            || descriptor.Family == HookFamily.Diagnostics && site != HookSiteId.MotionSurveyMotionAdvance
            || site.ToString().StartsWith("FieldParticlePause", StringComparison.Ordinal)
            || site.ToString().StartsWith("Dxgi", StringComparison.Ordinal)
            || site is HookSiteId.TextureDrawIndexed or HookSiteId.TextureDrawPlain
            || site == HookSiteId.TimingChCalcMain && !config.CharacterDelta && !config.Buoyancy;
        if (site == HookSiteId.TimingChSetMotionSpeed) deferred = false;
        return new(site, enabled, Required: !deferred, Deferred: deferred, ExpectedTargets: targets);
    }

    internal static IReadOnlyList<string> AvailableSourceContracts { get; } = Array.AsReadOnly(SourceContracts);


    private static bool ConfigSelects(HookSiteId site, VrrConfig config, bool independent, bool diagnostics, bool binaryDiagnostics)
        => site switch
        {
            HookSiteId.AssetOverrideBigFileStreamCheckExists => config.AssetOverride,
            HookSiteId.AssetOverrideBigFileStreamOpenFile => config.AssetOverride,
            HookSiteId.AtelDeadlineSettersAllLevelsTemplate => config.AtelWorkerMotion,
            HookSiteId.AtelDeadlineSettersOneLevelTemplate => config.AtelWorkerMotion,
            HookSiteId.AtelRateSettersAllLevelsTemplate => config.AtelWorkerMotion,
            HookSiteId.AtelRateSettersOneLevelTemplate => config.AtelWorkerMotion,
            HookSiteId.AtelWorkerMotionATELMotionRecordDefaults => config.AtelWorkerMotion,
            HookSiteId.AtelWorkerMotionATELMoveRecordReader => config.AtelWorkerMotion,
            HookSiteId.AtelWorkerMotionATELRotationRecordReader => config.AtelWorkerMotion,
            HookSiteId.AtelWorkerMotionATELWorkerPositionIntegrator => config.AtelWorkerTranslation,
            HookSiteId.AtelWorkerMotionSetGravity0094h => config.AtelWorkerGravity,
            HookSiteId.Av1AV1MovieDecodingWorker => config.VideoOverride,
            HookSiteId.BattleEffectPauseMAG028ParticlePauseSetter => false,
            HookSiteId.BattleTOBtlCtrlLimitTimer => config.BattleTimers,
            HookSiteId.BuoyancyCharacterJobBarrier => config.Buoyancy,
            HookSiteId.ChFacingChSetPitchTarget => config.ChPitchTurnRate,
            HookSiteId.ChShadeRampsChSetRampTarget => config.ChShadeRamps,
            HookSiteId.CrossFadeSgDrawFilter => config.CrossFadeHold,
            HookSiteId.DeviceLockFairnessPhyreDeviceLockRelease => independent,
            HookSiteId.DeviceLockFairnessPhyreDeviceLockTelemetry => independent,
            HookSiteId.DiagnosticBindingsMainLoop => config.NativePhaseProbe && diagnostics && !independent,
            HookSiteId.DiagnosticBindingsPadPollObservation => config.NativePhaseProbe && diagnostics,
            HookSiteId.DiagnosticsViewCommit => config.NativePhaseProbe && diagnostics,
            HookSiteId.DiagnosticsViewportFinalize => config.NativePhaseProbe && diagnostics,
            HookSiteId.DxgiClearState => independent,
            HookSiteId.DxgiDraw => independent && diagnostics && (config.NativePhaseProbe || config.EffectProbe),
            HookSiteId.DxgiDrawIndexed => independent && diagnostics && (config.NativePhaseProbe || config.EffectProbe),
            HookSiteId.DxgiFullscreen => independent,
            HookSiteId.DxgiPresent => independent,
            HookSiteId.DxgiRelease => independent,
            HookSiteId.DxgiResize => independent,
            HookSiteId.DxgiSetTargets => independent,
            HookSiteId.EffectsEternalEffectRunAfter => independent || (config.EffectHold && config.EternalEffectHold),
            HookSiteId.EffectsMsEffectProcess => config.EffectHold || config.EffectProbe || independent,
            HookSiteId.EternalCalmToDrawEternalCalmCopyRight => config.EternalCalmCard,
            HookSiteId.FarplaneDiagnosticsFarplaneBeginScene => config.NativePhaseProbe && diagnostics,
            HookSiteId.FarplaneDiagnosticsFarplaneClearColor => config.NativePhaseProbe && diagnostics,
            HookSiteId.FarplaneDiagnosticsFarplaneEndScene => config.NativePhaseProbe && diagnostics,
            HookSiteId.FarplaneDiagnosticsFarplaneFmvDraw => config.NativePhaseProbe && diagnostics,
            HookSiteId.FieldParticlePauseFX010CameraShapeDisPosIntegrate => independent,
            HookSiteId.FieldParticlePauseFX010CameraShapeDisPosSpawn => independent,
            HookSiteId.FieldParticlePauseFX010CameraShapeIntegrate => independent,
            HookSiteId.FieldParticlePauseFX010CameraShapeSpawn => independent,
            HookSiteId.FieldParticlePauseFX010KeyedDrawOp => independent,
            HookSiteId.FieldParticlePresentationFieldParticleProducerDispatcher => independent,
            HookSiteId.FieldParticleRestartPppFpLoop => config.FieldParticleRestartHold || independent,
            HookSiteId.FmvAudioMovieAudioScopeStop => config.VideoOverride,
            HookSiteId.FmvAudioMovieAudioTrackType => config.VideoOverride,
            HookSiteId.FrameSequencesBattleEntryBlur => config.BattleIntroBlur,
            HookSiteId.FrameSequencesGraphicDrawMainMenuWaterEffect => config.MenuWater,
            HookSiteId.FrameSequencesGraphicTextureVideoUpdate => config.TextureVideo,
            HookSiteId.FrameSequencesGraphicVideoUpdate => config.Fmv,
            HookSiteId.FrameSequencesOldFormatTextureAnimationAdvance => config.TextureAnimation,
            HookSiteId.GameOverGameoverVfxDataUpdate => config.Diagnostics && config.EffectProbe,
            HookSiteId.GameOverGameoverVfxUpdate => config.Diagnostics && config.EffectProbe,
            HookSiteId.GraphicsLifetimeGraphicsFinalizeObservation => independent,
            HookSiteId.GraphicsLifetimeGraphicsInitializeObservation => independent,
            HookSiteId.GraphicsLifetimeGraphicsResetObservation => independent,
            HookSiteId.GraphicsLifetimeOrdinaryRenderCompletion => independent,
            HookSiteId.IdleSwayChIdleSway => config.IdleSway,
            HookSiteId.LensSpriteKeLnsShpUpdate => config.LensSpriteClock,
            HookSiteId.MenuWaterDiagnosticsWaterAssetLoad => config.NativePhaseProbe && diagnostics,
            HookSiteId.MenuWaterDiagnosticsWaterClusterGet => config.NativePhaseProbe && diagnostics,
            HookSiteId.MenuWaterDiagnosticsWaterClusterLoad => config.NativePhaseProbe && diagnostics,
            HookSiteId.MenuWaterDiagnosticsWaterShaderTexture => config.NativePhaseProbe && diagnostics,
            HookSiteId.MenuWaterDiagnosticsWaterTextureIterator => config.NativePhaseProbe && diagnostics,
            HookSiteId.ModuleStartupCookieInitialization => config.Present,
            HookSiteId.ModuleFFXApplicationAnimate => config.Present,
            HookSiteId.ModuleSetFlipVSyncInterval => config.KeepVsyncInterval,
            HookSiteId.MotionSequenceChSeqFrame => config.MotionSequenceCounters,
            HookSiteId.MotionSequenceMotionCrossFade => config.MotionSequenceCounters,
            HookSiteId.MotionSurveyMotionAdvance => config.MotionSurvey || config.MotionAdvanceLendFlag || config.MotionHoldOptedOut,
            HookSiteId.NeckTrackingChNeckTracking => config.NeckTracking,
            HookSiteId.PadLatchPadLatch => independent,
            HookSiteId.PadLatchPressedHistory => independent,
            HookSiteId.PadLatchReleasedHistory => independent,
            HookSiteId.PadLatchRepeatHistory => independent,
            HookSiteId.PadLatchRepeat2History => independent,
            HookSiteId.PadLatchMenuCacheUpdate => independent,
            HookSiteId.PadLatchMenuPressed => independent,
            HookSiteId.PadLatchMenuRepeat => independent,
            HookSiteId.ParticlePresentationKernelTemplate => independent,
            HookSiteId.ParticlePresentationParticlePresentationDD => independent,
            HookSiteId.ParticlePresentationParticlePresentationWorker => independent,
            HookSiteId.ParticleSpriteClockParticleManagerFree => config.ParticleSpriteClock || independent,
            HookSiteId.ParticleSpriteClockParticleObjectCreation => config.ParticleSpriteClock || independent,
            HookSiteId.ParticleSpriteClockParticleObjectDeletion => config.ParticleSpriteClock || independent,
            HookSiteId.ParticleSpriteClockParticleObjectLoopReset => config.ParticleSpriteClock || independent,
            HookSiteId.ParticleSpriteClockParticleShapeSpriteClock => config.ParticleSpriteClock,
            HookSiteId.ParticleSpriteClockParticleShapeXSpriteClock => config.ParticleSpriteClock,
            HookSiteId.ParticleStepKernelTemplate => config.ParticleKernelHold || config.ParticleIntegratorScale,
            HookSiteId.ParticleUvPppDrawMdlTs2UVClock => config.ParticleUvClock,
            HookSiteId.ParticleUvPppDrawMdlTs3UVClock => config.ParticleUvClock,
            HookSiteId.ParticlesPppPartLoop => config.ParticleTimeline,
            HookSiteId.ParticlesPppRunPart => config.ParticleTimeline || independent || config.ParticleSpriteClock,
            HookSiteId.ParticlesPppRunPartFp => config.ParticleTimeline,
            HookSiteId.ParticlesPppStartPart => config.ParticleTimeline || independent || config.ParticleSpriteClock,
            HookSiteId.PresenterD3D11CreateDeviceAndSwapChain => independent,
            HookSiteId.RenderActorsActorModelWorldGroup => independent,
            HookSiteId.RenderActorsActorRendererWorldInstance => independent,
            HookSiteId.RenderActorsActorWorldGroup => independent,
            HookSiteId.RenderCameraRenderCameraFinalize => independent,
            HookSiteId.RenderImagesPApplicationFrame => independent,
            HookSiteId.RenderImagesRenderFlipAction => independent,
            HookSiteId.RenderImagesRenderFlipSlot => independent,
            HookSiteId.RenderImagesRendererFlipIdentity => independent,
            HookSiteId.RenderSchedulerRenderCalculationRate => independent,
            HookSiteId.RenderSchedulerRenderExecCount => independent,
            HookSiteId.RenderSchedulerRenderMainLoop => independent,
            HookSiteId.RenderSchedulerRenderParticleEmission => independent,
            HookSiteId.SurveyChTextureAnimSetEnable => config.SurveyTextureAnimation,
            HookSiteId.SurveyChrTexanimDraw => config.SurveyTextureAnimation,
            HookSiteId.SurveyScreenTextureAnimationDraw => config.SurveyTextureAnimation,
            HookSiteId.SurveySetMaterialUVScroll => config.SurveyTextureAnimation,
            HookSiteId.TextureAnimationNewFormatCharacterTextureAdvance => config.TextureAnimationStep,
            HookSiteId.TextureBlendProbeResolve => config.TextureBlendProbe,
            HookSiteId.TextureDeviceTextureEngineDeviceObserver => config.TextureBlendProbe || config.TextureFrameInterpolation,
            HookSiteId.TextureDrawIndexed => config.TextureFrameInterpolation || config.TextureBlendProbe,
            HookSiteId.TextureDrawPlain => config.TextureFrameInterpolation || config.TextureBlendProbe,
            HookSiteId.TextureFrameInterpolationTextureFrameInterpolation => config.TextureFrameInterpolation,
            HookSiteId.TexturePresentationTextureFinalImage => config.TextureAnimationStep && (independent || config.TextureFrameInterpolation),
            HookSiteId.TexturePresentationTextureSlotLifetime => config.TextureAnimationStep && (independent || config.TextureFrameInterpolation),
            HookSiteId.TexturePresentationTextureUVOutput => config.TextureAnimationStep && (independent),
            HookSiteId.TexturePresentationTextureWorkLifetime => config.TextureAnimationStep && (independent || config.TextureFrameInterpolation),
            HookSiteId.TimingATELWaitExec => config.AtelWaits,
            HookSiteId.TimingATELWaitInit => config.AtelWaits,
            HookSiteId.TimingChCalcMain => config.CharacterDelta || config.Buoyancy || binaryDiagnostics,
            HookSiteId.TimingChSetMotionSpeed => config.Motion,
            HookSiteId.TimingChTextureSetAnimTimer => config.Motion,
            HookSiteId.TimingISyncGetData => config.SyncDataDisable,
            HookSiteId.TimingMovieFrameDelta => config.AtelWaits,
            HookSiteId.TimingMsCameraMoveAcc => config.Camera,
            HookSiteId.TimingMsCameraMoveFrame => config.Camera,
            HookSiteId.TimingMsEffectSetSpeed => config.Motion,
            HookSiteId.TimingMsSetChrMotionParamF => config.Motion,
            HookSiteId.TimingSgAccSetAlpha => config.Fades || config.CrossFadeHold,
            HookSiteId.TimingSgFadeCommon => config.Fades,
            HookSiteId.TimingSgFlash => config.Fades,
            HookSiteId.TimingTkSetFadeOut => config.Fades,
            HookSiteId.TimingVerticalBlankCounters => config.VBlankRate,
            HookSiteId.TimingYiAnimInfoInit => config.EventClockOnVblank,
            HookSiteId.UpstreamUiUI014MinimapDraw => independent,
            HookSiteId.UpstreamUiUI020SaveLoadListInput => independent,
            HookSiteId.UpstreamUiUIBTLL1PartySwapEdge => independent,
            HookSiteId.UpstreamUiUISPH001SphereGridUpdate => independent,
            HookSiteId.VideoOverridePhyFMVPlayerManagerInitialize => config.VideoOverride,
            HookSiteId.VideoOverridePhyFMVPlayerManagerPlayStart => config.VideoOverride,
            HookSiteId.PadSnapshotProducer => independent,
            _ => throw new ArgumentOutOfRangeException(nameof(site))
        };
}
