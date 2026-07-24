namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization
    {
        private const int ParsedAnimPayloadCacheLimit = 1024;
        private const double ClientInterpolationAlpha = 0.72;
        /// <summary>Coalesce rapid client hit MOBEVENT spam; keep small so multi-hit weapons still report.</summary>
        private const double ClientAnimSpeedEpsilon = 0.05;
        /// <summary>Frames to wait before re-forcing the same boss anim group after a forced play(); prevents fighting the local animation state machine at frame rate (which pins the anim to frame 0).</summary>
        private const double ClientBossAnimForceReplayCooldownFrames = 20.0;
        /// <summary>Upper clamp for anim speed carried in payloads; a corrupt value must not fast-forward frames unbounded.</summary>
        private const double MaxClientAnimPayloadSpeed = 10.0;
        private static readonly bool ClientSyncVerticalPosition = false;
        private const double ClientTurnSnapDeltaPx = 2.0;
        private const double MobStatePositionEpsilon = 0.25;
        private const double HostMobStateMidPositionEpsilon = 1.20;
        private const double HostMobStateDormantPositionEpsilon = 6.00;
        private const double MobFallbackMinimumScoreGap = 4.0;
        /// <summary>Client-side recovery radius for an authoritative snapshot whose sync-id mapping was lost.</summary>
        private const double ClientStateRebindMaxDistancePx = 24.0 * 48.0;
        /// <summary>Nearest same-type candidate must beat the next candidate by this many pixels before rebinding.</summary>
        private const double ClientStateRebindMinimumGapPx = 12.0;
        /// <summary>Cooldown in frames between logging ambiguous fallback rejections to prevent log spam.</summary>
        private const int AmbiguousFallbackLogCooldownFrames = 60;
        /// <summary>Client/host mobs can drift while fighting. Direct sync-id hits are allowed within this many pixels instead of requiring exact coordinates.</summary>
        private const double MobHitTrustedSyncIdDistancePx = 24.0 * 64.0;
        /// <summary>Host-side fallback radius for client kill reports whose sync id was pruned locally while the mob is still alive.</summary>
        private const double MobHitMissingSyncIdRebindDistancePx = 24.0 * 48.0;
        /// <summary>How often the host catch-up pass covers remaining tracked mobs.</summary>
        private const double HostAuthoritativeFullResyncIntervalFrames = 45.0;
        /// <summary>Reliable keyframe interval for mobs actively fighting or visible to either player.</summary>
        private const double HostActiveReliableKeyframeIntervalFrames = 8.0;
        /// <summary>Bosses get a tighter reliable state cadence without increasing traffic for every normal mob.</summary>
        private const double HostBossReliableKeyframeIntervalFrames = 3.0;
        /// <summary>After a level registry rebuild, resend MOBREG + catch-up a few times for late-loading clients.</summary>
        private const int HostAuthoritativeBootstrapResyncCount = 6;
        private const double HostAuthoritativeBootstrapResyncIntervalFrames = 5.0;
        /// <summary>Max mobs included in one catch-up pass (byte budget still applies).</summary>
        private const int HostPriorityResyncCatchUpBudgetPerFlush = 48;
        /// <summary>Keep a short host-side tombstone for dead mobs so clients that miss the one death packet still clean up 0-HP ghosts.</summary>
        private const int HostAuthoritativeDeathTombstoneResendCount = 18;
        private const double HostAuthoritativeDeathTombstoneResendIntervalFrames = 8.0;
        private const double HostAuthoritativeDeathTombstoneRetainFrames = 30.0 * 20.0;
        /// <summary>After this many frames a locally culled 0-HP client mob is hidden if vanilla never wakes it for a safe onDie.</summary>
        private const double ClientPendingCulledDeathHideFrames = 30.0 * 6.0;
        /// <summary>Distance where the client stops smoothing and snaps to the host to avoid drawing enemies through walls.</summary>
        private const double ClientAuthoritativeHardSnapDistancePx = 24.0 * 10.0;
        /// <summary>Clamp host velocity prediction so reduced-rate packets cannot overshoot through walls on clients.</summary>
        private const double ClientMaxPredictionFrames = 4.0;
        /// <summary>Ground mobs use only a short local-time prediction window so corrections feel responsive without skipping collision.</summary>
        private const double ClientGroundedMaxPredictionFrames = 1.5;
        /// <summary>Ground mobs are corrected in small X steps. Direct large setPos snaps cut through solid tiles and corners.</summary>
        private const double ClientGroundedBaseCorrectionPxPerFrame = 2.25;
        private const double ClientGroundedMaxCorrectionPxPerFrame = 4.50;
        private const double ClientGroundedMaxDirectCorrectionDistancePx = 24.0 * 8.0;
        /// <summary>Large grounded drift (elite teleports/charges or a missed packet) must still converge instead of freezing forever.</summary>
        private const double ClientGroundedLargeDriftCorrectionPxPerFrame = 10.0;
        private const double ClientGroundedEmergencyDriftCorrectionPxPerFrame = 16.0;
        private const double ClientGroundedEmergencyDriftDistancePx = 24.0 * 20.0;
        private const double ClientGroundedMaxVerticalMismatchPx = 18.0;
        /// <summary>Unlabelled discontinuities are teleports only when horizontal motion dominates.</summary>
        private const double ClientTeleportHorizontalSnapDistancePx = 24.0 * 4.0;
        private const double ClientTeleportUnlabelledMaxVerticalDeltaPx = 24.0 * 3.0;
        private const double ClientTeleportHorizontalDominanceRatio = 1.25;
        private const double ClientTeleportAnimSnapDistancePx = 24.0;
        private const double ClientTeleportVerticalSnapMinimumDeltaPx = 24.0;
        private const double ClientTeleportVerticalSnapMaxHostVerticalSpeedPx = 3.0;
        /// <summary>
        /// Any single unlabelled discontinuity this large (in either axis) is treated as a
        /// teleport even when it is not horizontally dominant. High-BC blink enemies warp
        /// diagonally/vertically and some carry no teleport anim token; without this backstop
        /// their jump lands the client in the ordinary-interpolation path and drifts for the
        /// whole encounter. Kept well above any real single-frame run/fall delta.
        /// </summary>
        private const double ClientTeleportUnlabelledMagnitudeSnapDistancePx = 24.0 * 9.0;
        /// <summary>Place gravity mobs slightly above the authoritative landing point and let vanilla collision finish the landing.</summary>
        private const double ClientGroundedSafeLandingLiftPx = 18.0;
        /// <summary>Upward-only safety recovery for a gravity mob that fell below the host's landed position.</summary>
        private const double ClientGroundedBelowHostRecoveryDistancePx = 16.0;
        private const double ClientGroundedBelowHostRecoveryMaxHorizontalDriftPx = 24.0 * 6.0;
        private const double ClientGroundedBelowHostRecoveryMaxHostVerticalSpeedPx = 1.5;
        /// <summary>Jumping chase attacks keep vanilla motion but receive bounded host phase/velocity alignment.</summary>
        private const double ClientJumpPhaseUpwardCorrectionPxPerFrame = 6.0;
        private const double ClientJumpPhaseBelowHostRecoveryPx = 24.0;
        private const double ClientJumpVelocityEpsilon = 0.03;
        private const double ClientJumpVelocityMaxRawMagnitude = 4.0;
        private const double ClientAiAuthorityLockDurationSeconds = 99999.0;
        private const double HostBossIntroReadyBarrierLockSeconds = 0.5;
        /// <summary>Boss replicas converge much more tightly than ordinary grounded mobs.</summary>
        private const double ClientBossHardSnapDistancePx = 24.0 * 3.0;
        private const double ClientBossMinimumInterpolationAlpha = 0.82;
        private const double ClientBossMaxPredictionFrames = 1.5;
        private const double ClientBossVisualVelocityMaxRawMagnitude = 12.0;
        private const double AuthoritativeAffectPresenceSeconds = 99999.0;
        private const double PixelsPerCase = 24.0;
        private const double ClientNetworkAttackMinActiveFrames = 20.0;
        /// <summary>
        /// A malformed or boss-specific native skill must not leave the client replica autonomous
        /// forever.  Twenty seconds is long enough for multi-stage attacks, leaps and projectile
        /// cleanup, after which only a still queued/charging skill may retain the visual lease.
        /// </summary>
        private const double ClientBossVisualAttackMaxActiveFrames = 30.0 * 20.0;
        /// <summary>Retry an early boss attack briefly while a native phase/proxy replacement finishes registering.</summary>
        private const double ClientPendingBossAttackRetryFrames = 30.0 * 1.5;
        private const int ClientPendingBossAttackLimit = 64;
        private const string ContactAttackPacketSkillId = "@contact";
        private const string OldSkillPreparePacketPrefix = "@oldprep:";
        private const string OldSkillChargeCompletePacketPrefix = "@oldcc:";
        private const string OldSkillExecutePacketPrefix = "@oldexec:";
        private const string NewSkillExecutePacketPrefix = "@newexec:";
        private const int MobWirePacketByteBudget = 4096;
        private static double GetClientInterpolationAlpha()
        {
            var configured = MultiplayerSettingsStorage.MobsInterpolationQuality;
            if (double.IsNaN(configured) || double.IsInfinity(configured))
                return ClientInterpolationAlpha;

            return System.Math.Clamp(configured, 0.20, 1.00);
        }

        /// <summary>User toggle (and dev flag); combined per-mob with <c>!hasGravity</c> for client mob Y sync.</summary>
        private static bool IsClientVerticalSyncEnabled()
        {
            return ClientSyncVerticalPosition || MultiplayerSettingsStorage.SyncVerticalPosition;
        }
    }
}
