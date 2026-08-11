using System.Globalization;
using System.Runtime.CompilerServices;
using dc;
using dc.en;
using dc.en.mob;
using dc.en.mob.boss;

namespace DeadCellsMultiplayerMod.Mobs.Bosses;

/// <summary>
/// Boss-specific adapter for Conjunctivius (internal type <c>dc.en.mob.boss.Beholder</c>).
/// </summary>
/// <remarks>
/// Two pieces of this fight are gameplay-critical, are decided by native logic that only the host
/// runs correctly, and have no entity of their own to carry them:
///
///   * <b>Vulnerability.</b> <c>Beholder.canBeHitBy</c> is the native gate that makes the boss
///     shielded while its tentacles are up. A client replica computes it from ITS OWN tentacle
///     state, so any divergence produced the reported symptoms in both directions: a boss the
///     client could damage during the shielded phase, or one that stayed permanently invulnerable
///     because the client believed tentacles were still alive.
///   * <b>Arena platforms.</b> <c>Beholder.setPlatformsState</c> raises/lowers the arena platforms
///     for a phase. It changes collision, so a client whose platforms disagree is standing on
///     geometry the host does not have.
///
/// Tentacles themselves are deliberately NOT re-described here: <c>BeholderTtcl</c> derives from
/// <c>dc.en.Mob</c>, so it is already an ordinary authoritative registry entity with a host sync id,
/// HP, death and despawn handling. Duplicating it into the boss payload would create a second,
/// competing description of the same objects. What this adapter adds is the state that the
/// tentacles IMPLY, which is exactly the part the entity sync cannot carry.
///
/// The host only ever OBSERVES native decisions here (it never overrides its own fight), and the
/// client applies them. Host validation therefore stays final by construction.
/// </remarks>
internal static class BeholderArenaSync
{
    internal const string VulnerablePrefix = "bvul:";
    internal const string PlatformsPrefix = "bplat:";

    /// <summary>Last value native <c>canBeHitBy</c> returned on the host, per boss instance.</summary>
    private static readonly ConditionalWeakTable<Beholder, StrongBox<bool>> HostVulnerable = new();

    /// <summary>Last value the host passed to <c>setPlatformsState</c>, per boss instance.</summary>
    private static readonly ConditionalWeakTable<Beholder, StrongBox<bool>> HostPlatformsUp = new();

    /// <summary>Authoritative vulnerability received from the host, per client replica.</summary>
    private static readonly ConditionalWeakTable<Beholder, StrongBox<bool>> ClientVulnerable = new();

    /// <summary>Last platform state this client actually applied, so re-sends are inert.</summary>
    private static readonly ConditionalWeakTable<Beholder, StrongBox<bool>> ClientAppliedPlatforms = new();

    private static bool _hooksInstalled;

    internal static void InstallHooks()
    {
        if (_hooksInstalled)
            return;
        _hooksInstalled = true;

        try
        {
            Hook_Beholder.canBeHitBy += Hook_Beholder_canBeHitBy;
            Hook_Beholder.setPlatformsState += Hook_Beholder_setPlatformsState;
            BossSyncDiag.Trace("beholder arena hooks installed");
        }
        catch (Exception ex)
        {
            // A generated-binding mismatch must never stop the rest of mob sync from initializing.
            _hooksInstalled = false;
            Serilog.Log.Warning(ex, "[BossSync] Beholder arena hooks unavailable");
        }
    }

    private static bool Hook_Beholder_canBeHitBy(Hook_Beholder.orig_canBeHitBy orig, Beholder self, Entity by)
    {
        var native = orig(self, by);
        if (self == null)
            return native;

        var net = LobbySession.NetRef;
        if (net == null || !net.IsAlive)
            return native;

        if (net.IsHost)
        {
            // Observe only. The host's own fight is untouched; we just remember what it decided so
            // the state can be published with the boss snapshot.
            Remember(HostVulnerable, self, native);
            return native;
        }

        // Client: the host is authoritative. Returning the host's value in BOTH directions is
        // deliberate. Clamping only to false would stop the client from landing hits (and therefore
        // from reporting damage at all) during a phase the host considers vulnerable, which reads
        // as an invulnerable boss just the same. Until a host value arrives, native behaviour
        // stands.
        if (ClientVulnerable.TryGetValue(self, out var box) && box != null)
            return box.Value;

        return native;
    }

