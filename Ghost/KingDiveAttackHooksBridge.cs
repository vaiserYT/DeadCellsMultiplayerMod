using System.Globalization;
using System.Diagnostics;
using System.Text;
using dc.en;
using dc.hl.types;
using dc.pr;
using dc.tool.mainSkills;
using DeadCellsMultiplayerMod.Ghost;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using Hashlink.Virtuals;
using ModCore.Utilities;

namespace DeadCellsMultiplayerMod;

public partial class ModEntry
{
    private const string RemoteDiveAttackKind = "__DIVE_ATTACK__";
    private const string RemoteDiveInfoKindPrefix = "__DIVE_INFO__:";
    private const int RemoteDiveAttackSlot = -1;
    private const double LocalDiveStartRepeatBlockSeconds = 0.04;
    private const double LocalDiveLandRepeatBlockSeconds = 0.04;
    private const double DiveInfoRescanMinSeconds = 0.2;
    private const double PostRoomDiveGuardSeconds = 1.25;

    private static long s_lastNoCombatDiveSuppressLogTicks;

    private long _lastLocalDiveStartSendTicks;
    private long _lastLocalDiveLandSendTicks;
    private long _lastDiveInfoScanTicks;
    /// <summary>Suppress dive roll network traffic briefly after spawn / level change (controller noise).</summary>
    private long _localDiveNetGuardUntilTicks;
    private string _lastSentDiveInfoPayload = string.Empty;
    private readonly Dictionary<int, string> _remoteDiveInfoPayloadById = new();

    /// <summary>Vanilla <c>onOwnerLand</c> delegate from the hook; set on first hook invocation so remote replay can call the same path without re-entering the hook.</summary>
    private static Hook_DiveAttack.orig_onOwnerLand? s_diveOnOwnerLandOrig;

    private void Hook_DiveAttack_onStart(Hook_DiveAttack.orig_onStart orig, DiveAttack self)
    {
        if (!IsDiveAttackHookContextValid(self, out _))
            return;

        try
        {
            orig(self);
        }
        catch (Exception ex)
        {
            Logger.Warning("[NetMod] DiveAttack.onStart failed: {Message}", ex.Message);
            try { self?.end(); } catch { }
            return;
        }

        NotifyLocalDiveAttackStartedFromHooks(self);
    }

    private void Hook_DiveAttack_onOwnerLand(Hook_DiveAttack.orig_onOwnerLand orig, DiveAttack self, double high)
    {
        s_diveOnOwnerLandOrig ??= orig;
        if (!IsDiveAttackHookContextValid(self, out var hero))
            return;

        var wasDiving = IsDiveReallyActive(self);
        bool executed;
        try
        {
            executed = ExecuteDiveAttackLand(orig, self, high, hero!);
        }
        catch (Exception ex)
        {
            Logger.Warning("[NetMod] DiveAttack.onOwnerLand failed: {Message}", ex.Message);
            try { self?.end(); } catch { }
            return;
        }

        if (!executed)
            return;

        try
        {
            NotifyLocalDiveAttackLandedFromHooks(self, high, wasDiving);
        }
        catch (Exception nex)
        {
            Logger.Warning("[NetMod] DiveAttack.onOwnerLand notify failed: {Message}", nex.Message);
        }
    }

    /// <summary>
    /// Runs the same validation + quad sanitization + vanilla land as <see cref="Hook_DiveAttack_onOwnerLand"/>,
    /// using the cached vanilla delegate (avoids re-entering the hook). Returns false if the vanilla delegate is not
    /// yet available or validation fails; callers may fall back to <c>dive.onOwnerLand</c> (hooked path).
    /// </summary>
    internal static bool TryInvokeSafeDiveAttackOnOwnerLand(DiveAttack? self, double high)
    {
        if (self == null || s_diveOnOwnerLandOrig == null)
            return false;

        if (!IsDiveAttackHookContextValid(self, out var hero))
            return false;

        try
        {
            ExecuteDiveAttackLand(s_diveOnOwnerLandOrig, self, high, hero!);
            return true;
        }
        catch
        {
            try { self.end(); } catch { }
            return true;
        }
    }

