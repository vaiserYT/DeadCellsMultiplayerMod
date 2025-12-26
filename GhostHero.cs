using dc.en;
using dc.pr;
using HaxeProxy.Runtime;
using ModCore.Utitities;
using Serilog;
using dc.en;
using dc.tool;
using dc.h3d.mat;
using dc.libs.heaps.slib;



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
            king = new KingSkin(level, (int)_me.spr.x + 10, (int)_me.spr.y);
            king.init();
            king.set_level(level);
            king.set_team(_me._team);
            king.setPosCase(_me.cx, _me.cy, _me.xr, _me.yr);
            king.visible = true;
            king.initGfx();
            Log.Debug($"king.initDone = {king.initDone}");
            return king;
        }

        public void TeleportKing(int x, int y, double? xr, double? yr)
        {
            king.setPosCase(x, y, xr, yr);
        }


        public void Teleport(int x, int y, double? xr, double? yr)
        {
            king?.setPosCase(x, y, xr, yr);
        }

        public void TeleportByPixels(double x, double y)
        {
            king?.setPosPixel(x, y);
        }

        public void SetLabel(string? text, int? color = null)
        {
            if (king == null || _me == null) return;
            king.say(text.AsHaxeString(), 0, 0, 8);
        //    _Assets _Assets = Assets.Class;
            // dc.h2d.Text text_h2d = _Assets.makeText(text.AsHaxeString(), dc.ui.Text.Class.COLORS.get("ST".AsHaxeString()), true, _companion.spr);

            
        }
    }
}
