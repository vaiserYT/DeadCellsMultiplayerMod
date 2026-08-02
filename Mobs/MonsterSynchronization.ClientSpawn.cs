using System;
using System.Collections.Generic;
using System.Reflection;
using dc;
using dc.en;
using dc.pr;

// The dc namespace exposes a Haxe `Type` class, which collides with System.Type on every
// reflection declaration in this file. An alias binds the name explicitly and takes precedence
// over both namespace imports, so `using dc;` can stay for Level.
using Type = System.Type;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    /// <summary>
    /// Client-side replica creation for mobs that exist only on the host.
    /// </summary>
    /// <remarks>
    /// MOBREG can only ever BIND a NetId to a mob the client already has. Level-bootstrap mobs are
    /// fine because both peers generate the same level, but anything the host spawns at runtime —
    /// malaise waves, summons, elite replacements — has no local counterpart, so the registration is
    /// dropped and the enemy is invisible to the second player.
    ///
    /// This builds the missing replica by runtime class name. The host's type signature is
    /// "typeId|RuntimeClass"; the runtime class is a real Haxe-backed type in the generated
    /// GameProxy assembly, so it can be resolved by name and constructed the same way the mod
    /// already constructs a GhostKing: (Level, int x, int y) then init().
    ///
    /// The spawned replica is a puppet like every other client mob — client AI stays locked and the
    /// host remains authoritative for position, HP, state and death. If construction fails for a
    /// type, that type is cached as unsupported so the failure costs one attempt, not one per packet.
    /// </remarks>
    public partial class MobsSynchronization
    {
        private static readonly Dictionary<string, Type?> s_clientSpawnTypeCache = new(StringComparer.Ordinal);
        private static long s_clientSpawnAttempts;
        private static long s_clientSpawnSucceeded;

        private static void TryInvokeNoArgMember(object target, string methodName)
        {
            try
            {
                var method = target.GetType().GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                method?.Invoke(target, null);
            }
            catch
            {
            }
        }

        private static void TryInvokeLevelMember(object target, string methodName, Level level)
        {
            try
            {
                var method = target.GetType().GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(Level) },
                    null);
                method?.Invoke(target, new object[] { level });
            }
            catch
            {
            }
        }

        /// <summary>
        /// Extracts the runtime class portion of a host type signature ("typeId|RuntimeClass").
        /// </summary>
        private static string ExtractRuntimeClassKey(string? typeSignature)
        {
            if (string.IsNullOrWhiteSpace(typeSignature))
                return string.Empty;

            var pipe = typeSignature.LastIndexOf('|');
            if (pipe >= 0 && pipe + 1 < typeSignature.Length)
                return typeSignature[(pipe + 1)..].Trim();

            return typeSignature.Trim();
        }

        private static Type? ResolveMobRuntimeType(string runtimeClass)
        {
            if (string.IsNullOrWhiteSpace(runtimeClass))
                return null;

            lock (s_clientSpawnTypeCache)
            {
                if (s_clientSpawnTypeCache.TryGetValue(runtimeClass, out var cached))
                    return cached;
            }

            Type? found = null;
            try
            {
                // Scoped to the assembly that already provided the Mob base type. A full
                // AppDomain scan with GetTypes() would force every type in every loaded assembly
                // to resolve, which is both slow and risky next to a Hashlink-backed proxy.
                found = FindMobTypeInAssembly(typeof(Mob).Assembly, runtimeClass);
            }
            catch
            {
                found = null;
            }

            lock (s_clientSpawnTypeCache)
            {
                s_clientSpawnTypeCache[runtimeClass] = found;
            }

            return found;
        }

        private static Type? FindMobTypeInAssembly(Assembly assembly, string runtimeClass)
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsAbstract || !typeof(Mob).IsAssignableFrom(type))
                        continue;
                    if (!string.Equals(type.Name, runtimeClass, StringComparison.Ordinal))
                        continue;

                    return type;
                }
            }
            catch
            {
                // A partially-loaded assembly can throw on GetTypes; treat as no match.
            }

            return null;
        }

        /// <summary>
        /// Finds a usable replica constructor.
        /// </summary>
        /// <remarks>
        /// Concrete mobs do NOT expose <c>dc.Entity</c>'s <c>(Level, int, int)</c> shape. The real
        /// signatures are <c>dc.en.Mob(Level, int x, int y, string kind, int dmgTier, int lifeTier)</c>
        /// and, for typical subclasses such as Zombie or MamaTick,
        /// <c>(Level, int x, int y, int dmgTier, int lifeTier)</c>. Only the base Entity declares the
        /// three-argument form, so demanding it matched essentially no real mob type and every
        /// dynamic spawn failed with "no_ctor" - which is why malaise/summoned enemies existed on the
        /// host only. Accept any constructor that begins with (Level, int, int) and whose remaining
        /// parameters can be defaulted, preferring the one with fewest extras.
        /// </remarks>
        private static ConstructorInfo? ResolveMobReplicaConstructor(Type mobType)
        {
            ConstructorInfo? best = null;
            var bestExtraCount = int.MaxValue;

            ConstructorInfo[] ctors;
            try
            {
                ctors = mobType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch
            {
                return null;
            }

            foreach (var ctor in ctors)
            {
                var parameters = ctor.GetParameters();
                if (parameters.Length < 3)
                    continue;
                if (parameters[0].ParameterType != typeof(Level))
                    continue;
                if (parameters[1].ParameterType != typeof(int) || parameters[2].ParameterType != typeof(int))
                    continue;

                var usable = true;
                for (var i = 3; i < parameters.Length; i++)
                {
                    var parameterType = parameters[i].ParameterType;
                    if (parameterType.IsValueType || parameterType == typeof(string))
                        continue;

                    usable = false;
                    break;
                }

                if (!usable)
                    continue;

                var extraCount = parameters.Length - 3;
                if (extraCount >= bestExtraCount)
                    continue;

                bestExtraCount = extraCount;
                best = ctor;
            }

            return best;
        }

        private static object?[] BuildMobReplicaConstructorArgs(ConstructorInfo ctor, Level level, int cx, int cy)
        {
            var parameters = ctor.GetParameters();
            var args = new object?[parameters.Length];
            args[0] = level;
            args[1] = cx;
            args[2] = cy;

            for (var i = 3; i < parameters.Length; i++)
            {
                // Tier/kind arguments only seed local stats and visuals. The replica's AI is locked
                // and the host overwrites life/maxLife on the first authoritative state, so defaults
                // never decide anything the second player can observe.
                var parameter = parameters[i];
                if (parameter.HasDefaultValue)
                {
                    args[i] = parameter.DefaultValue;
                    continue;
                }

                var parameterType = parameter.ParameterType;
                args[i] = parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
            }

            return args;
        }

        /// <summary>
        /// Creates a client replica for a host-only mob. Returns null when the type cannot be built.
        /// </summary>
        /// <param name="x">Host world position in PIXELS, as carried by MOBREG.</param>
        /// <param name="y">Host world position in PIXELS, as carried by MOBREG.</param>
        private static Mob? TryCreateClientMobReplica(string? typeSignature, double x, double y)
        {
            System.Threading.Interlocked.Increment(ref s_clientSpawnAttempts);

            var runtimeClass = ExtractRuntimeClassKey(typeSignature);
            if (string.IsNullOrWhiteSpace(runtimeClass))
                return null;

            var mobType = ResolveMobRuntimeType(runtimeClass);
            if (mobType == null)
            {
                MobSyncTrace.LogClientSpawn(runtimeClass, false, "type_not_found");
                return null;
            }

            Level? level = null;
            try
            {
                level = ModCore.Modules.Game.Instance?.HeroInstance?._level;
            }
            catch
            {
                level = null;
            }

            if (level == null)
            {
                MobSyncTrace.LogClientSpawn(runtimeClass, false, "no_level");
                return null;
            }

            try
            {
                var ctor = ResolveMobReplicaConstructor(mobType);
                if (ctor == null)
                {
                    MobSyncTrace.LogClientSpawn(runtimeClass, false, "no_ctor");
                    return null;
                }

                // Entity constructors take GRID CELL coordinates (see Entity.setPosCase versus
                // Entity.setPosPixel, and dc.level.Mob storing spawns as cx/cy), but MOBREG carries
                // world pixels. Feeding pixels straight in placed every replica 24x too far out,
                // usually outside the level entirely.
                var cx = (int)System.Math.Floor(x / PixelsPerCase);
                var cy = (int)System.Math.Floor(y / PixelsPerCase);

                var created = ctor.Invoke(BuildMobReplicaConstructorArgs(ctor, level, cx, cy)) as Mob;
                if (created == null)
                {
                    MobSyncTrace.LogClientSpawn(runtimeClass, false, "ctor_null");
                    return null;
                }

                // init() and set_level() are called reflectively on purpose. They exist on the
                // KingSkin path the mod already constructs, but I can't verify their presence on
                // every Mob subclass without the game assemblies, and a missing member here would
                // be a compile error rather than a graceful no-op.
                TryInvokeNoArgMember(created, "init");
                TryInvokeLevelMember(created, "set_level", level);

                // Land exactly on the host's position rather than the rounded cell, so the first
                // interpolation step starts from the authoritative point instead of dragging the
                // replica up to half a tile.
                try { created.setPosPixel(x, y); } catch { }

                // The replica must never run its own AI; the host drives it like every other
                // client mob. Position is corrected by the normal authoritative stream.
                TryLockClientMobAiAuthority(created);

                System.Threading.Interlocked.Increment(ref s_clientSpawnSucceeded);
                MobSyncTrace.LogClientSpawn(runtimeClass, true, "created");
                return created;
            }
            catch (Exception ex)
            {
                MobSyncTrace.LogClientSpawn(runtimeClass, false, ex.GetType().Name);
                return null;
            }
        }
    }
}
