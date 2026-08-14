using System;
using dc;
using dc.h2d;
using dc.haxe.ds;
using dc.hl.types;
using dc.hxd;
using dc.libs.heaps.slib;
using dc.libs.heaps.slib._AnimManager;
using dc.shader;
using dc.tool._AnimationTrack;
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
    /// Uses the title-screen shader stack (ColorMap + DirLighted + NormalMap). ColorMap alone is not cached.
    /// </summary>
    public partial class ConnectionUI
    {
        private const string DefaultLobbySkin = "PrisonerDefault";
        private const string DefaultLobbyHead = "BaseFlame";

        /// <summary>Log each head's detail once per session so lobby rebuilds don't spam the log.</summary>
        private static readonly System.Collections.Generic.HashSet<string> _loggedHeadIds = new();
        private static readonly System.Collections.Generic.HashSet<string> _loggedHeadBone = new();

        /// <summary>Four lobby beheaded seats under / beside the lobby code card.</summary>
        private dc.h2d.Object? _lobbyBeheadedRoot;
        private MainPageLightingInitializer? _lobbyLighting;
        private readonly List<string> _lobbyBeheadedSkinIds = new();
        private readonly List<string> _lobbyBeheadedHeadIds = new();
        private readonly List<dc.h2d.Object> _lobbyHeadSprites = new();
        private readonly List<bool> _lobbyBeheadedSilhouette = new();
        private readonly List<int> _lobbyBodyLastAnimCursor = new();
        private bool _lobbyBeheadedNeedsSkinRebind;
        private static readonly Dictionary<string, StringMap?> _lobbyAnimTracksByModel = new();

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
            this._lobbyBeheadedHeadIds.Clear();
            for (int i = 0; i < this._lobbyHeadSprites.Count; i++)
            {
                try { this._lobbyHeadSprites[i]?.remove(); } catch { }
            }
            this._lobbyHeadSprites.Clear();
            LobbyHeadFx.ClearAll();
            this._lobbyBeheadedSilhouette.Clear();
            this._lobbyBodyLastAnimCursor.Clear();
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
            const double approxTileHeight = 80.0;
            const double nickGap = 6.0;
            const double boxPadX = 20.0;
            const double boxPadY = 28.0;
            // Whole row on screen (negative = left).
            const double rootXNudge = -5.0;
            // Plate vs beheaded — tune so the art sits in the middle of the box.
            // Negative X = box left; negative Y = box up.
            const double boxOffsetX = 5.0;
            const double boxOffsetY = -95.0;
            const double headOffsetY = -48.0;

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
                    string headId = occupied ? slot.Head : DefaultLobbyHead;
                    if (string.IsNullOrWhiteSpace(headId))
                        headId = DefaultLobbyHead;

                    var spr = CreateLobbyBeheaded(skinId, i, scale);
                    if (spr != null)
                    {
                        this._lobbyBeheadedRoot.addChild(spr);
                        spr.x = sprX;
                        spr.y = sprY;
                        ApplyLobbyBeheadedSkin(spr, skinId, ResolveLobbyBeheadedAnim(i), i, "create");
                        if (!occupied)
                            ApplyLobbyBeheadedSilhouette(spr);
                        this.sprites.Add(spr);
                        this._lobbyBeheadedSkinIds.Add(skinId);
                        this._lobbyBeheadedHeadIds.Add(headId);
                        this._lobbyBeheadedSilhouette.Add(!occupied);
                        this._lobbyBodyLastAnimCursor.Add(-1);

                        var head = CreateLobbyHead(headId, scale);
                        if (head != null)
                        {
                            this._lobbyBeheadedRoot.addChild(head);
                            PositionLobbyHeadOnBone(head, spr, skinId, headOffsetY * uiScale, headId);
                            if (!occupied)
                                ApplyLobbyBeheadedSilhouette(head);
                            this._lobbyHeadSprites.Add(head);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[ConnectionUI] Lobby beheaded[{Index}] failed: {Message}", i, ex.Message);
                }

                string nick = ResolveLobbySlotNick(slot);
                if (string.IsNullOrWhiteSpace(nick))
                {
                    BringLobbyHeadsToFront();
                    continue;
                }

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
                BringLobbyHeadsToFront();
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
                return null;

            var spr = new HSprite(g, skinanim.AsHaxeString(), Ref<int>.Null, null);

            SpritePivot pivot = spr.pivot;
            // Center pivot so negative scaleX keeps the body in the middle of the plate.
            pivot.centerFactorX = 0.5;
            pivot.centerFactorY = 0.5;
            pivot.usingFactor = true;
            pivot.isUndefined = false;

            this.spriteui = spr;

            EnsureLobbyBodyIdleAnim(spr, skinanim);

            double absScale = System.Math.Abs(scale);
            // Default idle faces right; negative scaleX faces left.
            spr.scaleX = -absScale;
            spr.scaleY = absScale;
            try { spr.smooth = false; } catch { }
            spr.set_visible(true);
            return spr;
        }

        private static void ApplyLobbyBeheadedSilhouette(dc.h2d.Object obj)
        {
            try
            {
                if (obj == null)
                    return;

                if (obj is HSprite spr)
                {
                    var color = spr.color;
                    if (color != null)
                    {
                        // Dark silhouette, not pure black (pure black disappears on navy plates).
                        color.x = 0.18;
                        color.y = 0.18;
                        color.z = 0.22;
                    }
                }

                var children = obj.children;
                if (children == null)
                    return;
                for (int i = 0; i < children.length; i++)
                {
                    if (children.array[i] is dc.h2d.Object child)
                        ApplyLobbyBeheadedSilhouette(child);
                }
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

                if (i < this._lobbyHeadSprites.Count && this._lobbyHeadSprites[i] != null)
                {
                    if (i < this._lobbyBeheadedSilhouette.Count && this._lobbyBeheadedSilhouette[i])
                        ApplyLobbyBeheadedSilhouette(this._lobbyHeadSprites[i]);
                }
            }
        }

        internal void TickLobbyHeadBones()
        {
            int n = System.Math.Min(this.sprites.Count, this._lobbyHeadSprites.Count);
            n = System.Math.Min(n, this._lobbyBeheadedSkinIds.Count);
            double dt = GetLobbyAnimDt();
            for (int i = 0; i < n; i++)
            {
                var body = this.sprites[i];
                var head = this._lobbyHeadSprites[i];
                if (body == null || head == null)
                    continue;

                int lastCursor = i < this._lobbyBodyLastAnimCursor.Count ? this._lobbyBodyLastAnimCursor[i] : -1;
                int cursor = ReadLobbyAnimCursor(body);
                // Title HSprites only step during sync(). If the cursor did not move since last
                // tick, drive AnimManager here the same way an entity does before updateHeadFx.
                if (cursor == lastCursor)
                {
                    try { body.get_anim()?._update(dt); } catch { }
                    cursor = ReadLobbyAnimCursor(body);
                }

                while (this._lobbyBodyLastAnimCursor.Count <= i)
                    this._lobbyBodyLastAnimCursor.Add(-1);
                this._lobbyBodyLastAnimCursor[i] = cursor;

                string headId = i < this._lobbyBeheadedHeadIds.Count ? this._lobbyBeheadedHeadIds[i] : DefaultLobbyHead;
                PositionLobbyHeadOnBone(head, body, this._lobbyBeheadedSkinIds[i], fallbackLocalY: 0, headId);
            }
        }

        private double GetLobbyAnimDt()
        {
            try
            {
                if (this.tmod > 0.0)
                    return this.tmod;
            }
            catch
            {
            }

            return 1.0;
        }

        private static int ReadLobbyAnimCursor(HSprite sprite)
        {
            try
            {
                var stack = sprite.get_anim()?.stack;
                if (stack != null && stack.length > 0)
                {
                    AnimInstance? top = null;
                    try { top = stack.getDyn(0) as AnimInstance; } catch { }
                    if (top == null)
                    {
                        try { top = stack.array[0] as AnimInstance; } catch { }
                    }
                    if (top != null)
                        return top.animCursor;
                }
            }
            catch
            {
            }

            return sprite.frame;
        }

        private static void EnsureLobbyBodyIdleAnim(HSprite spr, string animGroup)
        {
            if (spr == null)
                return;
            if (string.IsNullOrWhiteSpace(animGroup))
                animGroup = "idle";

            try
            {
                AnimManager anim = spr.get_anim().play(animGroup.AsHaxeString(), null, null).loop(null);
                anim.genSpeed = 0.4;
            }
            catch
            {
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
        /// Do not strip DirLighted/NormalMap. ColorMap alone is missing from the shader cache.
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
            catch
            {
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
            catch
            {
                return false;
            }
        }

        private void ApplyLobbyBeheadedSkin(HSprite spr, string? skinId, string? animGroup, int index, string reason)
        {
            if (spr == null)
                return;

            string resolvedSkin = string.IsNullOrWhiteSpace(skinId) ? DefaultLobbySkin : skinId;
            if (!TryResolveLobbySkinInfo(ref resolvedSkin, out var skinInfo) || skinInfo == null)
                return;

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
                    bool stopAllAnims = false;
                    spr.set(heroLib, (animGroup ?? "idle").AsHaxeString(), Ref<int>.From(ref startFrame), Ref<bool>.From(ref stopAllAnims));
                }

                EnsureLobbyBodyIdleAnim(spr, animGroup ?? "idle");

                dc.h3d.mat.Texture? heroColorMap = ResolveLobbyHeroColorMap(skinInfo, resolvedSkin);
                if (heroColorMap == null)
                    return;

                EnsureLobbyColorMapTextureReady(heroColorMap);

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
            }
            catch
            {
            }
        }

        private void BringLobbyHeadsToFront()
        {
            if (this._lobbyBeheadedRoot == null)
                return;

            for (int i = 0; i < this._lobbyHeadSprites.Count; i++)
            {
                var head = this._lobbyHeadSprites[i];
                if (head == null)
                    continue;
                try { this._lobbyBeheadedRoot.addChild(head); } catch { }
            }
        }

        private static dc.h2d.Object? CreateLobbyHead(string headId, double scale)
        {
            if (string.IsNullOrWhiteSpace(headId))
                headId = DefaultLobbyHead;

            var root = new dc.h2d.Object(null);

            if (!LobbyHeadSkin.TryResolve(headId, out var atlas, out var parts, out var glowData, out var particleEffects))
            {
                AttachDefaultLobbyHeadContent(root, scale, headId);
                return root;
            }

            if (parts.Count == 0)
            {
                AttachDefaultLobbyHeadContent(root, scale, headId);
                return root;
            }

            var lib = LobbyHeadSkin.LoadAtlas(atlas);
            if (lib == null)
            {
                AttachDefaultLobbyHeadContent(root, scale, headId);
                return root;
            }

            int mainIndex = 0;
            for (int i = 1; i < parts.Count; i++)
            {
                if (parts[i].PartNumber == 0)
                {
                    mainIndex = i;
                    break;
                }
            }

            // Game layout: every part is a sibling of the head container at its (unscaled)
            // CDB offset, scaled by headScale * part.Scale. Nesting parts under a mirrored,
            // scaled root is what mis-placed the eye.
            bool firstTime = _loggedHeadIds.Add(headId);
            int added = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (!LobbyHeadSkin.TryResolveGroup(lib, part.Group, part.IdleAnim, out var group))
                    continue;

                var partSpr = CreateLobbyHeadPart(lib, group, scale * part.Scale, part, glowData);
                if (partSpr == null)
                    continue;

                try { root.addChild(partSpr); } catch { }
                added++;
                if (firstTime)
                {
                    Log.Information(
                        "[ConnectionUI] Lobby head part head={Head} part={Part} group={Group} anim={Anim} spd={Spd} offset={OX},{OY} scale={S}",
                        headId, part.PartNumber, group, part.IdleAnim ?? group, part.IdleAnimSpeed ?? 0.5,
                        part.OffsetX, part.OffsetY, global::System.Math.Round(scale * part.Scale, 3));
                }
            }

            if (added == 0)
            {
                AttachDefaultLobbyHeadContent(root, scale, headId);
                return root;
            }

            // Body faces left. Flip the whole custom-head assembly on X so part offsets
            // (eye, flame) stay on the correct side.
            try { root.scaleX = -1; } catch { }

            return root;
        }

        private static void AttachDefaultLobbyHeadContent(dc.h2d.Object root, double scale, string headId)
        {
            try { LobbyHeadFx.AttachDefaultHeadFx(root, scale, headId, mirrorX: true); } catch { }

            try
            {
                var star = CreateDefaultLobbyHeadStar(scale);
                if (star != null)
                {
                    try { root.addChild(star); } catch { }
                }
            }
            catch
            {
            }

            if (_loggedHeadIds.Add(headId))
                Log.Information("[ConnectionUI] Lobby default head head={Head} (star + particle FX)", headId);
        }

        /// <summary>
        /// Game-accurate default head: no customHead sprite exists for BaseFlame (it is particles only),
        /// so the game renders the homunculus eye (fxSmallStar, Add blend) as the visible head sprite.
        /// </summary>
        private static HSprite? CreateDefaultLobbyHeadStar(double scale)
        {
            try
            {
                SpriteLib fx = Assets.Class.fx;
                if (fx == null)
                    return null;

                var spr = new HSprite(fx, "fxSmallStar".AsHaxeString(), Ref<int>.Null, null);
                SpritePivot pivot = spr.pivot;
                pivot.centerFactorX = 0.5;
                pivot.centerFactorY = 0.5;
                pivot.usingFactor = true;
                pivot.isUndefined = false;

                try { spr.rotation = 1.57; } catch { }
                try { spr.posChanged = true; } catch { }
                try { spr.blendMode = new dc.h2d.BlendMode.Add(); } catch { }

                // The game tints the homunculus eye warm-orange (0xFFBF00) when the skill is usable.
                try
                {
                    var color = spr.color;
                    color.x = 1.0;
                    color.y = 191.0 / 255.0;
                    color.z = 0.0;
                }
                catch { }

                double absScale = System.Math.Abs(scale);
                // The homunculus eye star sits on the black head; keep it visible.
                double starScale = absScale * 0.55;
                spr.scaleX = -starScale;
                spr.scaleY = starScale;
                try { spr.smooth = false; } catch { }
                spr.set_visible(true);
                return spr;
            }
            catch
            {
                return null;
            }
        }

        private static HSprite? CreateLobbyHeadPart(
            SpriteLib lib,
            string group,
            double scale,
            LobbyHeadSkin.Part part,
            dc.hl.types.ArrayObj? glowData)
        {
            if (lib == null || string.IsNullOrWhiteSpace(group))
                return null;

            var spr = new HSprite(lib, group.AsHaxeString(), Ref<int>.Null, null);
            SpritePivot pivot = spr.pivot;
            pivot.centerFactorX = 0.5;
            pivot.centerFactorY = 0.5;
            pivot.usingFactor = true;
            pivot.isUndefined = false;

            // The violet head tint: the game colors the head parts with GradientHiLo
            // (lo = colorDark ?? colorLight, hi = colorLight). The body-skin ColorMap
            // must NOT be applied to the head — that is what rendered it gray.
            if (part.ColorDark.HasValue || part.ColorLight.HasValue)
            {
                try
                {
                    int? lo = part.ColorDark ?? part.ColorLight;
                    int? hi = part.ColorLight ?? part.ColorDark;
                    if (lo.HasValue && hi.HasValue)
                        spr.addShader(new GradientHiLo(lo.Value, hi.Value, null));
                }
                catch
                {
                }
            }

            if (glowData != null && glowData.length > 0)
            {
                try { spr.addShader(new GlowKey(glowData)); } catch { }
            }

            try
            {
                var normal = lib.getNormalMapFromSprite(spr);
                if (normal != null)
                    spr.addShader(new NormalMap(normal));
            }
            catch
            {
            }

            // Eye (part 1) is additive in the game, matching the glowing homunculus eye.
            if (part.PartNumber == 1)
            {
                try { spr.blendMode = new dc.h2d.BlendMode.Add(); } catch { }
            }

            try
            {
                AnimManager anim = spr.get_anim().play(group.AsHaxeString(), null, null).loop(null);
                anim.genSpeed = part.IdleAnimSpeed ?? 0.5;
            }
            catch
            {
            }

            double absScale = System.Math.Abs(scale);
            // Facing is applied on the head root (scaleX = -1). Parts keep a positive scale
            // so CDB offsets are mirrored with the assembly instead of flipping in place.
            spr.scaleX = absScale;
            spr.scaleY = absScale;
            spr.x = part.OffsetX;
            spr.y = part.OffsetY;
            try { spr.smooth = false; } catch { }
            spr.set_visible(true);
            return spr;
        }

        private static dc.h3d.mat.Texture? ResolveLobbyHeroColorMap(
            virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_ skinInfo,
            string skinId)
        {
            try
            {
                return Assets.Class.getHeroColorMap(skinInfo);
            }
            catch
            {
                return null;
            }
        }

        private static void EnsureLobbyColorMapTextureReady(dc.h3d.mat.Texture texture)
        {
            if (texture == null)
                return;

            try { _ = texture.width; } catch { }
            try { _ = texture.height; } catch { }
        }

        /// <summary>
        /// Same headBone placement as Kinghead: origin at the sprite frame, then the
        /// <c>headBone</c> animation track. Lobby bodies are UI-scaled, so track pixels
        /// are multiplied by abs(scale). dir comes from scaleX (left-facing = -1).
        /// </summary>
        private void PositionLobbyHeadOnBone(
            dc.h2d.Object head,
            HSprite body,
            string skinId,
            double fallbackLocalY,
            string? headId)
        {
            if (head == null || body == null)
                return;

            if (TryGetLobbyHeadBonePosition(body, skinId, out var hx, out var hy))
            {
                hy -= CustomLobbyHeadNeckLift(head, headId, body.scaleY);
                head.x = hx;
                head.y = hy;
                try { head.posChanged = true; } catch { }
                return;
            }

            if (fallbackLocalY == 0)
            {
                double lift = CustomLobbyHeadNeckLift(head, headId, body.scaleY);
                if (lift == 0)
                    return;
                head.x = body.x;
                head.y = body.y - lift;
                try { head.posChanged = true; } catch { }
                return;
            }

            head.x = body.x;
            head.y = body.y + fallbackLocalY - CustomLobbyHeadNeckLift(head, headId, body.scaleY);
            try { head.posChanged = true; } catch { }
        }

        /// <summary>
        /// Custom heads (root scaleX = -1) sit a bit low on the stump vs in-game.
        /// </summary>
        private static double CustomLobbyHeadNeckLift(dc.h2d.Object head, string? headId, double bodyScaleY)
        {
            bool custom = false;
            try
            {
                if (head != null && head.scaleX < 0)
                    custom = true;
            }
            catch
            {
            }

            if (!custom &&
                !string.IsNullOrWhiteSpace(headId) &&
                !string.Equals(headId, DefaultLobbyHead, StringComparison.Ordinal))
                custom = true;

            if (!custom)
                return 0;

            double s = System.Math.Abs(bodyScaleY);
            if (s < 0.001)
                s = 1.0;
            return 5.0 * s;
        }

        private static bool TryGetLobbyHeadBonePosition(HSprite sprite, string skinId, out double headX, out double headY)
        {
            headX = 0;
            headY = 0;
            if (sprite == null)
                return false;

            var tracks = ResolveLobbyAnimationTracks(skinId);
            if (tracks == null)
            {
                LogLobbyHeadBoneMiss(skinId, sprite, "no-tracks");
                return false;
            }

            var headSkeleton = ResolveLobbyHeadSkeleton(tracks, sprite);
            if (headSkeleton == null)
            {
                LogLobbyHeadBoneMiss(skinId, sprite, "no-headBone");
                return false;
            }

            var frameData = sprite.frameData;
            var pivot = sprite.pivot;
            if (frameData == null || pivot == null)
                return false;

            int frame = sprite.frame;
            int cursor = ReadLobbyAnimCursor(sprite);
            double dir = sprite.scaleX < 0 ? -1.0 : 1.0;
            double s = System.Math.Abs(sprite.scaleY);
            if (s < 0.001)
                s = 1.0;

            // Kinghead uses sprite.frame. Idle tracks are packed per anim step, so if the atlas
            // frame is stuck, fall back to animCursor (the timeline index).
            int trackFrame = frame;
            double x0 = AnimationTrack_Impl_.Class.x(headSkeleton, frame);
            double y0 = AnimationTrack_Impl_.Class.y(headSkeleton, frame);
            if (cursor != frame)
            {
                double x1 = AnimationTrack_Impl_.Class.x(headSkeleton, cursor);
                double y1 = AnimationTrack_Impl_.Class.y(headSkeleton, cursor);
                if (x1 != x0 || y1 != y0)
                    trackFrame = cursor;
            }

            headX = sprite.x - frameData.realWid * pivot.centerFactorX * dir * s;
            headX += AnimationTrack_Impl_.Class.x(headSkeleton, trackFrame) * dir * s;
            headY = sprite.y - frameData.realHei * pivot.centerFactorY * s - 3.0 * s;
            headY += AnimationTrack_Impl_.Class.y(headSkeleton, trackFrame) * s;

            if (_loggedHeadBone.Add(skinId + "|" + (sprite.groupName?.ToString() ?? "")))
            {
                Log.Information(
                    "[ConnectionUI] headBone skin={Skin} group={Group} frame={Frame} cursor={Cursor} trackFrame={Track} xy={X},{Y}",
                    skinId,
                    sprite.groupName?.ToString(),
                    frame,
                    cursor,
                    trackFrame,
                    global::System.Math.Round(headX, 2),
                    global::System.Math.Round(headY, 2));
            }

            return true;
        }

        private static void LogLobbyHeadBoneMiss(string skinId, HSprite sprite, string reason)
        {
            string group = sprite?.groupName?.ToString() ?? "";
            if (!_loggedHeadBone.Add("miss|" + reason + "|" + skinId + "|" + group))
                return;
            Log.Warning("[ConnectionUI] headBone miss reason={Reason} skin={Skin} group={Group}", reason, skinId, group);
        }

        private static ArrayBytes_Int? ResolveLobbyHeadSkeleton(StringMap tracks, HSprite sprite)
        {
            if (tracks == null)
                return null;

            ArrayBytes_Int? TryGroup(dc.String? key)
            {
                if (key == null)
                    return null;
                try
                {
                    var groupTracks = tracks.get(key) as StringMap;
                    return groupTracks?.get("headBone".AsHaxeString()) as ArrayBytes_Int;
                }
                catch
                {
                    return null;
                }
            }

            var bone = TryGroup(sprite.groupName);
            if (bone != null)
                return bone;

            try
            {
                var stack = sprite.get_anim()?.stack;
                if (stack != null && stack.length > 0)
                {
                    var top = stack.getDyn(0) as AnimInstance;
                    bone = TryGroup(top?.group);
                    if (bone != null)
                        return bone;
                }
            }
            catch
            {
            }

            bone = TryGroup("idle".AsHaxeString());
            if (bone != null)
                return bone;

            string playing = sprite.groupName?.ToString() ?? string.Empty;
            ArrayBytes_Int? idleBone = null;
            ArrayBytes_Int? anyBone = null;
            try
            {
                var keys = tracks.keys();
                while (keys.hasNext())
                {
                    var key = keys.next();
                    var found = TryGroup(key);
                    if (found == null)
                        continue;

                    anyBone ??= found;
                    string name = key?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(playing) &&
                        string.Equals(name, playing, StringComparison.OrdinalIgnoreCase))
                        return found;
                    if (idleBone == null && name.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0)
                        idleBone = found;
                }
            }
            catch
            {
            }

            return idleBone ?? anyBone;
        }

        private static StringMap? ResolveLobbyAnimationTracks(string skinId)
        {
            if (string.IsNullOrWhiteSpace(skinId))
                skinId = DefaultLobbySkin;

            if (_lobbyAnimTracksByModel.TryGetValue(skinId, out var cached) && cached != null)
                return cached;

            StringMap? tracks = null;
            try
            {
                string resolved = skinId;
                if (!TryResolveLobbySkinInfo(ref resolved, out var skinInfo) || skinInfo?.model == null)
                    return cached;

                dc._String _String = dc.String.Class;
                dc.String path = "atlas/".AsHaxeString();
                path = _String.__add__(_String.__add__(path, skinInfo.model), "_tracks.json".AsHaxeString());
                tracks = Assets.Class.getAnimationTracks(Res.Class.load(path));
            }
            catch (Exception ex)
            {
                Log.Warning("[ConnectionUI] Lobby headBone tracks failed skin={Skin}: {Message}", skinId, ex.Message);
            }

            if (tracks != null)
                _lobbyAnimTracksByModel[skinId] = tracks;
            return tracks;
        }
    }
}
