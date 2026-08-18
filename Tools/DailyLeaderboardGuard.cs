using System;
using dc.h2d;
using dc.pr;
using dc.ui;
using HaxeProxy.Runtime;
using Serilog;

namespace DeadCellsMultiplayerMod.Tools
{
    /// <summary>Disables the vanilla Daily Challenge leaderboard panel and its network refresh.</summary>
    internal static class DailyLeaderboardGuard
    {
        private static bool _hooksInstalled;
        private static bool _logged;

        internal static void Initialize()
        {
            if (_hooksInstalled)
                return;

            try
            {
                Hook__LeaderboardPanel.__constructor__ += OnConstructed;
                Hook_LeaderboardPanel.set_visible += OnSetVisible;
                Hook_LeaderboardPanel.refreshData += OnRefreshData;
                Hook_LeaderboardPanel.update += OnUpdate;
                _hooksInstalled = true;
                Log.Information("[NetMod] Daily Challenge leaderboard disabled");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[NetMod] Failed to install Daily Challenge leaderboard guard");
            }
        }

        private static void OnConstructed(
            Hook__LeaderboardPanel.orig___constructor__ orig,
            LeaderboardPanel self,
            TitleScreen screen)
        {
            orig(self, screen);
            Suppress(self);
        }

        private static bool OnSetVisible(
            Hook_LeaderboardPanel.orig_set_visible orig,
            LeaderboardPanel self,
            bool visible)
        {
            var result = orig(self, false);
            Suppress(self);
            return result;
        }

        private static void OnRefreshData(
            Hook_LeaderboardPanel.orig_refreshData orig,
            LeaderboardPanel self,
            Ref<bool> force)
        {
            Suppress(self);
        }

        private static void OnUpdate(
            Hook_LeaderboardPanel.orig_update orig,
            LeaderboardPanel self)
        {
            orig(self);
            Suppress(self);
        }

        private static void Suppress(LeaderboardPanel? panel)
        {
            if (panel == null)
                return;

            try { panel.root?.set_visible(false); } catch { }
            try { panel.mainFlow?.set_visible(false); } catch { }

            if (_logged)
                return;

            _logged = true;
            Log.Information("[NetMod] Suppressed vanilla Daily Challenge leaderboard panel");
        }
    }
}
