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
        private MainPageLightingInitializer? _lobbyLighting;
        private readonly List<string> _lobbyBeheadedSkinIds = new();
        private readonly List<bool> _lobbyBeheadedSilhouette = new();
        private bool _lobbyBeheadedNeedsSkinRebind;

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
            this._lobbyBeheadedSkinIds.Clear();
            this._lobbyBeheadedSilhouette.Clear();
            this._lobbyBeheadedNeedsSkinRebind = false;
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

            Log.Information(
                "[ConnectionUI] Lobby beheaded row create slots={SlotCount} panelW={PanelW:0.#} scale={Scale:0.###} lighting={Lighting}",
                slotCount,
                panelW,
                scale,
                this._lobbyLighting != null);

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

                    Log.Information(
                        "[ConnectionUI] Lobby beheaded[{Index}] create nick={Nick} occupied={Occupied} connecting={Connecting} host={Host} you={You} requestedSkin={RequestedSkin} effectiveSkin={EffectiveSkin} anim={Anim} silhouette={Silhouette}",
                        i,
                        slot.Nick ?? string.Empty,
                        occupied,
                        slot.IsConnecting,
                        slot.IsHost,
                        slot.IsYou,
                        slot.Skin ?? string.Empty,
                        skinId,
                        ResolveLobbyBeheadedAnim(i),
                        !occupied);

                    var spr = CreateLobbyBeheaded(skinId, i, scale);
                    if (spr == null)
                    {
                        Log.Warning("[ConnectionUI] Lobby beheaded[{Index}] create returned null skin={Skin}", i, skinId);
                    }
                    else
                    {
                        this._lobbyBeheadedRoot.addChild(spr);
                        spr.x = sprX;
                        spr.y = sprY;
                        // ColorMap must be bound after the sprite is in the live scene tree.
                        // Applying it on a detached HSprite leaves the raw default atlas on screen
                        // until a later Hero/run init compiles the title-screen shader combo.
                        ApplyLobbyBeheadedSkin(spr, skinId, ResolveLobbyBeheadedAnim(i), i, "create");
                        if (!occupied)
                            ApplyLobbyBeheadedSilhouette(spr);
                        this.sprites.Add(spr);
                        this._lobbyBeheadedSkinIds.Add(skinId);
                        this._lobbyBeheadedSilhouette.Add(!occupied);
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

            try { this._lobbyBeheadedRoot.set_visible(true); } catch { }
            this._lobbyBeheadedNeedsSkinRebind = this.sprites.Count > 0;
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

        private string ResolveLobbyBeheadedAnim(int index)
        {
            return index >= 0 && index < this.animlist.Count ? this.animlist[index] : "idle";
        }

        private HSprite? CreateLobbyBeheaded(string skinId, int index, double scale)
        {
            if (string.IsNullOrWhiteSpace(skinId))
                skinId = DefaultLobbySkin;

            string skinanim = ResolveLobbyBeheadedAnim(index);
            if (!TryResolveLobbySkinInfo(ref skinId, out var skinInfo) || skinInfo == null)
                return null;

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

            AnimManager animManager = spr.get_anim().play(skinanim.AsHaxeString(), null, null).loop(null);
            animManager.genSpeed = 0.4;

            double absScale = System.Math.Abs(scale);
            // Default idle faces right; negative scaleX faces left.
            spr.scaleX = -absScale;
            spr.scaleY = absScale;
            try { spr.smooth = false; } catch { }
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
            PushLobbyBeheadedLighting();
        }

        private void PushLobbyBeheadedLighting()
        {
            try
            {
                this._lobbyLighting ??= new MainPageLightingInitializer(this);
                this._lobbyLighting.Apply(this);
            }
            catch (Exception ex)
            {
                Log.Warning("[ConnectionUI] lobby lighting init failed: {Message}", ex.Message);
            }
        }

        private void FlushPendingLobbyBeheadedSkinApply()
        {
            if (!this._lobbyBeheadedNeedsSkinRebind)
                return;

            this._lobbyBeheadedNeedsSkinRebind = false;
            Log.Information("[ConnectionUI] Lobby beheaded skin rebind count={Count}", this.sprites.Count);
            PushLobbyBeheadedLighting();

            int count = System.Math.Min(this.sprites.Count, this._lobbyBeheadedSkinIds.Count);
            for (int i = 0; i < count; i++)
            {
                var spr = this.sprites[i];
                if (spr == null)
                    continue;

                ApplyLobbyBeheadedSkin(spr, this._lobbyBeheadedSkinIds[i], ResolveLobbyBeheadedAnim(i), i, "rebind");
                if (i < this._lobbyBeheadedSilhouette.Count && this._lobbyBeheadedSilhouette[i])
                    ApplyLobbyBeheadedSilhouette(spr);
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
            ApplyLobbyBeheadedSkin(this.spriteui, colorMap, animGroup, -1, "initColorMap");
        }

        private static bool TryResolveLobbySkinInfo(
            ref string skinId,
            out virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_? skinInfo)
        {
            skinInfo = null;
            if (string.IsNullOrWhiteSpace(skinId))
                skinId = DefaultLobbySkin;

            try
            {
                skinInfo = Cdb.Class.getSkinInfo(skinId.AsHaxeString());
                if (skinInfo != null)
                    return true;
            }
            catch (Exception ex)
            {
                Log.Information("[ConnectionUI] getSkinInfo({Skin}) failed: {Message}", skinId, ex.Message);
            }

            // Same object GameDataSync/GhostKing use after reading user.heroSkin:
            // getHeroSkinInfos() already is the CDB row. consoleCmdId is not a getSkinInfo key.
            if (_ConnectionUI.TryGetCachedLocalSkinInfo(out var cached) && cached != null)
            {
                skinInfo = cached;
                return true;
            }

            skinId = DefaultLobbySkin;
            try
            {
                skinInfo = Cdb.Class.getSkinInfo(DefaultLobbySkin.AsHaxeString());
                return skinInfo != null;
            }
            catch (Exception ex)
            {
                Log.Warning("[ConnectionUI] getSkinInfo({Skin}) failed: {Message}", DefaultLobbySkin, ex.Message);
                return false;
            }
        }

        private void ApplyLobbyBeheadedSkin(HSprite spr, string? skinId, string? animGroup, int index, string reason)
        {
            if (spr == null)
                return;

            string requestedSkin = skinId ?? string.Empty;
            string resolvedSkin = string.IsNullOrWhiteSpace(skinId) ? DefaultLobbySkin : skinId;
            if (!TryResolveLobbySkinInfo(ref resolvedSkin, out var skinInfo) || skinInfo == null)
            {
                Log.Warning(
                    "[ConnectionUI] Lobby beheaded[{Index}] {Reason} skin resolve failed requested={RequestedSkin}",
                    index,
                    reason,
                    requestedSkin);
                return;
            }

            Log.Information(
                "[ConnectionUI] Lobby beheaded[{Index}] {Reason} requestedSkin={RequestedSkin} resolvedSkin={ResolvedSkin} cmd={Cmd} colorMap={ColorMap} model={Model} group={Group} anim={Anim}",
                index,
                reason,
                requestedSkin,
                resolvedSkin,
                SafeSkinField(() => skinInfo.consoleCmdId?.ToString()),
                SafeSkinField(() => skinInfo.colorMap.ToString()),
                SafeSkinField(() => skinInfo.model.ToString()),
                SafeSkinField(() => skinInfo.group.ToString()),
                animGroup ?? "idle");

            PushLobbyBeheadedLighting();
            this.spriteui = spr;

            try
            {
                dc.shader.ColorMap existing = (dc.shader.ColorMap)spr.getShader(dc.shader.ColorMap.Class);
                if (existing != null)
                    spr.removeShader(existing);

                DirLighted existingLight = (DirLighted)spr.getShader(DirLighted.Class);
                if (existingLight != null)
                    spr.removeShader(existingLight);

                NormalMap existingNormal = (NormalMap)spr.getShader(NormalMap.Class);
                if (existingNormal != null)
                    spr.removeShader(existingNormal);
            }
            catch
            {
            }

            try
            {
                SpriteLib heroLib = Assets.Class.getHeroLib(skinInfo);
                if (heroLib != null && !ReferenceEquals(spr.lib, heroLib))
                {
                    int startFrame = 0;
                    bool stopAllAnims = true;
                    spr.set(heroLib, (animGroup ?? "idle").AsHaxeString(), Ref<int>.From(ref startFrame), Ref<bool>.From(ref stopAllAnims));
                }

                dc.h3d.mat.Texture? heroColorMap = ResolveLobbyHeroColorMap(skinInfo, resolvedSkin);
                if (heroColorMap == null)
                {
                    Log.Warning(
                        "[ConnectionUI] Lobby beheaded[{Index}] {Reason} getHeroColorMap null skin={Skin} lib={Lib}",
                        index,
                        reason,
                        resolvedSkin,
                        spr.lib != null);
                    return;
                }

                EnsureLobbyColorMapTextureReady(heroColorMap);
                int mapW = 0;
                int mapH = 0;
                try { mapW = heroColorMap.width; } catch { }
                try { mapH = heroColorMap.height; } catch { }

                spr.addShader(new dc.shader.ColorMap(heroColorMap));
                spr.addShader(new DirLighted());

                dc.h3d.mat.Texture? normalMap = null;
                try
                {
                    string group = string.IsNullOrWhiteSpace(animGroup) ? "idle" : animGroup;
                    normalMap = spr.lib?.getNormalMapFromGroup(group.AsHaxeString());
                }
                catch
                {
                    try { normalMap = spr.lib?.getNormalMapFromSprite(spr); } catch { }
                }

                if (normalMap != null)
                {
                    try
                    {
                        spr.addOrUpdateNormalMapTexture(normalMap);
                    }
                    catch
                    {
                        spr.addShader(new NormalMap(normalMap));
                    }

                    if (spr.getShader(NormalMap.Class) == null)
                        spr.addShader(new NormalMap(normalMap));
                }

                try { spr.smooth = false; } catch { }

                bool hasColor = false;
                bool hasLight = false;
                bool hasNormal = false;
                try { hasColor = spr.getShader(dc.shader.ColorMap.Class) != null; } catch { }
                try { hasLight = spr.getShader(DirLighted.Class) != null; } catch { }
                try { hasNormal = spr.getShader(NormalMap.Class) != null; } catch { }

                Log.Information(
                    "[ConnectionUI] Lobby beheaded[{Index}] {Reason} applied skin={Skin} lib={Lib} colorMapTex={MapW}x{MapH} shaders color={HasColor} dirLight={HasLight} normal={HasNormal} visible={Visible} parent={HasParent}",
                    index,
                    reason,
                    resolvedSkin,
                    spr.lib != null,
                    mapW,
                    mapH,
                    hasColor,
                    hasLight,
                    hasNormal,
                    SafeBool(() => spr.visible),
                    spr.parent != null);
            }
            catch (Exception ex)
            {
                Log.Warning(
                    "[ConnectionUI] Lobby beheaded[{Index}] {Reason} initColorMap({Skin}) failed: {Message}",
                    index,
                    reason,
                    resolvedSkin,
                    ex.Message);
            }
        }

        private static string SafeSkinField(Func<string?> getter)
        {
            try
            {
                var value = getter();
                return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
            }
            catch
            {
                return "?";
            }
        }

        private static bool SafeBool(Func<bool> getter)
        {
            try { return getter(); }
            catch { return false; }
        }

        private static dc.h3d.mat.Texture? ResolveLobbyHeroColorMap(
            virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_ skinInfo,
            string skinId)
        {
            try
            {
                var heroColorMap = Assets.Class.getHeroColorMap(skinInfo);
                if (heroColorMap != null)
                    return heroColorMap;
            }
            catch (Exception ex)
            {
                Log.Warning("[ConnectionUI] getHeroColorMap({Skin}) failed: {Message}", skinId, ex.Message);
            }

            return null;
        }

        private static void EnsureLobbyColorMapTextureReady(dc.h3d.mat.Texture texture)
        {
            if (texture == null)
                return;

            try { _ = texture.width; } catch { }
            try { _ = texture.height; } catch { }
        }
    }
}
