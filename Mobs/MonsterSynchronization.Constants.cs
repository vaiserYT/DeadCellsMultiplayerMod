namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization
    {
        private const int ParsedAnimPayloadCacheLimit = 1024;
        private const double ClientInterpolationAlpha = 0.70;
        /// <summary>Coalesce rapid client hit MOBEVENT spam; keep small so multi-hit weapons still report.</summary>
        private const double ClientAnimSpeedEpsilon = 0.05;
        private static readonly bool ClientSyncVerticalPosition = false;
        private const double ClientTurnSnapDeltaPx = 2.0;
        private const double MobStatePositionEpsilon = 0.35;
        private const double HostMobStateMidPositionEpsilon = 1.20;
        private const double HostMobStateDormantPositionEpsilon = 6.00;
        private const double MobFallbackMinimumScoreGap = 4.0;
        /// <summary>Client/host mobs can drift while fighting. Direct sync-id hits are allowed within this many pixels instead of requiring exact coordinates.</summary>
        private const double MobHitTrustedSyncIdDistancePx = 24.0 * 32.0;
        private const double ClientAiAuthorityLockDurationSeconds = 99999.0;
        private const double AuthoritativeAffectPresenceSeconds = 99999.0;
        private const double PixelsPerCase = 24.0;
        private const double ClientNetworkAttackMinActiveFrames = 20.0;
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
