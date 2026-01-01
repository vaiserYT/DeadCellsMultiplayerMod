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
using dc.en.inter.door;



namespace DeadCellsMultiplayerMod
{
    internal class GhostHero
    {
        private readonly dc.pr.Game _game;
        private readonly Hero _me;
        private static ILogger? _log;

        private string? _lastRemoteAnim;
        private int? _lastRemoteAnimQueue;
        private bool? _lastRemoteAnimG;

        private const double RestartFrameIndex = 0;

        public KingSkin king;




        public GhostHero(dc.pr.Game game, Hero me, ILogger logger)
        {
            _game = game;
            _me = me;
            _log = logger;
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
            var miniMap = ModEntry.miniMap;
            if (miniMap != null && _me._level.map == king._level.map)
            {
                miniMap.track(king, 14888237, "minimapHero".AsHaxeString(), null, true, null, null, null);
            }
            SetLabel(king, GameMenu.RemoteUsername);

            return king;
        }


        public KingSkin reInitKing(Level level)
        {
            if (king == null)
            {
                king.init();
            }
            king.set_level(level);
            king.initGfx();
            king.visible = true;
            var miniMap = ModEntry.miniMap;
            if (miniMap != null && _me._level.map == king._level.map)
            {
                miniMap.track(king, 14888237, "minimapHero".AsHaxeString(), null, true, null, null, null);
            }
            SetLabel(king, GameMenu.RemoteUsername);
            return king;
        }
        
        public void TeleportByPixels(double x, double y)
        {
            king?.setPosPixel(x, y - 0.2d);
        }

        public void PlayAnimation(string anim, int? queueAnim = null, bool? g = null)
        {
            if (king == null || king.spr == null || king.spr._animManager == null) return;
            if (string.IsNullOrWhiteSpace(anim)) return;
            var animManager = king.spr._animManager;

            try
            {
                animManager.stopWithoutStateAnims(anim.AsHaxeString(), queueAnim);
                animManager.setFrame((int)RestartFrameIndex);
            }
            catch { }

            animManager.play(anim.AsHaxeString(), queueAnim, g);
        }

        public void HandleRemoteAnim(NetNode? net)
        {
            if (net == null || king == null || king.spr == null) return;

            if (net.TryGetRemoteAnim(out var anim, out var queueAnim, out var g) && !string.IsNullOrWhiteSpace(anim))
            {
                _lastRemoteAnim = anim;
                _lastRemoteAnimQueue = queueAnim;
                _lastRemoteAnimG = g;
                PlayAnimation(anim, queueAnim, g);
            }
        }

        public void SetLabel(Entity entity, string? text)
        {
            if (entity == null) return;
            _Assets _Assets = Assets.Class;
            dc.h2d.Text text_h2d = _Assets.makeText(text.AsHaxeString(), dc.ui.Text.Class.COLORS.get("ST".AsHaxeString()), true, entity.spr);
            text_h2d.y -= 80;
            text_h2d.x -= 2.5 * text.Length;
            text_h2d.font.size = 18;
            text_h2d.alpha = 0.8;
            text_h2d.scaleX = 0.6d;
            text_h2d.scaleY = 0.6d;
            text_h2d.textColor = 0;
        }
    }
}
