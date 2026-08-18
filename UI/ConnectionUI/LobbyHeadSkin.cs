using System;
using dc;
using dc.hl.types;
using dc.libs.heaps.slib;
using ModCore.Utilities;
using Serilog;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.Connection
{
    /// <summary>
    /// Kinghead-style CDB lookup only: atlas + part sprites. No live HeroHead.
    /// </summary>
    internal static class LobbyHeadSkin
    {
        internal readonly struct Part
        {
            public Part(string group, double offsetX, double offsetY, int? colorLight, int? colorDark, double scale, int partNumber, string? idleAnim, double? idleAnimSpeed)
            {
                Group = group;
                OffsetX = offsetX;
                OffsetY = offsetY;
                ColorLight = colorLight;
                ColorDark = colorDark;
                Scale = scale;
                PartNumber = partNumber;
                IdleAnim = idleAnim;
                IdleAnimSpeed = idleAnimSpeed;
            }

            public string Group { get; }
            public double OffsetX { get; }
            public double OffsetY { get; }
            public int? ColorLight { get; }
            public int? ColorDark { get; }
            public double Scale { get; }
            public int PartNumber { get; }
            public string? IdleAnim { get; }
            public double? IdleAnimSpeed { get; }
        }

        private static SpriteLib? _cachedLib;
        private static string _cachedAtlas = string.Empty;

        internal static bool TryResolve(string headId, out string atlas, out List<Part> parts, out ArrayObj? glowData, out List<string> particleEffects)
        {
            atlas = "customHead";
            parts = new List<Part>();
            glowData = null;
            particleEffects = new List<string>();
            if (string.IsNullOrWhiteSpace(headId))
                return false;

            try
            {
                var rows = ModEntry.customHeads;
                if (rows?.array == null)
                    return false;

                for (int i = 0; i < rows.array.length; i++)
                {
                    var row = rows.getDyn(i);
                    if (!string.Equals(row.item?.ToString(), headId, StringComparison.Ordinal))
                        continue;

                    try
                    {
                        string rowAtlas = row.atlas?.ToString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(rowAtlas))
                            atlas = rowAtlas;
                    }
                    catch
                    {
                    }

                    try
                    {
                        var g = row.glowData;
                        if (g != null && g.getDyn(0) != null)
                        {
                            var glowArr = ArrayUtils.CreateDyn();
                            glowArr.array.pushDyn(g.getDyn(0));
                            glowData = (ArrayObj)glowArr.array;
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        var props = row.properties;
                        if (props != null)
                        {
                            for (int p = 0; p < props.length; p++)
                            {
                                var part = props.getDyn(p);
                                string group = part.baseSpr?.ToString() ?? string.Empty;
                                if (string.IsNullOrWhiteSpace(group))
                                    continue;

                                double ox = 0;
                                double oy = 0;
                                int? colorLight = null;
                                int? colorDark = null;
                                double scale = 1;
                                int partNumber = 0;
                                string? idleAnim = null;
                                double? idleAnimSpeed = null;
                                try { ox = (double)part.offsetX; } catch { }
                                try { oy = (double)part.offsetY; } catch { }
                                try { scale = (double)part.scale; } catch { }
                                try { partNumber = (int)part.part; } catch { }
                                try
                                {
                                    int raw = (int)part.colorLight;
                                    if (raw != 0)
                                        colorLight = raw;
                                }
                                catch
                                {
                                }
                                try
                                {
                                    int raw = (int)part.colorDark;
                                    if (raw != 0)
                                        colorDark = raw;
                                }
                                catch
                                {
                                }

                                try
                                {
                                    var anims = part.anims;
                                    if (anims != null && anims.length > 0)
                                    {
                                        var states = anims.getDyn(0)?.states;
                                        if (states != null)
                                        {
                                            for (int s = 0; s < states.length; s++)
                                            {
                                                var st = states.getDyn(s);
                                                if ((int)st.state != 0)
                                                    continue;
                                                idleAnim = st.animId?.ToString();
                                                try { idleAnimSpeed = (double)st.animSpd; } catch { }
                                                break;
                                            }
                                        }
                                    }
                                }
                                catch
                                {
                                }

                                parts.Add(new Part(group, ox, oy, colorLight, colorDark, scale, partNumber, idleAnim, idleAnimSpeed));
                            }
                        }
                    }
                    catch
                    {
                    }

try
                    {
                        var fx = row.particleEffects;
                        if (fx != null)
                        {
                            for (int f = 0; f < fx.length; f++)
                            {
                                var confName = fx.getDyn(f)?.particleConf?.ToString();
                                if (!string.IsNullOrWhiteSpace(confName))
                                    particleEffects.Add(confName);
                            }
                        }
                    }
                    catch
                    {
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[ConnectionUI] Lobby head CDB failed headId={HeadId}: {Message}", headId, ex.Message);
            }

            return false;
        }

        internal static SpriteLib? LoadAtlas(string atlas)
        {
            if (string.IsNullOrWhiteSpace(atlas))
                atlas = "customHead";

            if (_cachedLib != null && string.Equals(_cachedAtlas, atlas, StringComparison.Ordinal))
                return _cachedLib;

            try
            {
                DynamicLoadAtlas id = Assets.Class.getDynamicLoadAtlasEnumFromString(atlas.AsHaxeString());
                Assets.Class.loadAtlas(id);
                var lib = Assets.Class.tryGetAtlas(id);
                if (lib == null)
                    return null;

                _cachedLib = lib;
                _cachedAtlas = atlas;
                return lib;
            }
            catch (Exception ex)
            {
                Log.Warning("[ConnectionUI] Lobby head atlas failed atlas={Atlas}: {Message}", atlas, ex.Message);
                return null;
            }
        }

        internal static bool TryResolveGroup(SpriteLib lib, string baseSpr, out string group)
        {
            return TryResolveGroup(lib, baseSpr, null, out group);
        }

        internal static bool TryResolveGroup(SpriteLib lib, string baseSpr, string? preferAnim, out string group)
        {
            group = baseSpr;
            if (string.IsNullOrWhiteSpace(baseSpr) || lib?.groups == null)
                return false;

            string? best = null;
            int bestScore = int.MinValue;
            string? bestAnimated = null;
            int bestAnimatedScore = int.MinValue;
            try
            {
                var keys = lib.groups.keys();
                while (keys.hasNext())
                {
                    string key = keys.next().ToString();
                    int score = ScoreGroup(key, baseSpr);
                    if (score <= bestScore)
                        continue;
                    bestScore = score;
                    best = key;
                }

                if (!string.IsNullOrWhiteSpace(preferAnim))
                {
                    // The CDB IdleAnim (e.g. BobbyFlammeIdle) pins the exact idle timeline,
                    // which wins over the loose baseSpr suffix match (Fall/Run variants).
                    keys = lib.groups.keys();
                    while (keys.hasNext())
                    {
                        string key = keys.next().ToString();
                        if (!key.EndsWith("/" + preferAnim, StringComparison.Ordinal)
                            && !string.Equals(key, preferAnim, StringComparison.Ordinal))
                            continue;
                        int score = ScoreGroup(key, preferAnim);
                        if (score <= bestAnimatedScore)
                            continue;
                        bestAnimatedScore = score;
                        bestAnimated = key;
                    }
                    if (bestAnimated != null && bestAnimatedScore >= 200)
                    {
                        group = bestAnimated;
                        return true;
                    }
                }
            }
            catch
            {
            }

            if (string.IsNullOrEmpty(best) || bestScore < 200)
                return false;

            group = best;
            return true;
        }

        private static int ScoreGroup(string group, string baseSpr)
        {
            // Loose "contains" matches pick the wrong tile (the grey shield on the legs).
            bool pathTail = group.EndsWith("/" + baseSpr, StringComparison.Ordinal);
            bool exact = string.Equals(group, baseSpr, StringComparison.Ordinal);
            if (!pathTail && !exact)
                return int.MinValue;

            int score = pathTail ? 260 : 200;
            if (group.IndexOf("Idle", StringComparison.OrdinalIgnoreCase) >= 0)
                score += 40;
            if (group.IndexOf("Fall", StringComparison.OrdinalIgnoreCase) >= 0
                || group.IndexOf("Run", StringComparison.OrdinalIgnoreCase) >= 0
                || group.IndexOf("Fire", StringComparison.OrdinalIgnoreCase) >= 0
                || group.IndexOf("Atk", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score -= 60;
            }

            return score;
        }
    }
}
