using System;
using dc;
using dc.hl.types;
using Hashlink.Virtuals;
using dc.level;
using HaxeProxy.Runtime;
using dc.tool;
using System.Text.Json.Nodes;
using dc.en;
using dc.haxe;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;


namespace DeadCellsMultiplayerMod
{
    internal class GameDataSync
    {
        static Serilog.ILogger _log;


        static public int Seed;
        private static readonly object _remoteDataLock = new();
        private static readonly Dictionary<string, string> _remoteBrValues = new(StringComparer.OrdinalIgnoreCase);
        private static string? _remoteHeroSkin;

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
            SendBrData(self, net);
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
            SendBrData(seed, net);
            _log.Debug($"meta: {seed}| meta is null? {seed.meta is null}");
            return orig(self, seed, ldat, resetCount, resetCount2);
        }

        public static IReadOnlyDictionary<string, string> GetRemoteBrValues()
        {
            lock (_remoteDataLock)
            {
                return new Dictionary<string, string>(_remoteBrValues);
            }
        }

        public static string? GetRemoteHeroSkin()
        {
            lock (_remoteDataLock)
            {
                return _remoteHeroSkin;
            }
        }

        public static void ReceiveBrData(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return;

            try
            {
                var parts = payload.Split('|');
                if (parts.Length < 2)
                    return;

                var remoteSkin = Unescape(parts[1]);

                lock (_remoteDataLock)
                {
                    _remoteHeroSkin = remoteSkin;
                    _remoteBrValues.Clear();
                    _remoteBrValues["seed"] = Unescape(parts[0]);

                    for (int i = 2; i < parts.Length; i++)
                    {
                        var kv = parts[i].Split('=', 2);
                        if (kv.Length == 2)
                        {
                            _remoteBrValues[kv[0]] = Unescape(kv[1]);
                        }
                    }
                }

                ModEntry.SetRemoteSkin(remoteSkin);
                GameMenu.NotifyBrDataArrived();
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to receive BRDATA: {Message}", ex.Message);
            }
        }

        private static void SendBrData(User self, NetNode? net)
        {
            if (net == null || !net.IsAlive)
                return;

            try
            {
                var payload = BuildBrPayload(self);
                net.SendBrData(payload);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to send BRDATA: {Message}", ex.Message);
            }
        }

        private static string BuildBrPayload(User self)
        {
            var parts = new List<string>
            {
                Seed.ToString(CultureInfo.InvariantCulture),
                Escape(self?.heroSkin?.ToString() ?? string.Empty)
            };

            foreach (var (name, value) in EnumerateBrValues(self))
            {
                parts.Add($"{name}={Escape(value)}");
            }

            return string.Join("|", parts);
        }

        private static IEnumerable<(string Name, string Value)> EnumerateBrValues(User self)
        {
            if (self == null)
                yield break;

            var methods = self.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m =>
                    m.Name.StartsWith("br_", StringComparison.OrdinalIgnoreCase) &&
                    m.GetParameters().Length == 0 &&
                    m.ReturnType != typeof(void))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var method in methods)
            {
                string value;
                try
                {
                    var result = method.Invoke(self, null);
                    value = FormatValue(result);
                }
                catch (Exception ex)
                {
                    _log?.Debug("[NetMod] Failed to invoke {Method}: {Message}", method.Name, ex.Message);
                    continue;
                }

                yield return (method.Name, value);
            }
        }

        private static string FormatValue(object? value)
        {
            if (value == null)
                return string.Empty;

            if (value is IFormattable formattable)
                return formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;

            return value.ToString() ?? string.Empty;
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        }

        private static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
        }
    }
}
