using dc.en;
using dc.pr;
using HaxeProxy.Runtime;
using ModCore.Utitities;
using Serilog;
using dc.h3d.mat;
using dc.libs.heaps.slib;
using dc;



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

        private KingSkin king;

        
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
            king.set_easeSpritePos(true);

            reInitKing();
            return king;
        }

        public void reInitKing()
        {
            ModEntry.miniMap.track(king, 14888237, "minimapHero".AsHaxeString(), null, true, null, null, null);
            king.set_level(_me._level);
            king.initGfx();
            SetLabel(king, GameMenu.RemoteUsername);
            king.spr.visible = true;
        }

        public void Teleport(int x, int y, double? xr, double? yr)
        {
            if (king == null) return;
            king?.setPosCase(x, y, xr, yr);
        }

        public void TeleportByPixels(double x, double y)
        {
            king?.setPosPixel(x, y-0.1d);
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
