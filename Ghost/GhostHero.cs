using dc.en;
using dc.pr;
using ModCore.Utilities;
using Serilog;
using dc;
using HaxeProxy.Runtime;
using dc.shader;
using dc.hl.types;
using Hashlink.Virtuals;
using dc.libs.heaps.slib;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
namespace DeadCellsMultiplayerMod
{
    public class GhostHero
    {
        private sealed class LabelState
        {
            public dc.ui.Text Label { get; }
            public string TextValue { get; set; }
            public int TextLength { get; set; }

            public LabelState(dc.ui.Text label, string textValue)
            {
                Label = label;
                TextValue = textValue;
                TextLength = textValue.Length;
            }
        }

        private const double NickScaleWindowed = 0.8;
        private const double NickScaleFullscreen = 0.5;
        private const int WindowedDisplayMode = 0;
        private const int FullscreenDisplayMode = 1;
        private const int BorderlessDisplayMode = 2;

        private readonly Hero _me;
        private static ILogger? _log;
        private readonly Dictionary<Entity, LabelState> _labels = new();
        private readonly List<Entity> _staleLabels = new();
        private static int _cachedDisplayMode = int.MinValue;
        private static int _cachedFullScreenMode = int.MinValue;
        private static double _cachedNicknameScale = NickScaleWindowed;

        private const double RestartFrameIndex = 0;

        public int PlayerId { get; }

        public GhostKing king = null!;
        public KingHead.Kinghead kinghead = null!;


        public GhostHero(
        int playerId,
        dc.pr.Game game,
        Hero me,
        ILogger logger,
        ModEntry entry)
        {
            PlayerId = playerId;
            _ = game;
            _ = entry;
            _me = me;
            _log = logger;
        }


        public GhostKing CreateGhostKing(Level level, string? label = null)
        {

            king = new GhostKing(level, (int)-1000, (int)-1000);
            king.init();
            king.set_level(level);
            king.set_team(level.teamHero);
            king._targetable = true;
            king.hasWineGlass = false;
            king.lifeBarAbove = true;
            king.initLife(100, 100);
            king.hasRepelling = true;
            king.collisionMode = new CollisionMode.Normal();
            king.hasEntityTouchChecks = true;
            king.onActivate(_me, true);
            king.canBeActivated(_me);
            king.needsLongPress = true;
            king.hasEntityTouchChecks = true;


            bool sics = false;
            king.enableAllPhysics(Ref<bool>.From(ref sics));
            king.visible = true;
            var miniMap = ModEntry.miniMap;
            if (miniMap != null && _me._level == king._level)
            {
                miniMap.track(king, 14888237, "minimapHero".AsHaxeString(), null, true, null, null, null);
            }
            if (!string.IsNullOrWhiteSpace(label))
                SetLabel(king, label);
            king.spr._animManager.play("idle".AsHaxeString(), null, null).loop(null);
            return king;
        }

        public void disposeKing(GhostKing k)
        {
            if (k.spr != null)
            {
                ColorMap shader = (ColorMap)k.spr.getShader(ColorMap.Class);

                if (shader != null)
                {
                    k.spr.removeShader(shader);
                    k.spr.lib = null;
                }
            }

            if (k.spriteClones != null)
            {
                int num = 0;
                ArrayObj arrayObj = k.spriteClones;
                for (; ; )
                {
                    int length = arrayObj.length;
                    if (num >= length)
                    {
                        break;
                    }
                    length = arrayObj.length;
                    virtual_e_followHead_notActualClone_offX_offY_scaleBonus_? virtual_e_followHead_notActualClone_offX_offY_scaleBonus_;
                    if (num >= length)
                    {
                        virtual_e_followHead_notActualClone_offX_offY_scaleBonus_ = null;
                    }
                    else
                    {
                        virtual_e_followHead_notActualClone_offX_offY_scaleBonus_ = (virtual_e_followHead_notActualClone_offX_offY_scaleBonus_)arrayObj.array[num]!;
                    }
                    num++;
                    HSprite hsprite = virtual_e_followHead_notActualClone_offX_offY_scaleBonus_!.e;
                    if (hsprite != null)
                    {
                        if (hsprite.parent != null)
                        {
                            hsprite.parent.removeChild(hsprite);
                        }
                    }
                }
            }

            if (k.speechSfxDeck != null)
            {
                k.speechSfxDeck.clear();
            }

            if (k.runAnims != null)
            {
                k.runAnims = null;
            }
            k.removeAllLights(true);
            k.disposeGfx();
            k.destroy();
            k.dispose();

        }

