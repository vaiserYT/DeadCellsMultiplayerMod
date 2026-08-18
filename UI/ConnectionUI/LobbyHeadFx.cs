using System;
using dc;
using dc.h2d;
using dc.libs.heaps.slib;
using HaxeProxy.Runtime;
using ModCore.Utilities;
using Serilog;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.Connection
{
    /// <summary>
    /// Lightweight, self-contained particle emitter for lobby head FX.
    /// Mirrors the game's BaseHead* particle confs (cdb_extract\particleConf\HeadFx):
    /// fxDotWhite motes with life/alpha-fade/scale-shrink/gravity-driven motion,
    /// spawned under the head root so it moves with the lobby head.
    /// </summary>
    internal static class LobbyHeadFx
    {
        private static readonly List<Emitter> Active = new();

        internal static void AttachDefaultHeadFx(
            dc.h2d.Object root,
            double headScale,
            string headId,
            bool mirrorX = false,
            double yOffset = 0.0)
        {
            try
            {
                if (root == null)
                    return;

                var defs = LookupDefs(headId);
                if (defs == null || defs.Count == 0)
                    return;

                var fx = Assets.Class.fx;
                if (fx == null)
                    return;

                Active.Add(new Emitter(root, fx, defs, headScale, mirrorX, yOffset));
            }
            catch (Exception ex)
            {
                Log.Warning("[ConnectionUI] Lobby head FX attach failed: {Message}", ex.Message);
            }
        }

        internal static void TickAll(double dt)
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var emitter = Active[i];
                if (emitter == null || emitter.Root == null || emitter.Root.parent == null)
                {
                    emitter?.Dispose();
                    Active.RemoveAt(i);
                    continue;
                }
                emitter.Tick(dt);
            }
        }

        internal static void ClearAll()
        {
            for (int i = 0; i < Active.Count; i++)
                Active[i]?.Dispose();
            Active.Clear();
        }

        private static List<FxDef>? LookupDefs(string headId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(headId))
                    headId = "BaseFlame";
                var rows = ModEntry.customHeads;
                if (rows?.array == null)
                    return BaseFlameDefs();

                for (int i = 0; i < rows.array.length; i++)
                {
                    var row = rows.getDyn(i);
                    if (!string.Equals(row.item?.ToString(), headId, StringComparison.Ordinal))
                        continue;

                    var defs = new List<FxDef>();
                    var fx = row.particleEffects;
                    if (fx != null)
                    {
                        for (int f = 0; f < fx.length; f++)
                        {
                            var conf = fx.getDyn(f)?.particleConf?.ToString();
                            var def = GetConfDef(conf, (int)(fx.getDyn(f)?.blendMode ?? -1));
                            if (def != null)
                                defs.Add(def);
                        }
                    }
                    return defs.Count > 0 ? defs : BaseFlameDefs();
                }
            }
            catch
            {
            }

            return BaseFlameDefs();
        }

        private static FxDef? GetConfDef(string? conf, int blendMode)
        {
            if (string.IsNullOrWhiteSpace(conf))
                return null;

            foreach (var def in AllDefs)
            {
                if (string.Equals(def.Id, conf, StringComparison.OrdinalIgnoreCase))
                {
                    if (blendMode >= 0)
                    {
                        var clone = def.Clone();
                        clone.AddBlend = blendMode == 1 || def.AddBlend;
                        return clone;
                    }
                    return def;
                }
            }
            return null;
        }

        private static List<FxDef> BaseFlameDefs()
        {
            var defs = new List<FxDef>();
            foreach (var def in AllDefs)
            {
                if (def.Id.StartsWith("BaseHead", StringComparison.Ordinal) && def.ScaleMax > 0)
                    defs.Add(def);
            }
            return defs;
        }

        private static readonly FxDef[] AllDefs =
        {
            new FxDef
            {
                Id = "BaseHeadCore",
                SprName = "fxDotWhite",
                Count = 11,
                AddBlend = false,
                LifeMin = 0.5,
                LifeMax = 1.0,
                SpeedMin = 3.0,
                SpeedMax = 5.0,
                VYUp = 0.8,
                Frict = 0.88,
                GravX = 0.0,
                GravY = 0.0,
                PosMinX = 3.0,
                PosMaxX = 4.0,
                PosMinY = 2.0,
                PosMaxY = 4.0,
                YMirror = true,
                AlphaMin = 0.4,
                AlphaMax = 0.7,
                FadeIn = 0.1,
                FadeOut = 0.5,
                ScaleMin = 1.0,
                ScaleMax = 3.0,
                ScaleMul = 0.97,
                RotMax = 6.28,
                RotSpeedMax = 0.1,
                // The game tints these to the head's black silhouette color (headBlack bit, fxFlags & 4).
                ColorStart = 0x1E1E22,
            },
            new FxDef
            {
                Id = "BaseHeadSmoke",
                SprName = "fxDotWhite",
                Count = 4,
                AddBlend = false,
                LifeMin = 0.2,
                LifeMax = 0.4,
                SpeedMin = 0.0,
                SpeedMax = 1.2,
                VYUp = 1.2,
                Frict = 0.88,
                GravX = 0.0,
                GravY = -0.07,
                PosMinX = -1.0,
                PosMaxX = 1.0,
                PosMinY = -2.0,
                PosMaxY = 0.0,
                AlphaMin = 0.3,
                AlphaMax = 0.5,
                FadeIn = 0.1,
                FadeOut = 0.5,
                ScaleMin = 2.0,
                ScaleMax = 2.0,
                ScaleMul = 0.98,
                RotMax = 6.28,
                RotSpeedMax = 0.1,
                // headBlack tint like BaseHeadCore.
                ColorStart = 0x2E2E33,
            },
            new FxDef
            {
                Id = "BaseHeadHomunculus",
                SprName = "fxDirt",
                Count = 4,
                AddBlend = false,
                LifeMin = 0.4,
                LifeMax = 0.6,
                SpeedMin = 0.0,
                SpeedMax = 0.6,
                VYUp = 0.6,
                Frict = 0.9,
                GravX = 0.0,
                GravY = 0.0,
                PosMinX = -1.0,
                PosMaxX = 1.0,
                PosMinY = -1.0,
                PosMaxY = 2.0,
                AlphaMin = 0.35,
                AlphaMax = 0.5,
                FadeIn = 0.1,
                FadeOut = 0.3,
                // No scaleProps in the real conf -> game default scale 1. Was wrongly 2.5-3.5.
                ScaleMin = 1.0,
                ScaleMax = 1.0,
                ScaleMul = 1.0,
                RotMax = 6.28,
                RotSpeedMax = 0.0,
                // Real conf color is 0x1F4B2E (dark green), but at headScale that green body
                // reads as a homunculus blob. Render it as the black head silhouette texture.
                ColorStart = 0x131512,
            },
            new FxDef
            {
                Id = "BaseHeadEyeCore",
                SprName = "fxDotWhite",
                Count = 6,
                AddBlend = true,
                LifeMin = 0.2,
                LifeMax = 0.5,
                SpeedMin = 0.0,
                SpeedMax = 0.8,
                VYUp = 0.8,
                Frict = 0.9,
                GravX = 0.0,
                GravY = -0.05,
                PosMinX = -1.7,
                PosMaxX = 1.7,
                PosMinY = -0.7,
                PosMaxY = 2.7,
                AlphaMin = 0.3,
                AlphaMax = 0.5,
                FadeIn = 0.1,
                FadeOut = 0.5,
                ScaleMin = 1.0,
                ScaleMax = 2.0,
                ScaleMul = 0.97,
                RotMax = 6.28,
                RotSpeedMax = 0.1,
                ColorStart = 0xC71F3D,
            },
        };

        internal sealed class FxDef
        {
            public string Id = string.Empty;
            public string SprName = "fxDotWhite";
            public int Count = 4;
            public bool AddBlend;
            public double LifeMin = 0.5;
            public double LifeMax = 1.0;
            public double SpeedMin;
            public double SpeedMax;
            public double VYUp;
            public double Frict = 0.9;
            public double GravX;
            public double GravY;
            public double PosMinX;
            public double PosMaxX;
            public double PosMinY;
            public double PosMaxY;
            public bool YMirror;
            public double AlphaMin = 0.4;
            public double AlphaMax = 0.7;
            public double FadeIn = 0.1;
            public double FadeOut = 0.5;
            public double ScaleMin = 1.0;
            public double ScaleMax = 3.0;
            public double ScaleMul = 0.97;
            public double RotMax = 6.28;
            public double RotSpeedMax = 0.1;
            public int? ColorStart;
            public double AlphaFlicker;

            public FxDef Clone()
            {
                return (FxDef)MemberwiseClone();
            }
        }

        private sealed class Particle
        {
            public HSprite? Spr;
            public double Life = 1.0;
            public double Age;
            public double MaxAlpha = 1.0;
            public double Scale = 1.0;
            public double ScaleMul = 1.0;
            public double Rot;
            public double RotSpeed;
            public double VX;
            public double VY;
            public double GravX;
            public double GravY;
            public double FadeIn;
            public double FadeOut;
            public double OffX;
            public double OffY;

            public double FadeAlpha(double fadeIn, double fadeOut)
            {
                if (Age < fadeIn)
                    return MaxAlpha * (fadeIn > 0 ? Age / fadeIn : 1.0);
                double left = Life - Age;
                if (left < fadeOut)
                    return MaxAlpha * (fadeOut > 0 ? left / fadeOut : 0.0);
                return MaxAlpha;
            }
        }

        private sealed class Emitter
        {
            private static readonly System.Random Rng = new System.Random();

            public dc.h2d.Object Root;
            private readonly SpriteLib _fx;
            private readonly double _headScale;
            private readonly bool _mirrorX;
            private readonly double _yOffset;
            private readonly List<FxDef> _defs = new List<FxDef>();
            private readonly List<Particle> _particles = new List<Particle>();
            private bool _disposed;

            public Emitter(
                dc.h2d.Object root,
                SpriteLib fx,
                List<FxDef> defs,
                double headScale,
                bool mirrorX = false,
                double yOffset = 0.0)
            {
                Root = root;
                _fx = fx;
                _headScale = headScale;
                _mirrorX = mirrorX;
                _yOffset = yOffset;

                for (int i = 0; i < defs.Count; i++)
                {
                    int count = defs[i].Count;
                    for (int p = 0; p < count; p++)
                    {
                        _particles.Add(new Particle());
                        _defs.Add(defs[i]);
                    }
                }

                for (int i = 0; i < _particles.Count; i++)
                    Spawn(_particles[i], _defs[i]);
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                try
                {
                    for (int i = 0; i < _particles.Count; i++)
                    {
                        var spr = _particles[i].Spr;
                        if (spr != null && spr.parent != null)
                            spr.parent.removeChild(spr);
                    }
                }
                catch
                {
                }
            }

            public void Tick(double dt)
            {
                if (_disposed || Root.parent == null)
                    return;

                for (int i = 0; i < _particles.Count; i++)
                    TickParticle(_particles[i], _defs[i], dt);
            }

            private void TickParticle(Particle p, FxDef def, double dt)
            {
                var spr = p.Spr;
                if (spr == null)
                    return;

                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    Spawn(p, def);
                    return;
                }

                p.VX += p.GravX;
                p.VY += p.GravY;
                p.VX *= def.Frict;
                p.VY *= def.Frict;

                p.OffX += p.VX * _headScale;
                p.OffY += p.VY * _headScale;

                spr.x = _mirrorX ? -p.OffX : p.OffX;
                spr.y = p.OffY + _yOffset * _headScale;

                double scale = p.Scale;
                if (global::System.Math.Abs(p.ScaleMul - 1.0) > 0.0001)
                    scale = p.Scale * p.ScaleMul;

                spr.scaleX = (_mirrorX ? -scale : scale) * _headScale;
                spr.scaleY = scale * _headScale;

                p.Rot += p.RotSpeed * dt;
                try { spr.rotation = p.Rot; } catch { }

                try { spr.set_visible(true); } catch { }
                try
                {
                    double alpha = p.FadeAlpha(p.FadeIn, p.FadeOut);
                    if (def.AlphaFlicker > 0)
                        alpha *= 1.0f - (0.7f * (float)Rng.NextDouble() * (float)def.AlphaFlicker);
                    spr.alpha = alpha;
                }
                catch { }
            }

            private void Spawn(Particle p, FxDef def)
            {
                var spr = p.Spr;
                if (spr == null)
                {
                    if (string.IsNullOrWhiteSpace(def.SprName))
                        return;
                    try
                    {
                        spr = new HSprite(_fx, def.SprName.AsHaxeString(), Ref<int>.Null, null);
                        SpritePivot pivot = spr.pivot;
                        pivot.centerFactorX = 0.5;
                        pivot.centerFactorY = 0.5;
                        pivot.usingFactor = true;
                        pivot.isUndefined = false;
                        if (def.AddBlend)
                            spr.blendMode = new dc.h2d.BlendMode.Add();
                        try { spr.smooth = false; } catch { }
                        if (def.ColorStart.HasValue)
                        {
                            try
                            {
                                int c = def.ColorStart.Value;
                                var color = spr.color;
                                color.x = ((c >> 16) & 0xFF) / 255.0;
                                color.y = ((c >> 8) & 0xFF) / 255.0;
                                color.z = (c & 0xFF) / 255.0;
                            }
                            catch
                            {
                            }
                        }
                        Root.addChild(spr);
                        p.Spr = spr;
                    }
                    catch
                    {
                        return;
                    }
                }

                p.Life = Rand(def.LifeMin, def.LifeMax);
                p.Age = Rand(0.0, p.Life * 0.6);
                p.FadeIn = def.FadeIn;
                p.FadeOut = def.FadeOut;
                p.MaxAlpha = Rand(def.AlphaMin, def.AlphaMax);
                p.Scale = Rand(def.ScaleMin, def.ScaleMax);
                p.ScaleMul = def.ScaleMul;
                p.Rot = Rand(0.0, def.RotMax);
                p.RotSpeed = Rand(-def.RotSpeedMax, def.RotSpeedMax);
                p.GravX = def.GravX;
                p.GravY = def.GravY;
                p.VX = Rand(def.SpeedMin, def.SpeedMax) * (NextBool() ? 1.0 : -1.0);
                p.VY = -def.VYUp + Rand(-0.05, 0.05);

                p.OffX = Rand(def.PosMinX, def.PosMaxX) * _headScale;
                p.OffY = Rand(def.PosMinY, def.PosMaxY) * _headScale;
                if (def.YMirror && NextBool())
                    p.OffY = -p.OffY;
                if (NextBool())
                    p.OffX = -p.OffX;

                spr.x = _mirrorX ? -p.OffX : p.OffX;
                spr.y = p.OffY + _yOffset * _headScale;
                spr.scaleX = (_mirrorX ? -p.Scale : p.Scale) * _headScale;
                spr.scaleY = p.Scale * _headScale;
                try { spr.rotation = p.Rot; } catch { }
                try { spr.alpha = p.FadeAlpha(p.FadeIn, p.FadeOut); } catch { }
                try { spr.set_visible(true); } catch { }
                try { spr.posChanged = true; } catch { }
            }

            private static double Rand(double min, double max)
            {
                if (max <= min)
                    return min;
                return min + Rng.NextDouble() * (max - min);
            }

            private static bool NextBool()
            {
                return Rng.NextDouble() < 0.5;
            }
        }
    }
}