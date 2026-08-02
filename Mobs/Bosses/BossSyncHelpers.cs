using dc.en;
using dc.en.mob;
using DeadCellsMultiplayerMod;

namespace DeadCellsMultiplayerMod.Mobs.Bosses;

public static class BossSyncHelpers
{
    // Native bosses should always be caught by dc.en.mob.Boss or Level.boss.  These exact
    // normalized names cover DLC/Boss Rush proxy wrappers that have appeared without either
    // identity. Exact matching is intentional: broad tokens such as "Death" would classify
    // projectiles and arena helpers as encounter bosses.
    private static readonly HashSet<string> KnownBossProxyTypeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Behemoth",
        "Concierge",
        "Beholder",
        "Conjunctivius",
        "MamaTick",
        "Berserk",
        "TimeKeeper",
        "Giant",
        "Gardener",
        "GardenerBoss",
        // "Scarecrow" is deliberately NOT listed. dc.en.mob.Scarecrow is an ordinary enemy - it
        // derives from Mob rather than dc.en.mob.Boss and ships in the mob database as a weighted,
        // spawnable entry ("id":"Scarecrow","weight":500) - so listing it promoted a normal mob to
        // full boss authority: boss HP multiplier, boss keyframe cadence, boss identity/death
        // watchdogs and the boss-only "restore life after a client hit" path. The actual Scarecrow
        // ENCOUNTER is GardenerBoss, which is already covered above and by the Boss base type. If a
        // future build ever makes Scarecrow the arena boss, the Level.boss check below still
        // classifies it correctly without this entry.
        "KingsHand",
        "HandOfTheKing",
        "Collector",
        "Queen",
        "Servant",
        "ServantBoss",
        "LighthouseBoss",
        "Calliope",
        "Euterpe",
        "Kleio",
        "Death",
        "DeathBoss",
        "Dooku",
        "DookuBeast",
        "Dracula",
        "DraculaBeast",
        "Medusa"
    };

    private static readonly string[] NonEncounterBossComponentTokens =
    {
        "Tentacle", "Claw", "Hand", "Eye", "Scythe", "Projectile", "Bullet", "Orb", "Spawner",
        "Bomb", "Grenade", "Shard", "Weapon", "Helper", "Arm", "Fist", "Appendage", "Minion",
        "Pylon", "Trap", "Spike", "Hazard", "Beam", "Laser", "Wave", "Vine", "Mushroom",
        "Bat", "Swarm", "Shuriken", "Knife", "Dagger"
    };

    private static readonly string[] BossProxyPrefixTokens = { "BossRush", "Altered", "Modified", "Boss" };
    private static readonly string[] BossProxySuffixTokens = { "Proxy", "Clone", "BossRush", "Altered", "Modified", "Boss" };

    public static bool IsBossMob(Mob mob)
    {
        if (mob == null)
            return false;

        try
        {
            // Some generated DLC proxies do not preserve the native base type, but the active
            // Level still exposes the authoritative primary boss reference.
            var level = mob._level;
            if (level != null && ReferenceEquals(level.boss, mob))
                return true;

            if (IsKnownBossRuntimeType(mob))
                return true;

            // Boss-owned helpers often share dc.en.mob.Boss or the .mob.boss namespace with their
            // encounter owner. They must remain ordinary synchronized actors: treating a Giant
            // hand, Conjunctivius tentacle, Mama Tick claw, Death scythe or projectile as a boss
            // applies the strict boss AI/death lock and prevents its native attack lifecycle.
            if (IsKnownBossComponentType(mob))
                return false;

            if (IsKnownBossProxyType(mob))
                return true;

            // Prefer the actual Dead Cells boss base type after excluding known helper families.
            // Several DLC/Boss Rush bosses are generated through proxy types whose namespace/name
            // does not reliably contain ".boss.", but they still inherit dc.en.mob.Boss.
            if (mob is dc.en.mob.Boss)
                return true;

            // Namespace membership is not boss identity. Dead Cells keeps many boss-owned actors
            // (arena hazards, weapons and appendages) beside their owner in the boss namespace.
            // The native Boss base, Level.boss and the exact/decorated type table above cover the
            // encounter owners without promoting every such helper into strict boss authority.
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True for an encounter-owning boss actor. This is stricter than <see cref="IsBossMob"/>
    /// so lingering hands, eyes, tentacles or projectiles cannot prevent encounter completion.
    /// </summary>
    public static bool IsBossEncounterCombatant(Mob mob)
    {
        if (mob == null)
            return false;

        try
        {
            var level = mob._level;
            if (level != null && ReferenceEquals(level.boss, mob))
                return true;

            if (IsKnownBossRuntimeType(mob))
                return true;

            if (IsKnownBossComponentType(mob))
                return false;

            if (IsKnownBossProxyType(mob))
                return true;

            if (mob is dc.en.mob.Boss)
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsKnownBossComponentType(Mob mob)
    {
        if (mob == null)
            return false;

        string runtimeName;
        string typeId = string.Empty;
        try { runtimeName = mob.GetType()?.Name ?? string.Empty; }
        catch { runtimeName = string.Empty; }
        try { typeId = mob.type?.ToString() ?? string.Empty; }
        catch { }

        // A name that resolves to a known encounter boss is never a boss-owned component, even when
        // it contains a component token. KingsHand contains "Hand" and its type id "kingsHand" trips
        // the same test. IsKnownBossRuntimeType only inspects the runtime type NAME, so a boss whose
        // runtime type is a Boss Rush/DLC wrapper reaches this method with only its type id intact -
        // and without this guard it is demoted to a helper and loses boss authority entirely.
        if (IsKnownBossProxyKey(runtimeName) || IsKnownBossProxyKey(typeId))
            return false;

        for (var i = 0; i < NonEncounterBossComponentTokens.Length; i++)
        {
            var token = NonEncounterBossComponentTokens[i];
            if (runtimeName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                typeId.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsKnownBossProxyType(Mob mob)
    {
        if (mob == null)
            return false;

        var runtimeType = mob.GetType();
        if (IsKnownBossProxyKey(runtimeType?.Name))
            return true;

        try
        {
            if (IsKnownBossProxyKey(mob.type?.ToString()))
                return true;
        }
        catch
        {
        }

        return false;
    }

    private static bool IsKnownBossRuntimeType(Mob mob)
    {
        if (mob == null)
            return false;

        try
        {
            return IsKnownBossProxyKey(mob.GetType()?.Name);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsKnownBossProxyKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var lastDot = raw.LastIndexOf('.');
        var key = lastDot >= 0 && lastDot + 1 < raw.Length ? raw[(lastDot + 1)..] : raw;
        var lastPlus = key.LastIndexOf('+');
        if (lastPlus >= 0 && lastPlus + 1 < key.Length)
            key = key[(lastPlus + 1)..];

        key = key.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim();

        // Boss Rush and generated DLC wrappers compose decorators (for example
        // BossRushAlteredGiantProxy). Peel only known decorators and still require an exact known
        // boss key at the end, so ordinary mobs containing words such as "death" stay excluded.
        for (var pass = 0; pass < 6 && key.Length > 0; pass++)
        {
            if (KnownBossProxyTypeKeys.Contains(key))
                return true;

            var peeled = false;
            for (var i = 0; i < BossProxyPrefixTokens.Length; i++)
            {
                var prefix = BossProxyPrefixTokens[i];
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || key.Length <= prefix.Length)
                    continue;

                key = key[prefix.Length..];
                peeled = true;
                break;
            }
            if (peeled)
                continue;

            for (var i = 0; i < BossProxySuffixTokens.Length; i++)
            {
                var suffix = BossProxySuffixTokens[i];
                if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || key.Length <= suffix.Length)
                    continue;

                key = key[..^suffix.Length];
                peeled = true;
                break;
            }

            if (!peeled)
                break;
        }

        return KnownBossProxyTypeKeys.Contains(key);
    }

    public static bool IsBossTypeSignature(string? rawSignature)
    {
        if (string.IsNullOrWhiteSpace(rawSignature))
            return false;

        var parts = rawSignature.Split('|', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var raw = parts[i]?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (IsKnownBossProxyKey(raw))
                return true;

            var isComponent = false;
            for (var j = 0; j < NonEncounterBossComponentTokens.Length; j++)
            {
                if (raw.Contains(NonEncounterBossComponentTokens[j], StringComparison.OrdinalIgnoreCase))
                {
                    isComponent = true;
                    break;
                }
            }
            if (isComponent)
                continue;

            if (raw.Contains(".mob.boss.", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static double GetHpMultiplierForMob(Mob mob, int playerCount)
    {
        if (mob == null)
            return 1;

        var baseMultiplier = playerCount > 1
            ? (IsBossMob(mob)
                ? 1 + (playerCount - 1) * BossSyncConstants.BossHpMultiplierPerPlayer
                : 1 + (playerCount - 1) * BossSyncConstants.RegularMobHpMultiplierPerPlayer)
            : 1.0;

        var userMultiplier = IsBossMob(mob)
            ? MultiplayerSettingsStorage.BossesHpMultiplier
            : MultiplayerSettingsStorage.MobsHpMultiplier;

        if (double.IsNaN(userMultiplier) || double.IsInfinity(userMultiplier) || userMultiplier <= 0)
            userMultiplier = 1;

        return baseMultiplier * userMultiplier;
    }
}
