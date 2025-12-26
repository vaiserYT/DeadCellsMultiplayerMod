using dc.en;
using dc.pr;
using HaxeProxy.Runtime;
using ModCore.Utitities;
using Serilog;
using dc.en;



namespace DeadCellsMultiplayerMod
{
    internal class GhostHero
    {
        private readonly dc.pr.Game _game;
        private readonly Hero _me;
        private Hero? _companion;
        private static ILogger? _log;

        private KingSkin king;

        
        public GhostHero(dc.pr.Game game, Hero me)
        {
            _game = game;
            _me = me;
        }

        public Hero? Companion => _companion;

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

        public Hero? CreateGhost(Level level)
        {
            if (level == null) return null;
            _companion = Hero.Class.create(_game, "Beheaded".AsHaxeString());
            _companion.init();
            _companion.awake = false;

            _companion.set_level(level);
            _companion.set_team(_me._team);
            _companion.initGfx();
            _companion.setPosCase(_me.cx, _me.cy, _me.xr, _me.yr);
            _companion.visible = true;
            _companion.initAnims();
            _companion.wakeup(level, _me.cx, _me.cy);
            DisableHero(_companion);
            SetLabel("TEST");
            // _companion.activeSkillsManager.dispose();

            return _companion;
        }

        public void ReinitGFX(Hero h)
        {
            h.disposeGfx();
            h.initGfx();
        }
        public void DisableHero(Hero h)
        {
            if(h == null) return;
            bool disposeFlagValue = false;
            var disposeFlag = new Ref<bool>(ref disposeFlagValue);
            h.controller?.dispose(disposeFlag);
            h.mainSkillsManager.dispose();
            h.awake = false;

        }

        public void Teleport(int x, int y, double? xr, double? yr)
        {
            _companion?.setPosCase(x, y, xr, yr);
        }

        public void TeleportByPixels(double x, double y)
        {
            _companion?.setPosPixel(x, y);
        }

        public void SetLabel(string? text, int? color = null)
        {
            if (_companion == null || _me == null) return;
            _companion.say(text.AsHaxeString(), 0, 0, 8);
        //    _Assets _Assets = Assets.Class;
            // dc.h2d.Text text_h2d = _Assets.makeText(text.AsHaxeString(), dc.ui.Text.Class.COLORS.get("ST".AsHaxeString()), true, _companion.spr);

            
        }
    }
}
