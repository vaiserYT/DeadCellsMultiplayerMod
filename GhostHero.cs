using System;
using System.Reflection;
using dc.en;
using dc.haxe;
using dc.pr;
using HaxeProxy.Runtime;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using ModCore.Utitities;

namespace DeadCellsMultiplayerMod
{
    internal class GhostHero
    {
        private readonly dc.pr.Game _game;
        private readonly Hero _me;
        private Hero? _companion;

        public GhostHero(dc.pr.Game game, Hero me)
        {
            _game = game;
            _me = me;
        }

        public Hero? Companion => _companion;

        public Hero CreateGhost()
        {
            _companion = Hero.Class.create(_game, "Beheaded".AsHaxeString());
            _companion.init();
            _companion.awake = false;

            _companion.set_level(_me._level);
            _companion.set_team(_me._team);
            _companion.createHead();
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

        public void SetLabel(string? text, int? color = null, dc.ui.Text? target = null)
        {
            if (_companion == null) return;

            var labelText = text ?? string.Empty;
            var colorValue = color ?? 0xFFFFFF;

            try
            {
                var methods = _companion.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var method in methods)
                {
                    if (!string.Equals(method.Name, "setLabel", StringComparison.Ordinal)) continue;

                    var parameters = method.GetParameters();
                    object?[] args;
                    switch (parameters.Length)
                    {
                        case 3:
                            args = new object?[] { target, labelText, colorValue };
                            break;
                        case 2:
                            args = new object?[] { labelText, colorValue };
                            break;
                        case 1:
                            args = new object?[] { labelText };
                            break;
                        default:
                            continue;
                    }

                    method.Invoke(_companion, args);
                    return;
                }
            }
            catch
            {
            }

            if (target != null)
            {
                try
                {
                    target.set_text(labelText.AsHaxeString());
                    target.set_textColor(colorValue);
                }
                catch
                {
                }
            }
        }
    }
}
