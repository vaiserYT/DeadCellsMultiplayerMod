using dc.en;
using dc.pr;
using HaxeProxy.Runtime;
using ModCore.Utitities;
using Serilog;
using dc.h3d.mat;
using dc.libs.heaps.slib;
using dc;
using Hashlink.Virtuals;
using dc.hl.types;



namespace DeadCellsMultiplayerMod
{
    internal class GhostHero
    {
        private readonly dc.pr.Game _game;
        private readonly Hero _me;

        private Texture hero_nrmTex;

        private SpriteLib hero_lib;

        private dc.String hero_group;
        private static ILogger? _log;

        public KingSkin king;


        public GhostHero(dc.pr.Game game, Hero me)
        {
            _game = game;
            _me = me;
        }


        public KingSkin CreateGhostKing(Level level)
        {
            king = new KingSkin(level, (int)_me.spr.x, (int)_me.spr.y);
            king.init();
            king.set_level(level);
            king.set_team(_me._team);
            king.setPosCase(_me.cx, _me.cy, _me.xr, _me.yr);
            king.visible = true;
            king.initGfx();
            kingskinplay("PrisonerDefault");
            ModEntry.miniMap.track(king, 14888237, "minimapHero".AsHaxeString(), null, true, null, null, null);
            SetLabel(king, GameMenu.RemoteUsername);
            return king;
        }


        public void kingskinplay(string skinkmap)
        {
            dc.String group = "idle".AsHaxeString();
            SpriteLib heroLib = Assets.Class.getHeroLib(Cdb.Class.getSkinInfo(skinkmap.AsHaxeString()));
            Texture normalMapFromGroup = heroLib.getNormalMapFromGroup(group);
            int? dp_ROOM_MAIN_HERO = Const.Class.DP_ROOM_MAIN_HERO;
            king.initSprite(heroLib, group, 0.5, 0.5, dp_ROOM_MAIN_HERO, true, null, normalMapFromGroup);
            king.initColorMap(Cdb.Class.getSkinInfo(skinkmap.AsHaxeString()));
        }

        public void reInitKing(Level level)
        {
            king.disposeGfx();
            king.set_level(level);
            king.initGfx();
            kingskinplay("PrisonerDefault");
            ModEntry.miniMap.track(king, 14888237, "minimapHero".AsHaxeString(), null, true, null, null, null);
            SetLabel(king, GameMenu.RemoteUsername);
        }

        public void Teleport(int x, int y, double? xr, double? yr)
        {
            if (king == null) return;
            king?.setPosCase(x, y, xr, yr);
        }

        public void TeleportByPixels(double x, double y)
        {
            king?.setPosPixel(x, y - 0.2d);
        }

        public void PlayAnimation(string anim, int? queueAnim = null, bool? g = null)
        {
            if (king == null || king.spr == null || king.spr._animManager == null) return;
            if (string.IsNullOrWhiteSpace(anim)) return;
            king.spr._animManager.play(anim.AsHaxeString(), queueAnim, g);
        }

        public void SetLabel(Entity entity, string? text)
        {
            if (entity == null) return;
            _Assets _Assets = Assets.Class;
            dc.h2d.Text text_h2d = _Assets.makeText(text.AsHaxeString(), dc.ui.Text.Class.COLORS.get("ST".AsHaxeString()), true, entity.spr);
            text_h2d.y -= 80;
            text_h2d.x -= 15;
            text_h2d.font.size = 18;
            text_h2d.alpha = 0.8;
            text_h2d.scaleX = 0.6d;
            text_h2d.scaleY = 0.6d;
            text_h2d.textColor = 0;


        }
    }
}
