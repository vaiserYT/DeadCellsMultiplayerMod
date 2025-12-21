using System;
using System.Reflection;
using dc.en;
using dc.en.hero;
using dc.haxe;
using dc.pr;
using dc.tool.heroHeads;
using HaxeProxy.Runtime;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using ModCore.Utitities;
using Serilog.Core;
using Serilog;


namespace DeadCellsMultiplayerMod
{
    internal class GhostHero
    {
        private readonly dc.pr.Game _game;
        private readonly Hero _me;
        private Hero? _companion;
        private static ILogger? _log;

        
        public GhostHero(dc.pr.Game game, Hero me)
        {
            _game = game;
            _me = me;
        }

        public Hero? Companion => _companion;

        public Hero CreateGhost()
        {
            _companion = Hero.Class.create(_game, "Beheaded".AsHaxeString());
            _companion.heroHead = _me.heroHead;
            _companion.init();
            _companion.awake = false;

            _companion.set_level(_me._level);
            _companion.set_team(_me._team);
            SetLabel("TEST");
            _companion.initGfx();
            _companion.setPosCase(_me.cx, _me.cy, _me.xr, _me.yr);
            _companion.visible = true;
            _companion.initAnims();
            _companion.wakeup(_me._level, _me.cx, _me.cy);
            _companion.awake = false;
            bool disposeFlagValue = false;
            var disposeFlag = new Ref<bool>(ref disposeFlagValue);

            _companion.controller?.dispose(disposeFlag);
            _companion.mainSkillsManager.dispose();
            _companion.activeSkillsManager.dispose();

            return _companion;
        }

        public void Teleport(int x, int y, double? xr, double? yr)
        {
            _companion?.setPosCase(x, y, xr, yr);
        }

        public void SetLevel(Level lvl)
        {
            _companion?.set_level(lvl);
        }

        public void SetLabel(string? text, int? color = null)
        {
            if (_companion == null) return;

            var labelText = text;
            var colorValue = color ?? 0xFFFFFF;


            var text1 = new dc.ui.Text(
                _companion.heroHead.parent,
                null,
                null,
                Ref<double>.Null,
                new dc.ui.ImageVerticalAlign.Middle(),
                null);

            text1.rawText = labelText.AsHaxeString();


            var colorValueRef = new Ref<int>(ref colorValue);

            _companion.setLabel(text1, colorValue, colorValueRef);

            
        }
    }
}
