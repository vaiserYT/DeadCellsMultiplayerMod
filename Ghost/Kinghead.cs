using System;
using dc.en;
using dc.haxe.ds;
using dc.hl.types;
using dc.libs.heaps;
using dc.libs.heaps.slib;
using dc.pr;
using dc.tool;
using dc.tool._AnimationTrack;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using HaxeProxy.Runtime;
using ModCore.Storage;
using ModCore.Utilities;

namespace DeadCellsMultiplayerMod.KingHead
{
    public class Kinghead : HeroHead, IHxbitSerializable<object>
    {
        private Hero? me;
        private GhostKing? king;
        private Level? lvl;
        private dc.h2d.Object? headContainer;
        private dc.h2d.Object? headParticleContainer;
        private dc.h2d.Tile? headMaterial;
        private ArrayBytes_Int? headSkeleton;
        private bool? useLocalSpace;
        private FPoint? kingLastHeadPos;
        private bool usesCustomRemoteHead;
        private string _cachedHeadSkeletonGroup = string.Empty;
        private double _lastForcedPosX = double.NaN;
        private double _lastForcedPosY = double.NaN;
        private const double ForcedPositionEpsilon = 0.05;

        public Kinghead()
        {
        }

        public Kinghead(Hero _me, GhostKing _kingSkin, Level level)
        {
            me = _me;
            king = _kingSkin;
            lvl = level;
            TryBindHeroField(_me);
        }

        private void TryBindHeroField(Hero? heroRef)
        {
            if (heroRef == null)
                return;

            try
            {
                this.hero = heroRef;
            }
            catch
            {
            }
        }

        object IHxbitSerializable<object>.GetData()
        {
            return new();
        }

        void IHxbitSerializable<object>.SetData(object data)
        {
        }

        public override void init(Level parent, dc.h2d.Object fromUI, Ref<bool> fromUI1)
        {
            if (me == null)
                me = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            TryBindHeroField(me);

            var headSprite = king?.spr;
            var remoteHeadSkin = ModEntry.Instance?.remoteHeadSkin;
            if (remoteHeadSkin == null)
                remoteHeadSkin = "BaseFlame";

            usesCustomRemoteHead = false;
            this.customHead = false;
            this.forcedCustomHead = null!;
            this._customHeadInfoCache = null!;
            TryResolveRemoteCustomHeadInfo(remoteHeadSkin);
            if (headSprite != null)
            {
                headMaterial = headSprite.frameData?.tile;
                headSkeleton = ResolveHeadSkeleton(headSprite);
                var useLocal = UseLocalSpace();
                if (useLocal)
                {
                    headContainer = new dc.h2d.Object(headSprite);
                    headParticleContainer = new dc.h2d.Object(headContainer);
                }
                else
                {
                    headContainer = null;
                    headParticleContainer = new dc.h2d.Object(fromUI);
                }
                InitBaseHead(parent, headParticleContainer, fromUI1);
                RebuildHeadParticles(headParticleContainer, headMaterial);
                this.heroHasHead = true;
                this.alwaysShowHead = true;
                this.alwaysShowEye = true;
                return;
            }
            InitBaseHead(parent, fromUI, fromUI1);
            this.heroHasHead = true;
            this.alwaysShowHead = true;
            this.alwaysShowEye = true;
        }