    private static bool IsDiveAttackHookContextValid(DiveAttack? self, out Hero? hero)
    {
        hero = null;
        if (self == null)
            return false;

        try
        {
            hero = self.hero;
        }
        catch
        {
            return false;
        }

        if (hero == null)
            return false;

        try
        {
            if (hero.destroyed)
                return false;
        }
        catch
        {
            return false;
        }

        try
        {
            if (hero._level == null)
                return false;
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static bool ExecuteDiveAttackLand(Hook_DiveAttack.orig_onOwnerLand orig, DiveAttack self, double high, Hero hero)
    {
        Level? level;
        try
        {
            level = hero._level;
        }
        catch
        {
            level = null;
        }

        if (level == null)
            return false;

        // Transition/passages (T_*) are no-combat staging levels. A sublevel return can leave
        // stale Mob collision entries alive even though there are no legitimate combat targets.
        // Let the dive animation end, but do not run vanilla area-hit resolution there.
        if (IsNoCombatTransitionLevel(level, out var levelId))
        {
            LogSuppressedNoCombatDive(levelId);
            try { self.end(); } catch { }
            return false;
        }

        // Vanilla applyHit/applyAttackResult reads spr.groupName; null crashes the HL runtime (HashlinkError).
        if (!IsHeroSpriteGroupReadyForCombat(hero))
        {
            try { self.end(); } catch { }
            return false;
        }

        WithSanitizedDiveTargets(level, () => orig(self, high));
        return true;
    }

    private static bool IsNoCombatTransitionLevel(Level level, out string levelId)
    {
        levelId = string.Empty;
        try
        {
            levelId = level.map?.id?.ToString() ?? string.Empty;
        }
        catch
        {
        }

        return levelId.StartsWith("T_", StringComparison.Ordinal);
    }

    private static void LogSuppressedNoCombatDive(string levelId)
    {
        var now = Stopwatch.GetTimestamp();
        if (s_lastNoCombatDiveSuppressLogTicks != 0 &&
            Stopwatch.GetElapsedTime(s_lastNoCombatDiveSuppressLogTicks, now).TotalSeconds < 1.0)
        {
            return;
        }

        s_lastNoCombatDiveSuppressLogTicks = now;
        Instance?.Logger.Information(
            "[NetMod][DiveGuard] suppressed dive hit in no-combat transition level={LevelId}",
            levelId);
    }

    /// <summary>
    /// True when the hero sprite has a non-null <c>groupName</c>, which vanilla combat assumes during dive hits
    /// (e.g. remote dive under <see cref="KingWeaponSupport.WithKingContext"/> swapping the ghost skin onto the hero).
    /// </summary>
    private static bool IsHeroSpriteGroupReadyForCombat(Hero? hero)
    {
        if (hero == null)
            return false;

        try
        {
            var spr = hero.spr;
            if (spr == null)
                return false;
            return spr.groupName != null;
        }
        catch
        {
            return false;
        }
    }

    private readonly struct DiveTargetableSnapshot
    {
        public readonly dc.Entity Entity;
        public readonly bool WasTargetable;

        public DiveTargetableSnapshot(dc.Entity entity, bool wasTargetable)
        {
            Entity = entity;
            WasTargetable = wasTargetable;
        }
    }

    private static void WithSanitizedDiveTargets(Level level, Action action)
    {
        if (action == null)
            return;

        var disabled = new List<DiveTargetableSnapshot>();
        try
        {
            ArrayObj? entities = null;
            try { entities = level.entities; } catch { }
            if (entities != null)
            {
                for (var i = 0; i < entities.length; i++)
                {
                    dc.Entity? entity = null;
                    try { entity = entities.getDyn(i) as dc.Entity; } catch { }
                    if (entity == null || IsEntityQuadHitSafe(entity))
                        continue;

                    try
                    {
                        var wasTargetable = entity._targetable;
                        entity._targetable = false;
                        disabled.Add(new DiveTargetableSnapshot(entity, wasTargetable));
                    }
                    catch
                    {
                    }
                }
            }

            WithSanitizedQuadElements(level, action);
        }
        finally
        {
            for (var i = disabled.Count - 1; i >= 0; i--)
            {
                var snapshot = disabled[i];
                try
                {
                    if (!snapshot.Entity.destroyed)
                        snapshot.Entity._targetable = snapshot.WasTargetable;
                }
                catch
                {
                }
            }
        }
    }

    private static void WithSanitizedQuadElements(Level level, Action action)
    {
        if (action == null)
            return;

        ArrayObj? original = null;
        ArrayObj? sanitized = null;
        var swapped = false;
        try
        {
            original = level.listCurrentQuadElements;
            if (!TrySanitizeQuadElements(original, out sanitized))
            {
                action();
                return;
            }

            level.listCurrentQuadElements = sanitized!;
            swapped = true;
            action();
        }
        finally
        {
            if (swapped && sanitized != null)
            {
                try
                {
                    if (ReferenceEquals(level.listCurrentQuadElements, sanitized))
                        level.listCurrentQuadElements = original!;
                }
                catch
                {
                }
            }
        }
    }

    private static bool TrySanitizeQuadElements(ArrayObj? source, out ArrayObj? sanitized)
    {
        sanitized = null;
        if (source == null)
            return false;

        var needsSanitizing = false;
        var arr = ArrayUtils.CreateDyn();
        for (var i = 0; i < source.length; i++)
        {
            object? entry;
            try
            {
                entry = source.getDyn(i);
            }
            catch
            {
                needsSanitizing = true;
                continue;
            }

            if (entry is dc.Entity entity)
            {
                if (IsEntityQuadHitSafe(entity))
                {
                    arr.array.pushDyn(entity);
                    continue;
                }

                needsSanitizing = true;
                continue;
            }

            needsSanitizing = true;
        }

        if (!needsSanitizing)
            return false;

        sanitized = (ArrayObj)arr.array;
        return true;
    }

    /// <summary>
    /// Skip entities whose sprite/group metadata would crash vanilla hit resolution (<c>applyAttackResult</c>).
    /// </summary>
    private static bool IsEntityQuadHitSafe(dc.Entity entity)
    {
        if (entity == null)
            return false;

        try
        {
            var spr = entity.spr;
            if (spr == null)
                return false;
            return spr.groupName != null;
        }
        catch
        {
            return false;
        }
    }

    private bool IsLocalDiveNetGuardActive()
    {
        if (_localDiveNetGuardUntilTicks == 0)
            return false;
        return Stopwatch.GetTimestamp() < _localDiveNetGuardUntilTicks;
    }

    internal void MarkDiveNetGuardAfterSpawnOrRoomChange()
    {
        _localDiveNetGuardUntilTicks = Stopwatch.GetTimestamp() +
            (long)(Stopwatch.Frequency * PostRoomDiveGuardSeconds);
    }

    private void NotifyLocalDiveAttackStartedFromHooks(DiveAttack? self)
    {
        if (_netRole == NetRole.None || self == null || me == null)
            return;
        if (IsLocalDiveNetGuardActive())
            return;
        if (KingWeaponSupport.IsInKingContext)
            return;

        var net = _net;
        if (net == null || !net.IsAlive)
            return;

        Hero? owner;
        try { owner = self.hero; } catch { return; }
        if (owner == null || !ReferenceEquals(owner, me))
            return;
        if (!IsPrimaryLocalDiveAttack(self))
            return;

        var isDiving = IsDiveReallyActive(self);
        if (!isDiving)
            return;

        if (IsLocalDiveRepeat(ref _lastLocalDiveStartSendTicks, LocalDiveStartRepeatBlockSeconds))
            return;

        net.SendAttack(
            RemoteDiveAttackKind,
            RemoteDiveAttackSlot,
            isDiving ? 1 : 0,
            null,
            RemoteAttackAction.Attack);
    }

    private void NotifyLocalDiveAttackLandedFromHooks(DiveAttack? self, double high, bool wasDiving)
    {
        if (_netRole == NetRole.None || self == null || me == null)
            return;
        if (IsLocalDiveNetGuardActive())
            return;
        if (KingWeaponSupport.IsInKingContext)
            return;

        var net = _net;
        if (net == null || !net.IsAlive)
            return;

        Hero? owner;
        try { owner = self.hero; } catch { return; }
        if (owner == null || !ReferenceEquals(owner, me))
            return;
        if (!IsPrimaryLocalDiveAttack(self))
            return;
        if (!wasDiving)
            return;

        if (IsLocalDiveRepeat(ref _lastLocalDiveLandSendTicks, LocalDiveLandRepeatBlockSeconds))
            return;

        net.SendAttack(
            RemoteDiveAttackKind,
            RemoteDiveAttackSlot,
            1,
            EncodeRemoteDiveHeight(high),
            RemoteAttackAction.Interrupt);
    }

    private static bool IsDiveReallyActive(DiveAttack? self)
    {
        if (self == null)
            return false;

        try
        {
            if (self.isDiving())
                return true;
        }
        catch
        {
        }

        try
        {
            return self.isActive();
        }
        catch
        {
            return false;
        }
    }

    private bool IsPrimaryLocalDiveAttack(DiveAttack self)
    {
        if (self == null || me == null)
            return false;

        try
        {
            var primary = me.mainSkillsManager?.getMainSkill(DiveAttack.Class) as DiveAttack;
            return primary != null && ReferenceEquals(primary, self);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLocalDiveRepeat(ref long lastTicks, double minSeconds)
    {
        var now = Stopwatch.GetTimestamp();
        var minTicks = (long)(Stopwatch.Frequency * minSeconds);
        if (lastTicks != 0 && now - lastTicks < minTicks)
            return true;
        lastTicks = now;
        return false;
    }

    private static int EncodeRemoteDiveHeight(double high)
    {
        if (double.IsNaN(high) || double.IsInfinity(high))
            return 1000;

        if (high < -1000.0)
            high = -1000.0;
        else if (high > 1000.0)
            high = 1000.0;

        return (int)Math.Round(high * 1000.0, MidpointRounding.AwayFromZero);
    }

    private void TrySendDiveSkillInfoIfChanged(NetNode net, DiveAttack self)
    {
        if (net == null || self == null)
            return;

        var payload = PackDiveSkillInfoForNet(self.skillInfos);
        if (string.IsNullOrEmpty(payload))
            return;
        if (string.Equals(payload, _lastSentDiveInfoPayload, StringComparison.Ordinal))
            return;

        _lastSentDiveInfoPayload = payload;
        net.SendAttack(
            RemoteDiveInfoKindPrefix + payload,
            RemoteDiveAttackSlot,
            1,
            null,
            RemoteAttackAction.Attack);
    }

    internal void TrySendCurrentDiveSkillInfoSnapshot()
    {
        if (_netRole == NetRole.None || me == null)
            return;
        if (IsLocalDiveNetGuardActive())
            return;
        if (KingWeaponSupport.IsInKingContext)
            return;
        if (IsLocalDiveRepeat(ref _lastDiveInfoScanTicks, DiveInfoRescanMinSeconds))
            return;

        var net = _net;
        if (net == null || !net.IsAlive)
            return;

        try
        {
            var dive = me.mainSkillsManager?.getMainSkill(DiveAttack.Class) as DiveAttack;
            if (dive == null)
                return;
            TrySendDiveSkillInfoIfChanged(net, dive);
        }
        catch
        {
        }
    }

    internal void ApplyCachedRemoteDiveSkillInfoIfAny(int remoteId, GhostKing client)
    {
        if (remoteId <= 0 || client == null)
            return;

        if (!_remoteDiveInfoPayloadById.TryGetValue(remoteId, out var payload) || string.IsNullOrWhiteSpace(payload))
            return;

        if (TryUnpackDiveSkillInfoFromNet(payload, out var skillInfos))
            client.SetRemoteDiveSkillInfos(skillInfos);
    }

    internal void ClearCachedRemoteDiveSkillInfo(int remoteId)
    {
        if (remoteId <= 0)
            return;
        _remoteDiveInfoPayloadById.Remove(remoteId);
    }

    private static string PackDiveSkillInfoForNet(
        virtual_cooldown_duration_flags_forbiddenItem_props_requiredItem_skill_? info)
    {
        if (info == null)
            return string.Empty;

        var props = SafeRead(() => info.props, default(virtual_affect_alpha_buff_buff2_color_color2_color3_count_duration2_duration3_pct_pct2_pct3_power_power2_power3_radius_radius2_speed_threshold_));
        return string.Join(";", new[]
        {
            PackToken("cd", FormatDouble(SafeRead(() => info.cooldown, 0.0))),
            PackToken("du", FormatDouble(SafeRead(() => info.duration, 0.0))),
            PackToken("fl", FormatNullableInt(SafeRead(() => info.flags, default(int?)))),
            PackToken("fi", EncodeString(SafeRead(() => info.forbiddenItem?.ToString(), string.Empty))),
            PackToken("ri", EncodeString(SafeRead(() => info.requiredItem?.ToString(), string.Empty))),
            PackToken("sk", EncodeString(SafeRead(() => info.skill?.ToString(), string.Empty))),
            PackToken("af", FormatNullableDouble(SafeRead(() => props?.affect, default(double?)))),
            PackToken("al", FormatNullableDouble(SafeRead(() => props?.alpha, default(double?)))),
            PackToken("bf", FormatNullableDouble(SafeRead(() => props?.buff, default(double?)))),
            PackToken("b2", FormatNullableDouble(SafeRead(() => props?.buff2, default(double?)))),
            PackToken("c1", FormatNullableInt(SafeRead(() => props?.color, default(int?)))),
            PackToken("c2", FormatNullableInt(SafeRead(() => props?.color2, default(int?)))),
            PackToken("c3", FormatNullableInt(SafeRead(() => props?.color3, default(int?)))),
            PackToken("ct", FormatNullableInt(SafeRead(() => props?.count, default(int?)))),
            PackToken("d2", FormatNullableDouble(SafeRead(() => props?.duration2, default(double?)))),
            PackToken("d3", FormatNullableDouble(SafeRead(() => props?.duration3, default(double?)))),
            PackToken("p1", FormatNullableDouble(SafeRead(() => props?.pct, default(double?)))),
            PackToken("p2", FormatNullableDouble(SafeRead(() => props?.pct2, default(double?)))),
            PackToken("p3", FormatNullableDouble(SafeRead(() => props?.pct3, default(double?)))),
            PackToken("pw", FormatNullableDouble(SafeRead(() => props?.power, default(double?)))),
            PackToken("w2", FormatNullableDouble(SafeRead(() => props?.power2, default(double?)))),
            PackToken("w3", FormatNullableDouble(SafeRead(() => props?.power3, default(double?)))),
            PackToken("r1", FormatNullableDouble(SafeRead(() => props?.radius, default(double?)))),
            PackToken("r2", FormatNullableDouble(SafeRead(() => props?.radius2, default(double?)))),
            PackToken("sp", FormatNullableDouble(SafeRead(() => props?.speed, default(double?)))),
            PackToken("th", FormatNullableDouble(SafeRead(() => props?.threshold, default(double?))))
        });
    }

    private static bool TryUnpackDiveSkillInfoFromNet(
        string payload,
        out virtual_cooldown_duration_flags_forbiddenItem_props_requiredItem_skill_? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            var tokens = ParseTokens(payload);

            var parsed = new virtual_cooldown_duration_flags_forbiddenItem_props_requiredItem_skill_();
            SafeAssign(() => parsed.cooldown = ParseDouble(tokens, "cd"));
            SafeAssign(() => parsed.duration = ParseDouble(tokens, "du"));
            SafeAssign(() => parsed.flags = ParseNullableInt(tokens, "fl"));
            SafeAssign(() => parsed.forbiddenItem = DecodeString(ParseToken(tokens, "fi")).AsHaxeString());
            SafeAssign(() => parsed.requiredItem = DecodeString(ParseToken(tokens, "ri")).AsHaxeString());
            SafeAssign(() => parsed.skill = DecodeString(ParseToken(tokens, "sk")).AsHaxeString());

            var props = new virtual_affect_alpha_buff_buff2_color_color2_color3_count_duration2_duration3_pct_pct2_pct3_power_power2_power3_radius_radius2_speed_threshold_();
            SafeAssign(() => props.affect = ParseNullableDouble(tokens, "af"));
            SafeAssign(() => props.alpha = ParseNullableDouble(tokens, "al"));
            SafeAssign(() => props.buff = ParseNullableDouble(tokens, "bf"));
            SafeAssign(() => props.buff2 = ParseNullableDouble(tokens, "b2"));
            SafeAssign(() => props.color = ParseNullableInt(tokens, "c1"));
            SafeAssign(() => props.color2 = ParseNullableInt(tokens, "c2"));
            SafeAssign(() => props.color3 = ParseNullableInt(tokens, "c3"));
            SafeAssign(() => props.count = ParseNullableInt(tokens, "ct"));
            SafeAssign(() => props.duration2 = ParseNullableDouble(tokens, "d2"));
            SafeAssign(() => props.duration3 = ParseNullableDouble(tokens, "d3"));
            SafeAssign(() => props.pct = ParseNullableDouble(tokens, "p1"));
            SafeAssign(() => props.pct2 = ParseNullableDouble(tokens, "p2"));
            SafeAssign(() => props.pct3 = ParseNullableDouble(tokens, "p3"));
            SafeAssign(() => props.power = ParseNullableDouble(tokens, "pw"));
            SafeAssign(() => props.power2 = ParseNullableDouble(tokens, "w2"));
            SafeAssign(() => props.power3 = ParseNullableDouble(tokens, "w3"));
            SafeAssign(() => props.radius = ParseNullableDouble(tokens, "r1"));
            SafeAssign(() => props.radius2 = ParseNullableDouble(tokens, "r2"));
            SafeAssign(() => props.speed = ParseNullableDouble(tokens, "sp"));
            SafeAssign(() => props.threshold = ParseNullableDouble(tokens, "th"));
            SafeAssign(() => parsed.props = props);

            info = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string PackToken(string key, string value) => key + "=" + (value ?? string.Empty);
    private static T SafeRead<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    private static void SafeAssign(Action setter)
    {
        try
        {
            setter();
        }
        catch
        {
        }
    }

    private static string FormatDouble(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string FormatNullableDouble(double? value) => value.HasValue ? FormatDouble(value.Value) : string.Empty;
    private static string FormatNullableInt(int? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
    private static string EncodeString(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static string DecodeString(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(token));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Dictionary<string, string> ParseTokens(string payload)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = payload.Split(';');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (string.IsNullOrWhiteSpace(part))
                continue;
            var idx = part.IndexOf('=');
            if (idx <= 0)
                continue;
            var key = part[..idx];
            var value = idx < part.Length - 1 ? part[(idx + 1)..] : string.Empty;
            map[key] = value;
        }
        return map;
    }

    private static string ParseToken(Dictionary<string, string> tokens, string key)
    {
        return tokens.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static double ParseDouble(Dictionary<string, string> tokens, string key)
    {
        var token = ParseToken(tokens, key);
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        return 0.0;
    }

    private static double? ParseNullableDouble(Dictionary<string, string> tokens, string key)
    {
        var token = ParseToken(tokens, key);
        if (string.IsNullOrWhiteSpace(token))
            return null;
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        return null;
    }

    private static int? ParseNullableInt(Dictionary<string, string> tokens, string key)
    {
        var token = ParseToken(tokens, key);
        if (string.IsNullOrWhiteSpace(token))
            return null;
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;
        return null;
    }

    private static double DecodeRemoteDiveHeight(int? encoded)
    {
        if (!encoded.HasValue)
            return 1.0;

        return encoded.Value / 1000.0;
    }

    private static bool TryExtractRemoteDiveInfoPayload(string? kindId, out string payload)
    {
        payload = string.Empty;
        if (string.IsNullOrWhiteSpace(kindId))
            return false;

        var cleaned = kindId.Trim();
        if (!cleaned.StartsWith(RemoteDiveInfoKindPrefix, StringComparison.Ordinal))
            return false;

        payload = cleaned[RemoteDiveInfoKindPrefix.Length..];
        return true;
    }

    private static bool IsRemoteDiveAttackKind(string? kindId)
    {
        if (string.IsNullOrWhiteSpace(kindId))
            return false;

        return string.Equals(kindId.Trim(), RemoteDiveAttackKind, StringComparison.Ordinal);
    }

    private bool TryHandleRemoteDiveAttack(NetNode.RemoteAttack attack, int localId)
    {
        if (TryExtractRemoteDiveInfoPayload(attack.Kind, out var infoPayload))
        {
            if (!string.IsNullOrWhiteSpace(infoPayload))
            {
                _remoteDiveInfoPayloadById[attack.Id] = infoPayload;

                if (TryGetClientIndex(localId, attack.Id, out var infoIndex))
                {
                    var infoClient = clients[infoIndex];
                    if (infoClient != null && TryUnpackDiveSkillInfoFromNet(infoPayload, out var syncedInfo))
                        infoClient.SetRemoteDiveSkillInfos(syncedInfo);
                }
            }
            return true;
        }

        if (!IsRemoteDiveAttackKind(attack.Kind))
            return false;

        // Drop stale dive land packets while a room/sublevel is changing and during the
        // short post-activation grace window. Full-level changes already drain these queues;
        // sublevel activation needs the same protection.
        if (IsRemoteKingTransitionActive || IsLocalDiveNetGuardActive())
            return true;

        var remoteIsDiving = attack.PermanentId != 0;
        if (!remoteIsDiving)
            return true;

        if (!TryGetClientIndex(localId, attack.Id, out var index))
            return true;

        var client = clients[index];
        if (client == null)
            return true;

        try
        {
            if (attack.Action == RemoteAttackAction.Interrupt)
                client.TriggerRemoteDiveAttackLand(DecodeRemoteDiveHeight(attack.Ammo));
            else
                client.TriggerRemoteDiveAttackStart();
        }
        catch (Exception ex)
        {
            Logger.Warning(
                "[NetMod] Remote dive replay failed remoteId={RemoteId}: {Message}",
                attack.Id,
                ex.Message);
        }

        return true;
    }
}
