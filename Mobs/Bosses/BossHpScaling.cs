using System.Runtime.CompilerServices;
using dc.en;
using dc.en.mob;

namespace DeadCellsMultiplayerMod.Mobs.Bosses;

public static class BossHpScaling
{
    private sealed class ScaleState
    {
        public int BaseMaxLife;
        public int AppliedMaxLife;
        public double AppliedMultiplier;
    }

    private static readonly ConditionalWeakTable<Mob, ScaleState> ScaleStates = new();

    public static void ScaleForMultiplayer(Mob mob)
    {
        if (mob == null)
            return;

        // The host owns authoritative mob HP. Scaling client proxies locally can briefly create a
        // different max-life value and can multiply the same boss more than once before the first
        // host snapshot arrives.
        var net = DeadCellsMultiplayerMod.LobbySession.NetRef;
        if (net == null || !net.IsAlive || !net.IsHost)
            return;

        var playerCount = 1 + NetNode.ConnectedClientCount;

        try
        {
            var mult = BossSyncHelpers.GetHpMultiplierForMob(mob, playerCount);
            if (double.IsNaN(mult) || double.IsInfinity(mult) || mult <= 0)
                mult = 1.0;

            var currentMaxLife = System.Math.Max(1, mob.maxLife);
            var currentLife = System.Math.Clamp(mob.life, 0, currentMaxLife);
            var state = ScaleStates.GetValue(mob, static _ => new ScaleState());

            int baseMaxLife;
            double lifeRatio;

            if (state.AppliedMaxLife > 0 && currentMaxLife == state.AppliedMaxLife)
            {
                // This exact native object already carries our scaled value. Repeated calls from
                // registerEntity + entitiesPostCreate must be idempotent.
                if (System.Math.Abs(state.AppliedMultiplier - mult) <= 0.0001)
                    return;

                baseMaxLife = System.Math.Max(1, state.BaseMaxLife);
                lifeRatio = currentLife / (double)state.AppliedMaxLife;
            }
            else
            {
                // Vanilla may rewrite HP after entity registration or during a phase transition.
                // Treat the new value as the fresh baseline and preserve its current life ratio.
                baseMaxLife = currentMaxLife;
                lifeRatio = currentLife / (double)currentMaxLife;
            }

            var newMaxLife = System.Math.Max(1, (int)System.Math.Round(baseMaxLife * mult));
            var newLife = System.Math.Clamp(
                (int)System.Math.Round(newMaxLife * System.Math.Clamp(lifeRatio, 0.0, 1.0)),
                0,
                newMaxLife);

            if (mob.maxLife != newMaxLife || mob.life != newLife)
            {
                mob.maxLife = newMaxLife;
                mob.life = newLife;
                try { mob.initLife(newLife, newMaxLife); } catch { }
            }

            state.BaseMaxLife = baseMaxLife;
            // Record what the boss actually ends up carrying rather than what was requested.
            // initLife may clamp or re-derive maxLife, and storing the requested value would make
            // the next call miss the "already scaled" check above, treat an already-scaled maxLife
            // as a fresh vanilla baseline, and multiply it a second time.
            state.AppliedMaxLife = System.Math.Max(1, mob.maxLife);
            state.AppliedMultiplier = mult;
        }
        catch
        {
            // Boss HP scaling is optional. Never let a proxy/version mismatch break the fight.
        }
    }
}