        private void TryResolveRemoteCustomHeadInfo(string remoteHeadSkin)
        {
            for (int i = 0; i < ModEntry.customHeads.array.length; i++)
            {
                var cHead = ModEntry.customHeads.getDyn(i);
                if (cHead.item.ToString() != remoteHeadSkin)
                    continue;

                var data = new Hashlink.Virtuals.virtual_atlas_glowData_item_particleEffects_properties_();
                data.atlas = "customHead".AsHaxeString();

                var glowData = ArrayUtils.CreateDyn();
                var glowDataNone = ArrayUtils.CreateDyn();
                glowData.array.pushDyn(cHead.glowData.getDyn(0));
                data.glowData = ((ArrayObj)glowData.array).getDyn(0) == null
                    ? (ArrayObj)glowDataNone.array
                    : (ArrayObj)glowData.array;

                data.item = remoteHeadSkin.AsHaxeString();

                var particleEffects = ArrayUtils.CreateDyn();
                var particleEffectsNone = ArrayUtils.CreateDyn();
                particleEffects.array.pushDyn(cHead.particleEffects.getDyn(0));
                data.particleEffects = ((ArrayObj)particleEffects.array).getDyn(0) == null
                    ? (ArrayObj)particleEffectsNone.array
                    : (ArrayObj)particleEffects.array;

                var properties = ArrayUtils.CreateDyn();
                for (int b = 0; b < cHead.properties.length; b++)
                    properties.array.pushDyn(cHead.properties.getDyn(b));

                data.properties = (ArrayObj)properties.array;
                this.forcedCustomHead = data;
                this._customHeadInfoCache = data;
                usesCustomRemoteHead = true;
                break;
            }
        }

        private void InitBaseHead(Level parent, dc.h2d.Object attachParent, Ref<bool> fromUI1)
        {
            if (usesCustomRemoteHead)
            {
                base.init(parent, attachParent, fromUI1);
                return;
            }

            dc.User? user = null;
            dc.String? savedHeadSkin = null;
            bool restoreHeadSkin = false;
            try
            {
                user = dc.Main.Class.ME?.user;
                if (user != null)
                {
                    savedHeadSkin = user.heroHeadSkin;
                    restoreHeadSkin = true;
                    user.heroHeadSkin = null!;
                }

                base.init(parent, attachParent, fromUI1);
            }
            finally
            {
                if (restoreHeadSkin && user != null)
                {
                    try { user.heroHeadSkin = savedHeadSkin; } catch { }
                }
            }
        }

        private void RebuildHeadParticles(dc.h2d.Object particleParent, dc.h2d.Tile? material)
        {
            if (material == null)
            {
                return;
            }

            if (this.pool != null)
            {
                this.pool.dispose();
            }
            this.pool = new ParticlePool(material, 100, 30);

            if (this.headNormalSb != null && this.headNormalSb.parent != null)
            {
                this.headNormalSb.parent.removeChild(this.headNormalSb);
            }
            if (this.headAddSb != null && this.headAddSb.parent != null)
            {
                this.headAddSb.parent.removeChild(this.headAddSb);
            }

            this.headNormalSb = new HSpriteBatch(material, particleParent);
            this.headNormalSb.hasRotationScale = true;

            this.headAddSb = new HSpriteBatch(material, particleParent);
            this.headAddSb.hasRotationScale = true;
            this.headAddSb.blendMode = new dc.h2d.BlendMode.Add();
        }
        public override void updateHeadFx(double c1)
        {
            if (king == null)
            {
                return;
            }

            var sprite = king.spr;
            double headX;
            double headY;
            if (!TryGetHeadSkeletonPosition(sprite, out headX, out headY))
            {
                if (this.forcedPos == null)
                {
                    return;
                }

                UpdateHeadFxWithKingContext(c1);
                return;
            }

            if (sprite != null && UseLocalSpace())
            {
                TrySetForcedPos(headX - sprite.x, headY - sprite.y);
            }
            else
            {
                TrySetForcedPos(headX, headY);
            }
            UpdateHeadFxWithKingContext(c1);
            if (usesCustomRemoteHead)
                this.customHeadFx();
        }

