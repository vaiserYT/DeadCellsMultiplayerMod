using System.Diagnostics;
using dc.en;
using ModCore.Utilities;
using dc.hl.types;
using dc.tool;
using HaxeProxy.Runtime;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        private static bool IsDebugImmortalLocalHero(Hero? hero)
        {
            return hero != null &&
                   me != null &&
                   ReferenceEquals(hero, me) &&
                   MultiplayerSettingsStorage.IsDebugSectionEnabled &&
                   MultiplayerSettingsStorage.DebugPlayerImmortal;
        }

        private static void ApplyDebugImmortalState(Hero hero)
        {
            if (hero == null)
                return;

            hero.noDamageDuringBossBattle = true;
            if (hero.maxLife > 0 && hero.life < hero.maxLife)
                hero.life = hero.maxLife;
            hero._targetable = true;
        }

        private void ApplyDebugHeroRuntimeOptions()
        {
            var hero = me;
            if (hero == null || !MultiplayerSettingsStorage.IsDebugSectionEnabled)
                return;

            if (IsDebugImmortalLocalHero(hero))
            {
                ApplyDebugImmortalState(hero);
            }
            else
            {
                hero.noDamageDuringBossBattle = false;
            }

            // Stability hardening: never inject perks/items/runes during HeroInit.
            // The original debug path constructed tool.InventItem before item commonProps were ready and
            // caused the Hashlink crash: Null access .commonProps.
            // Real rune/progression sync is handled by CoopAdvancedHardening after the run is alive.
            // TryApplyDebugStartPerk(hero);
            // TryApplyDebugExplorerRune(hero);
        }

        private void TryApplyDebugStartPerk(Hero hero)
        {
            // Disabled intentionally. Do not construct InventItem from debug code.
            return;
#pragma warning disable CS0162
            if (hero == null)
                return;

            var configuredPerkId = MultiplayerSettingsStorage.DebugStartPerkId;
            if (string.IsNullOrWhiteSpace(configuredPerkId) ||
                string.Equals(configuredPerkId, MultiplayerSettingsStorage.NoStartPerkValue, StringComparison.OrdinalIgnoreCase))
            {
                _debugPerkAppliedHero = null;
                _debugPerkAppliedId = string.Empty;
                _nextDebugPerkApplyTick = 0;
                return;
            }

            var perkId = configuredPerkId.Trim();
            if (ReferenceEquals(_debugPerkAppliedHero, hero) &&
                string.Equals(_debugPerkAppliedId, perkId, StringComparison.Ordinal))
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            if (_nextDebugPerkApplyTick != 0 && now < _nextDebugPerkApplyTick)
                return;

            var item = new InventItem(new InventItemKind.Perk(perkId.AsHaxeString()));
            hero.applyItemPickEffect(hero, item);

            if (string.Equals(perkId, "P_Yolo", StringComparison.OrdinalIgnoreCase))
                hero.tryToApplyYoloPerk();

            _debugPerkAppliedHero = hero;
            _debugPerkAppliedId = perkId;
            _nextDebugPerkApplyTick = 0;
#pragma warning restore CS0162
        }

        private void TryApplyDebugExplorerRune(Hero hero)
        {
            if (hero == null)
                return;

            ItemMetaManager? itemMeta = null;
            var user = hero._level?.game?.user ?? game?.user ?? dc.pr.Game.Class.ME?.user;
            if (user == null)
                return;

            itemMeta = user.itemMeta ?? new ItemMetaManager(user);
            itemMeta.itemProgress ??= (ArrayObj)ArrayUtils.CreateDyn().array;
            itemMeta.permanentItems ??= (ArrayObj)ArrayUtils.CreateDyn().array;
            user.itemMeta = itemMeta;

            if (itemMeta == null)
                return;

            if (MultiplayerSettingsStorage.DebugUseExplorersRune)
            {
                var runeKey = ExplorerRunePermanentItemId.AsHaxeString();
                if (!itemMeta.hasPermanentItem(runeKey))
                {
                    if (itemMeta.addPermanentItem(runeKey))
                    {
                        _debugExplorerRuneInjectedByDebug = true;
                        _debugExplorerRuneInjectedMeta = itemMeta;
                    }
                }

                TryRevealAllMinimapForDebugExplorerRune(hero);

                return;
            }

            if (!_debugExplorerRuneInjectedByDebug)
                return;

            try
            {
                var runeKey = ExplorerRunePermanentItemId.AsHaxeString();
                var targetMeta = _debugExplorerRuneInjectedMeta ?? itemMeta;
                var permanentItems = targetMeta?.permanentItems;
                if (permanentItems != null)
                {
                    while (permanentItems.remove(runeKey))
                    {
                    }
                }
            }
            finally
            {
                _debugExplorerRuneInjectedByDebug = false;
                _debugExplorerRuneInjectedMeta = null;
                _debugExplorerRevealAppliedSignature = string.Empty;
                _nextDebugExplorerRevealRetryTick = 0;
            }
        }

        private void TryRevealAllMinimapForDebugExplorerRune(Hero hero)
        {
            if (_debugExplorerRevealAllCount >= MaxDebugExplorerRevealAllCalls)
                return;

            var now = Stopwatch.GetTimestamp();

            var sig = GetDebugExplorerRevealSignature(hero);
            if (!string.IsNullOrWhiteSpace(sig) &&
                string.Equals(_debugExplorerRevealAppliedSignature, sig, StringComparison.Ordinal))
                return;

            if (_nextDebugExplorerRevealRetryTick != 0 && now < _nextDebugExplorerRevealRetryTick)
                return;

            var feedback = false;
            hero.triggerExplorerInstinct(Ref<bool>.From(ref feedback));

            var minimap = hero._level?.game?.hud?.minimap ?? dc.ui.HUD.Class.ME?.minimap;
            if (minimap == null)
            {
                _nextDebugExplorerRevealRetryTick = now + (long)(Stopwatch.Frequency * 0.05);
                return;
            }

            minimap.revealAll();
            _debugExplorerRevealAllCount++;
            minimap.forceRenderRooms();
            minimap.invalidateMinimap();

            if (string.IsNullOrWhiteSpace(sig))
                sig = GetDebugExplorerRevealSignature(hero);

            if (!string.IsNullOrWhiteSpace(sig))
                _debugExplorerRevealAppliedSignature = sig;

            _nextDebugExplorerRevealRetryTick = 0;
        }

        /// <summary>Level id + branch token so we re-reveal after room/sub-level changes with the same map id.</summary>
        private string GetDebugExplorerRevealSignature(Hero hero)
        {
            if (TryGetCurrentVisibilityContext(out var levelId, out var branch) && branch >= 0 &&
                !string.IsNullOrWhiteSpace(levelId))
                return $"{levelId.Trim()}|{branch}";

            var fallback = GetDebugExplorerRevealLevelKey(hero);
            if (!string.IsNullOrWhiteSpace(fallback))
                return $"{fallback.Trim()}|0";

            return string.Empty;
        }

        private string GetDebugExplorerRevealLevelKey(Hero hero)
        {
            var levelFromHero = hero?._level?.map?.id?.ToString();
            if (!string.IsNullOrWhiteSpace(levelFromHero))
                return levelFromHero.Trim();

            var currentLevelId = GetCurrentLevelId();
            if (!string.IsNullOrWhiteSpace(currentLevelId))
                return currentLevelId.Trim();

            return string.Empty;
        }
    }
}
