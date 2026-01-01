using System;
using dc;
using dc.hl.types;
using Hashlink.Virtuals;
using dc.level;
using HaxeProxy.Runtime;
using dc.tool;
using dc.en;
using dc.haxe;
using ModCore.Utitities;


namespace DeadCellsMultiplayerMod
{
    internal class GameDataSync
    {
        static Serilog.ILogger _log;


        static public int Seed;

        public GameDataSync(Serilog.ILogger log)
        {
            _log = log;
        }



        public static void user_hook_new_game(Hook_User.orig_newGame orig, 
        User self, 
        int lvl, 
        virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ isTwitch, 
        bool isCustom, 
        bool mode, 
        LaunchMode gdata)
        {
            isCustom = false;
            mode = false;
            Seed = lvl;
            var net = GameMenu.NetRef;
            if (net != null && net.IsHost)
            {
                Seed = GameMenu.ForceGenerateServerSeed("NewGame_hook");
                try
                {
                var bossRune = self.mainGame.serverStats.bossRune;
                var endKind = self.mainGame.serverStats.endKind;
                var forge = self.mainGame.serverStats.forge;
                var hasMods = self.mainGame.serverStats.hasMods;
                var history = self.mainGame.serverStats.history;
                var Custom = self.mainGame.serverStats.isCustom;
                var meta = self.mainGame.serverStats.meta;
                }
                catch{}
                net.SendSeed(Seed);
            }
            else if (net != null)
            {
                if (GameMenu.TryGetRemoteSeed(out var remoteSeed))
                {
                    Seed = remoteSeed;
                }
            }
            lvl = Seed;

            SendHeroSkin(self, net);
            orig(self, lvl, isTwitch, isCustom, mode, gdata);
        }

        public static ArrayObj hook_generate(Hook_LevelGen.orig_generate orig, 
        LevelGen self, 
        User seed, 
        int ldat, 
        virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ resetCount, 
        Ref<bool> resetCount2)
        {
            ldat = Seed;
            ModEntry._companionKing = null;
            var net = GameMenu.NetRef;

            SendHeroSkin(seed, net);
            return orig(self, seed, ldat, resetCount, resetCount2);
        }

        public static void ReceiveHeroSkin(string skin)
        {
            try
            {
                var cleaned = CleanSkin(skin);
                if (string.IsNullOrWhiteSpace(cleaned))
                    cleaned = "PrisonerDefault";

                ModEntry.SetRemoteSkin(cleaned);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to receive hero skin: {Message}", ex.Message);
            }
        }

        private static void SendHeroSkin(User user, NetNode? net)
        {
            if (net == null || !net.IsAlive)
                return;

            try
            {
                var skin = CleanSkin(user?.heroSkin?.ToString());
                if (string.IsNullOrWhiteSpace(skin))
                    skin = "PrisonerDefault";

                net.SendHeroSkin(skin);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to send hero skin: {Message}", ex.Message);
            }
        }

        private static string CleanSkin(string? skin)
        {
            if (string.IsNullOrEmpty(skin))
                return string.Empty;

            return skin.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        }
    }
}