        private void UpdateHeadFxWithKingContext(double c1)
        {
            var hero = me;
            var ghost = king;
            if (hero == null || ghost == null || ghost.spr == null)
            {
                return;
            }

            var savedLastHeadPos = hero.lastHeadPos;
            bool savedHeroVisible;
            try { savedHeroVisible = hero.visible; } catch { savedHeroVisible = true; }
            bool ghostVisible;
            try { ghostVisible = ghost.visible; } catch { ghostVisible = true; }
            if (kingLastHeadPos == null)
            {
                kingLastHeadPos = new FPoint(0, 0);
            }
            hero.lastHeadPos = kingLastHeadPos;
            try { hero.visible = ghostVisible; } catch { }

            try
            {
                base.updateHeadFx(c1);
                this.postUpdate();

                // Never force KingSkin head visible. Only force-hide when ghost is hidden.
                if (!ghostVisible)
                {
                    try { this.customHeadSpr?.set_visible(false); } catch { }
                    try { this.customBackSpr?.set_visible(false); } catch { }
                    try { this.headNormalSb?.set_visible(false); } catch { }
                    try { this.headAddSb?.set_visible(false); } catch { }
                    try { this.headBlack = 0; } catch { }
                    try { this.eye?.set_visible(false); } catch { }
                }
            }
            finally
            {
                kingLastHeadPos = hero.lastHeadPos;
                hero.lastHeadPos = savedLastHeadPos;
                try { hero.visible = savedHeroVisible; } catch { }
            }
        }

        private bool TryGetHeadSkeletonPosition(HSprite? sprite, out double headX, out double headY)
        {
            headX = 0;
            headY = 0;

            if (sprite == null)
            {
                return false;
            }

            var groupName = sprite.groupName?.ToString() ?? string.Empty;
            if (headSkeleton == null || !string.Equals(_cachedHeadSkeletonGroup, groupName, StringComparison.Ordinal))
            {
                headSkeleton = ResolveHeadSkeleton(sprite);
                _cachedHeadSkeletonGroup = groupName;
            }
            if (headSkeleton == null)
            {
                return false;
            }

            var frameData = sprite.frameData;
            var pivot = sprite.pivot;
            if (frameData == null || pivot == null)
            {
                return false;
            }

            int dir = king?.dir ?? 1;
            int frame = sprite.frame;
            headX = sprite.x - frameData.realWid * pivot.centerFactorX;
            headX += AnimationTrack_Impl_.Class.x(headSkeleton, frame);
            headY = sprite.y - frameData.realHei * pivot.centerFactorY - 3;
            headY += AnimationTrack_Impl_.Class.y(headSkeleton, frame);
            return true;
        }

        private ArrayBytes_Int? ResolveHeadSkeleton(HSprite sprite)
        {
            var tracks = king?.animationTracks;
            var groupName = sprite.groupName;
            if (tracks == null || groupName == null)
            {
                return null;
            }

            var groupTracks = tracks.get(groupName) as StringMap;
            if (groupTracks == null)
            {
                return null;
            }

            return groupTracks.get("headBone".AsHaxeString()) as ArrayBytes_Int;
        }

        private bool UseLocalSpace()
        {
            if (useLocalSpace.HasValue)
            {
                return useLocalSpace.Value;
            }

            var hero = me;
            var heroHead = hero?.heroHead;
            var heroSprite = hero?.spr;
            if (hero == null || heroHead == null || heroSprite == null)
            {
                useLocalSpace = true;
                return true;
            }

            var forced = heroHead.forcedPos;
            if (forced == null)
            {
                useLocalSpace = true;
                return true;
            }

            var heroHeadX = hero.get_headX();
            var heroHeadY = hero.get_headY();
            var localX = heroHeadX - heroSprite.x;
            var localY = heroHeadY - heroSprite.y;
            var distLocal = global::System.Math.Abs(forced.x - localX) + global::System.Math.Abs(forced.y - localY);
            var distWorld = global::System.Math.Abs(forced.x - heroHeadX) + global::System.Math.Abs(forced.y - heroHeadY);
            useLocalSpace = distLocal <= distWorld;
            return useLocalSpace.Value;
        }

        private void TrySetForcedPos(double x, double y)
        {
            if (global::System.Math.Abs(_lastForcedPosX - x) <= ForcedPositionEpsilon &&
                global::System.Math.Abs(_lastForcedPosY - y) <= ForcedPositionEpsilon)
            {
                return;
            }

            this.setForcedPos(x, y);
            _lastForcedPosX = x;
            _lastForcedPosY = y;
        }

    }



}
