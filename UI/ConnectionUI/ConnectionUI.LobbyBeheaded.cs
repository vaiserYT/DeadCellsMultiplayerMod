using System;
using dc;
using dc.h2d;
using dc.libs.heaps.slib;
using dc.shader;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Modules;
using ModCore.Utilities;
using Serilog;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection.LightingInitializer;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using DeadCellsMultiplayerMod.Tools;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.Connection
{
    /// <summary>
    /// Lobby beheaded row: four fixed seats with UIChrome plates, hero sprites, and nicks.
    /// Uses the title-screen shader stack (ColorMap + DirLighted + NormalMap) — ColorMap alone is not cached.
    /// </summary>
    public partial class ConnectionUI
    {
        private const string DefaultLobbySkin = "PrisonerDefault";

        /// <summary>Four lobby beheaded seats under / beside the lobby code card.</summary>
        private dc.h2d.Object? _lobbyBeheadedRoot;

        private readonly List<string> animlist = new() { "idle", "idle", "idle", "idle" };

        private void ClearLobbyBeheadedSprites()
        {
            for (int i = 0; i < this.sprites.Count; i++)
            {
                try { this.sprites[i]?.remove(); } catch { }
            }
            this.sprites.Clear();

            try { this.spritesflow?.remove(); } catch { }
            this.spritesflow = null;
            this.spriteui = null;

            try { this._lobbyBeheadedRoot?.remove(); } catch { }
            this._lobbyBeheadedRoot = null;
        }

        private void HideLobbyBeheadedSprites()
        {
            if (this._lobbyBeheadedRoot == null)
                return;
            try { this._lobbyBeheadedRoot.set_visible(false); } catch { }
        }

        /// <summary>
        /// Spawns the four lobby beheaded slots (UIChrome bg + sprite + nick). This IS the players list.
        /// Position offsets are user-tuned; do not casually move the root.
        /// </summary>
        private void PlaceLobbyBeheadedUnderPlayerList(
            List<_ConnectionUI.LobbyPlayerSlot> slots,
            double panelW,
            double uiScale,
            double textUi,
            double screenPad)
        {
            ClearLobbyBeheadedSprites();
            if (this._panelRoot == null)
                return;

            // Scene lighting globals must be present for DirLighted (title-screen hero path).
            EnsureLobbyBeheadedLighting();

            // Beheaded positions are the anchor. Boxes/nicks are offset to frame them
            // (do not move the sprites to chase the plates — move the plates).
            const double beheadedScale = 2.75;
            const double gapBetweenBeheaded = 24.0;
            const double approxTileWidth = 48.0;
            const double approxTileHeight = 56.0;
            const double nickGap = 6.0;
            const double boxPadX = 20.0;
            const double boxPadY = 18.0;
            // Whole row on screen (negative = left).
            const double rootXNudge = -5.0;
            // Plate vs beheaded — tune so the art sits in the middle of the box.
            // Negative X = box left; negative Y = box up.
            const double boxOffsetX = 5.0;
            const double boxOffsetY = -75.0;

            double scale = beheadedScale * uiScale;
            double bodyW = approxTileWidth * scale;
            double bodyH = approxTileHeight * scale;
            double padX = boxPadX * uiScale;
            double padY = boxPadY * uiScale;
            double slotW = bodyW + padX * 2.0;
            double slotH = bodyH + padY * 2.0;
            double step = slotW + gapBetweenBeheaded * uiScale;
            double plateDX = boxOffsetX * uiScale;
            double plateDY = boxOffsetY * uiScale;

            this._lobbyBeheadedRoot = new dc.h2d.Object(null);
            this._panelRoot.addChild(this._lobbyBeheadedRoot);

            int slotCount = System.Math.Max(slots.Count, _ConnectionUI.LobbySlotCount);
            double rowW = step * slotCount - gapBetweenBeheaded * uiScale;
            this._lobbyBeheadedRoot.x = this._layoutW - System.Math.Max(panelW, rowW) - screenPad + rootXNudge;

            double belowCode = this._lobbyPanelHeight > 0
                ? this._lobbyPanelHeight + 12.0 * uiScale
                : 72.0 * uiScale;
            // Near the top, but not flush against the window edge.
            this._lobbyBeheadedRoot.y = screenPad * 0.85 + belowCode;

            for (int i = 0; i < slotCount; i++)
            {
                var slot = i < slots.Count ? slots[i] : _ConnectionUI.LobbyPlayerSlot.Empty;
                bool occupied = slot.Occupied && !slot.IsConnecting;
                double slotX = i * step;
                // Beheaded stays on the geometric slot center — do not nudge these.
                double sprX = slotX + slotW * 0.5;
                double sprY = slotH * 0.5;
                double plateX = slotX + plateDX;
                double plateY = plateDY;

                var plate = new Graphics(this._lobbyBeheadedRoot);
                UiChrome.DrawRaisedPlate(
                    plate,
                    plateX,
                    plateY,
                    slotW,
                    slotH,
                    FieldCornerRadius * uiScale,
                    occupied && slot.IsHost ? 0x3A5A7E : PanelInnerEdge,
                    occupied ? PanelInner : 0x1A2233,
                    occupied && slot.IsHost ? 0x4A6A8E : PanelInnerTop,
                    enabled: true);

                try
                {
                    string skinId = occupied ? slot.Skin : DefaultLobbySkin;
                    if (string.IsNullOrWhiteSpace(skinId))
                        skinId = DefaultLobbySkin;

                    var spr = CreateLobbyBeheaded(skinId, i, scale);
                    if (spr == null)
                    {
                        Log.Warning("[ConnectionUI] Lobby beheaded[{Index}] create returned null", i);
                    }
                    else
                    {
                        this._lobbyBeheadedRoot.addChild(spr);
                        spr.x = sprX;
                        spr.y = sprY;
                        if (!occupied)
                            ApplyLobbyBeheadedSilhouette(spr);
                        this.sprites.Add(spr);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[ConnectionUI] Lobby beheaded[{Index}] failed: {Message}", i, ex.Message);
                }

                string nick = ResolveLobbySlotNick(slot);
                if (string.IsNullOrWhiteSpace(nick))
                    continue;

                var nickText = Assets.Class.makeText(
                    nick.AsHaxeString(),
                    Tools.MultiColor.ColorFromHex(slot.IsConnecting ? "#9098a8" : "#e8eef7"),
                    false,
                    this._lobbyBeheadedRoot);
                double nickScale = 0.42 * textUi;
                nickText.customScale = nickScale;
                nickText.onResize();
                nickText.textColor = slot.IsConnecting ? HelpColor : (slot.IsYou ? 0xE8EEF7 : TextColor);
                try
                {
                    double textW = nickText.textWidth;
                    nickText.x = plateX + (slotW - textW) * 0.5;
                }
                catch
                {
                    nickText.x = plateX + 4.0 * uiScale;
                }
                // Nick follows the box, not the geometric slot.
                nickText.y = plateY + slotH + nickGap * uiScale;
                this.connectionLabels.Add(nickText);
            }

            Log.Information(
                "[ConnectionUI] Lobby beheaded rebuilt ({Count} seats, signature={Sig})",
                slotCount,
                this.lastLobbySlotsSignature);

            try { this._lobbyBeheadedRoot.set_visible(true); } catch { }
        }

        private static string ResolveLobbySlotNick(_ConnectionUI.LobbyPlayerSlot slot)
        {
            if (!slot.Occupied)
                return string.Empty;

            if (slot.IsConnecting)
            {
                if (string.Equals(slot.Nick, _ConnectionUI.SteamLobbyConnectingMarker, StringComparison.Ordinal))
                    return GetText.Instance.GetString("Connecting to Steam lobby...");
                return GetText.Instance.GetString("connecting...");
            }

            return slot.Nick;
        }

        private HSprite? CreateLobbyBeheaded(string skinId, int index, double scale)
        {
            if (string.IsNullOrWhiteSpace(skinId))
                skinId = DefaultLobbySkin;

            string skinanim = index >= 0 && index < this.animlist.Count ? this.animlist[index] : "idle";
            virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_ skinInfo;
            try
            {
                skinInfo = Cdb.Class.getSkinInfo(skinId.AsHaxeString());
            }
            catch (Exception ex)
            {
                Log.Warning("[ConnectionUI] getSkinInfo({Skin}) failed: {Message}; using {Fallback}", skinId, ex.Message, DefaultLobbySkin);
                skinId = DefaultLobbySkin;
                skinInfo = Cdb.Class.getSkinInfo(DefaultLobbySkin.AsHaxeString());
            }

            SpriteLib g = Assets.Class.getHeroLib(skinInfo);
            if (g == null)
            {
                Log.Warning("[ConnectionUI] getHeroLib returned null for {Skin}", skinId);
                return null;
            }

            var spr = new HSprite(g, skinanim.AsHaxeString(), Ref<int>.Null, null);

            SpritePivot pivot = spr.pivot;
            // Center pivot so negative scaleX keeps the body in the middle of the plate.
            pivot.centerFactorX = 0.5;
            pivot.centerFactorY = 0.5;
            pivot.usingFactor = true;
            pivot.isUndefined = false;

            this.spriteui = spr;
            // Must match title-screen hero linker cache:
            // Base2d + ColorMap + DirLighted + NormalMap. ColorMap alone is NOT cached → invisible.
            initColorMap(skinId, skinanim);

            AnimManager animManager = spr.get_anim().play(skinanim.AsHaxeString(), null, null).loop(null);
            animManager.genSpeed = 0.4;

            double absScale = System.Math.Abs(scale);
            // Default idle faces right; negative scaleX faces left.
            spr.scaleX = -absScale;
            spr.scaleY = absScale;
            spr.set_visible(true);
            return spr;
        }

        private static void ApplyLobbyBeheadedSilhouette(HSprite spr)
        {
            try
            {
                var color = spr.color;
                if (color == null)
                    return;
                // Dark silhouette, not pure black (pure black disappears on navy plates).
                color.x = 0.18;
                color.y = 0.18;
                color.z = 0.22;
            }
            catch
            {
            }
        }

        private void EnsureLobbyBeheadedLighting()
        {
            try
            {
                _ = new MainPageLightingInitializer(this);
            }
            catch (Exception ex)
            {
                Log.Warning("[ConnectionUI] lobby lighting init failed: {Message}", ex.Message);
            }
        }

        public void playallanims(HSprite hSprite)
        {
            try
            {
                var groups = hSprite.lib?.groups;
                if (groups == null)
                    return;

                var keysIterator = groups.keys();
                animlist.Clear();

                while (keysIterator.hasNext())
                {
                    string key = keysIterator.next().ToString();
                    if (!key.StartsWith("Atk", StringComparison.OrdinalIgnoreCase))
                        animlist.Add(key);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Title-screen beheaded path: ColorMap + DirLighted + NormalMap.
        /// Do not strip DirLighted/NormalMap — ColorMap alone is missing from the shader cache.
        /// </summary>
        public void initColorMap(string colorMap, string? animGroup = null)
        {
            if (this.spriteui == null)
                return;

            string skinId = string.IsNullOrWhiteSpace(colorMap) ? DefaultLobbySkin : colorMap;
            try
            {
                dc.shader.ColorMap existing = (dc.shader.ColorMap)this.spriteui.getShader(dc.shader.ColorMap.Class);
                if (existing != null)
                    this.spriteui.removeShader(existing);

                DirLighted existingLight = (DirLighted)this.spriteui.getShader(DirLighted.Class);
                if (existingLight != null)
                    this.spriteui.removeShader(existingLight);

                NormalMap existingNormal = (NormalMap)this.spriteui.getShader(NormalMap.Class);
                if (existingNormal != null)
                    this.spriteui.removeShader(existingNormal);
            }
            catch
            {
            }

            try
            {
                var skinInfo = Cdb.Class.getSkinInfo(skinId.AsHaxeString());
                dc.h3d.mat.Texture heroColorMap = Assets.Class.getHeroColorMap(skinInfo);
                if (heroColorMap == null)
                {
                    Log.Warning("[ConnectionUI] getHeroColorMap returned null for {Skin}", skinId);
                    return;
                }

                this.spriteui.addShader(new dc.shader.ColorMap(heroColorMap));
                this.spriteui.addShader(new DirLighted());

                dc.h3d.mat.Texture? normalMap = null;
                try
                {
                    string group = string.IsNullOrWhiteSpace(animGroup) ? "idle" : animGroup;
                    normalMap = this.spriteui.lib?.getNormalMapFromGroup(group.AsHaxeString());
                }
                catch
                {
                    try { normalMap = this.spriteui.lib?.getNormalMapFromSprite(this.spriteui); } catch { }
                }

                if (normalMap != null)
                    this.spriteui.addShader(new NormalMap(normalMap));
            }
            catch (Exception ex)
            {
                Log.Warning("[ConnectionUI] initColorMap({Skin}) failed: {Message}", skinId, ex.Message);
            }
        }
    }
}