        public void SetLabel(Entity entity, string? text)
        {
            if (entity == null) return;
            var normalizedText = string.IsNullOrWhiteSpace(text) ? "Guest" : text;
            if (_labels.TryGetValue(entity, out var existing))
            {
                if (existing.Label.parent != null)
                {
                    if (!string.Equals(existing.TextValue, normalizedText, StringComparison.Ordinal))
                    {
                        try { existing.Label.set_text(normalizedText.AsHaxeString()); } catch { }
                        existing.TextValue = normalizedText;
                        existing.TextLength = normalizedText.Length;
                    }
                    return;
                }

                try { existing.Label.remove(); } catch { }
                _labels.Remove(entity);
            }
            _Assets _Assets = Assets.Class;
            var nicknameColor = dc.ui.Text.Class.COLORS.get("ST".AsHaxeString());
            dc.ui.Text text_h2d = _Assets.makeText(normalizedText.AsHaxeString(), nicknameColor, null, entity.spr);
            var targetScale = GetNicknameScale();
            text_h2d.y -= 80;
            text_h2d.x -= 2.5 * normalizedText.Length;
            text_h2d.alpha = 0.8;
            text_h2d.customScale = targetScale;
            text_h2d.onResize();
            text_h2d.textColor = nicknameColor;
            _labels[entity] = new LabelState(text_h2d, normalizedText);
        }

        public void UpdateLabels()
        {
            if (_labels.Count == 0) 
            {
                return;
            } 
            var targetScale = GetNicknameScale();
            _staleLabels.Clear();
            foreach (var pair in _labels)
            {
                var entity = pair.Key;
                var state = pair.Value;
                var label = state.Label;
                if (entity == null || label == null || entity.spr == null || label.parent == null)
                {
                    if (entity != null)
                        _staleLabels.Add(entity);
                    continue;
                }

                var targetX = -2.5 * state.TextLength;
                var targetY = -80;
                label.customScale = targetScale;
                label.onResize();
                if (entity.dir < 0)
                {
                    label.scaleX = -label.scaleX;
                    label.x = -targetX;
                }
                else
                {
                    label.x = targetX;
                }
                label.y = targetY;
            }

            if (_staleLabels.Count == 0) return;
            for (int i = 0; i < _staleLabels.Count; i++)
            {
                _labels.Remove(_staleLabels[i]);
            }
        }

        private static double GetNicknameScale()
        {
            try
            {
                var win = dc.hxd.Window.Class.getInstance();
                if (win != null)
                {
                    var displayMode = int.MinValue;
                    var sdlWin = win.window;
                    if (sdlWin != null)
                        displayMode = sdlWin.displayMode;

                    var mode = win.fullScreenMode;
                    if (_cachedDisplayMode == displayMode && _cachedFullScreenMode == mode)
                        return _cachedNicknameScale;

                    _cachedDisplayMode = displayMode;
                    _cachedFullScreenMode = mode;
                    _cachedNicknameScale = ResolveNicknameScale(displayMode, mode);
                    return _cachedNicknameScale;
                }
            }
            catch
            {
            }

            return _cachedNicknameScale;
        }

        private static double ResolveNicknameScale(int displayMode, int fullScreenMode)
        {
            if (displayMode == FullscreenDisplayMode || displayMode == BorderlessDisplayMode)
                return NickScaleFullscreen;
            if (displayMode == WindowedDisplayMode)
                return NickScaleWindowed;

            if (fullScreenMode == FullscreenDisplayMode || fullScreenMode == BorderlessDisplayMode)
                return NickScaleFullscreen;
            if (fullScreenMode == WindowedDisplayMode)
                return NickScaleWindowed;

            return NickScaleWindowed;
        }

    }
}