    private static void Hook_Beholder_setPlatformsState(Hook_Beholder.orig_setPlatformsState orig, Beholder self, bool state)
    {
        orig(self, state);
        if (self == null)
            return;

        var net = LobbySession.NetRef;
        if (net == null || !net.IsAlive)
            return;

        if (net.IsHost)
        {
            Remember(HostPlatformsUp, self, state);
            BossSyncDiag.Trace("beholder platforms host state={State}", state);
            return;
        }

        // A client can also reach this natively (its replica runs its own phase scripts). Record it
        // as the applied value so an identical authoritative re-send does not replay the change.
        Remember(ClientAppliedPlatforms, self, state);
    }

    /// <summary>Host: append the observed arena state to the boss's authoritative snapshot.</summary>
    internal static void AppendState(List<string> parts, Mob mob)
    {
        if (parts == null || mob is not Beholder beholder)
            return;

        if (HostVulnerable.TryGetValue(beholder, out var vuln) && vuln != null)
            parts.Add(VulnerablePrefix + (vuln.Value ? "1" : "0"));

        if (HostPlatformsUp.TryGetValue(beholder, out var platforms) && platforms != null)
            parts.Add(PlatformsPrefix + (platforms.Value ? "1" : "0"));
    }

    /// <summary>Client: adopt the authoritative arena state parsed from the boss snapshot.</summary>
    internal static void ApplyState(Mob mob, bool? vulnerable, bool? platformsUp)
    {
        if (mob is not Beholder beholder)
            return;

        var net = LobbySession.NetRef;
        if (net == null || !net.IsAlive || net.IsHost)
            return;

        if (vulnerable.HasValue)
        {
            var hadPrevious = ClientVulnerable.TryGetValue(beholder, out var previous) && previous != null;
            if (!hadPrevious || previous!.Value != vulnerable.Value)
            {
                Remember(ClientVulnerable, beholder, vulnerable.Value);
                BossSyncDiag.Trace("beholder vulnerability applied vulnerable={Vulnerable}", vulnerable.Value);
            }
        }

        if (!platformsUp.HasValue)
            return;

        // Platform state changes collision, so only drive it when it actually differs from what
        // this replica last applied; calling it every snapshot would restart the native transition.
        var alreadyApplied = ClientAppliedPlatforms.TryGetValue(beholder, out var applied) &&
                             applied != null &&
                             applied.Value == platformsUp.Value;
        if (alreadyApplied)
            return;

        try
        {
            beholder.setPlatformsState(platformsUp.Value);
            Remember(ClientAppliedPlatforms, beholder, platformsUp.Value);
            BossSyncDiag.Trace("beholder platforms applied state={State}", platformsUp.Value);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[BossSync] Beholder setPlatformsState apply failed");
        }
    }

    /// <summary>Drops per-encounter state so a later fight cannot inherit the previous one.</summary>
    internal static void Reset()
    {
        HostVulnerable.Clear();
        HostPlatformsUp.Clear();
        ClientVulnerable.Clear();
        ClientAppliedPlatforms.Clear();
    }

    /// <summary>
    /// Updates in place when an entry already exists.
    /// </summary>
    /// <remarks>
    /// <c>canBeHitBy</c> is called many times per frame during a fight (once per attempted hit), and
    /// a Remove+Add with a fresh StrongBox on every one of those calls put avoidable allocation
    /// pressure directly in the hottest boss path. Mutating the existing box costs nothing and is
    /// safe here: every caller is on the game main thread, and the payload is a single bool.
    /// </remarks>
    private static void Remember(ConditionalWeakTable<Beholder, StrongBox<bool>> table, Beholder key, bool value)
    {
        if (table.TryGetValue(key, out var existing) && existing != null)
        {
            existing.Value = value;
            return;
        }

        table.Add(key, new StrongBox<bool>(value));
    }
}
