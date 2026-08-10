using dc;
using dc.h2d;
using dc.hxd;
using dc.libs.heaps.slib;
using dc.pr;
using dc.shader;
using dc.ui;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Events;
using ModCore.Utilities;
using Serilog;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection.LightingInitializer;
using ModCore.Modules;
using DeadCellsMultiplayerMod.Tools;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.Connection
{
    /// <summary>
    /// Visual hub for the multiplayer menu. GameMenu keeps all networking/state logic; this
    /// Process renders the pretty button screens (host/join LAN &amp; Steam, lobby status, errors).
    /// GameMenu feeds it through <see cref="BeginMenu"/>/<see cref="AddPendingButton"/>/
    /// <see cref="AddPendingInfo"/>/<see cref="CommitMenu"/>, and toggles the lobby display via
    /// <see cref="ShowLobbyMode"/>.
    /// </summary>
    public class ConnectionUI :
    Process,
    IEventReceiver
    {
        // ---------------------------------------------------------------- palette
        private static readonly int PanelBorder = 0xC98A4B;   // bronze frame
        private static readonly int PanelInner = 0x14161F;    // near-black panel
        private static readonly int PanelInnerEdge = 0x2A3A5E;
        private static readonly int PanelInnerTop = 0x3A4A6E;
        private static readonly int TitleColor = 0xF7FC65;
        private static readonly int SubtitleColor = 0x919191;
        private static readonly int AccentColor = 0x59D5FF;
        private static readonly int TextColor = 0xC9C9C9;
        private static readonly int HelpColor = 0x9098A8;
        private static readonly int DisabledColor = 0x6A6A6A;
        private static readonly int DisabledPlate = 0x232327;
        private static readonly int SeparatorColor = 0x3A3A44;
        private static readonly int ErrorColor = 0xFF9090;

        private enum UiMode
        {
            Lobby,
            Menu
        }

        private sealed class PendingButton
        {
            public string Label = string.Empty;
            public string Help = string.Empty;
            public bool Enabled = true;
            public int Color = 0xFFFFFF;
            public Action? OnClick;
        }

        private sealed class PendingInfo
        {
            public string Text = string.Empty;
            public int Color = 0xFFFFFF;
        }

        // ---------------------------------------------------------------- pending menu (fed by GameMenu)
        private static readonly List<PendingButton> PendingButtons = new();
        private static readonly List<PendingInfo> PendingInfos = new();

        // ---------------------------------------------------------------- instance state
        private Flow? rootFlow;
        private UIBox? bg;
        private dc.h2d.Interactive? inter;
        private Flow? spritesflow;
        private Flow? MainTitleflow;
        private Flow? playersListWrapper;
        private readonly List<HSprite> sprites = new();
        private readonly List<dc.ui.Text> connectionLabels = new();
        private readonly List<string> lastConnections = new();
        private Flow? lobbyCodeFlow;
        private dc.ui.Text? lobbyCodeTitleLabel;
        private dc.ui.Text? lobbyIdLabel;
        private string lastLobbyIdLabelText = string.Empty;
        private UiMode _mode = UiMode.Lobby;
        private bool _keepLobbyVisible;

        // menu list rendering (absolute layout inside bg, sibling of MainTitleflow)
        private dc.h2d.Object? _menuRoot;
        private readonly List<(double X, double Y, double W, double H, Action Cb)> _menuHitRects = new();
        private bool _menuVisible;

        private static ConnectionUI? Instance;
        private HSprite? spriteui;

        public ConnectionUI(Process parent) : base(parent)
        {
            Instance = this;
            this.createRoot(parent.root);
            MainPageLightingInitializer mainPage = new MainPageLightingInitializer(this);
            this.BuildUI();
            EventSystem.AddReceiver(this);
            this.root.visible = set_visible;
        }

        public static bool set_visible
        {
            get
            {
                var instance = TryGetLiveInstance();
                return instance?.root?.visible ?? false;
            }
            set
            {
                var instance = TryGetLiveInstance();
                if (instance?.root == null)
                    return;

                try
                {
                    instance.root.visible = value;
                }
                catch
                {
                    // TitleScreen/Process teardown can invalidate root mid-return-to-menu.
                    Instance = null;
                }
            }
        }

        /// <summary>
        /// Returns the current ConnectionUI only while its Process root is still alive.
        /// </summary>
        private static ConnectionUI? TryGetLiveInstance()
        {
            var instance = Instance;
            if (instance == null)
                return null;

            try
            {
                if (instance.root == null || instance.destroyed)
                {
                    Instance = null;
                    return null;
                }
            }
            catch
            {
                Instance = null;
                return null;
            }

            return instance;
        }

        /// <summary>After gamepad connect/disconnect, window metrics can change; re-run layout.</summary>
        public static void RefreshLayoutAfterDisconnect()
        {
            try
            {
                var instance = TryGetLiveInstance();
                if (instance != null && set_visible)
                    instance.onResize();
            }
            catch
            {
            }
        }

        // ================================================================ menu screen API (called from GameMenu)

        /// <summary>Clears the pending screen, ensures the hub is visible and switches to menu mode.</summary>
        public static void BeginMenu()
        {
            PendingButtons.Clear();
            PendingInfos.Clear();
            var instance = TryGetLiveInstance();
            if (instance != null)
            {
                instance._mode = UiMode.Menu;
                instance._menuVisible = false;
            }
            set_visible = true;
        }

        /// <summary>Adds a pretty button to the pending screen.</summary>
        public static void AddPendingButton(string label, string help, bool enabled, int color, Action onClick)
        {
            PendingButtons.Add(new PendingButton
            {
                Label = label ?? string.Empty,
                Help = help ?? string.Empty,
                Enabled = enabled,
                Color = color,
                OnClick = onClick
            });
        }

        /// <summary>Adds an informational line to the pending screen.</summary>
        public static void AddPendingInfo(string text, int color)
        {
            PendingInfos.Add(new PendingInfo
            {
                Text = text ?? string.Empty,
                Color = color
            });
        }

        /// <summary>Renders the accumulated pending screen. Call once at the end of a GameMenu Show* method.</summary>
        public static void CommitMenu(bool showLobby = false)
        {
            var instance = TryGetLiveInstance();
            if (instance == null)
                return;
            instance._mode = UiMode.Menu;
            instance._keepLobbyVisible = showLobby;
            instance.RebuildMenuScreen();
        }

        /// <summary>Switches to the lobby display (player list + lobby code).</summary>
        public static void ShowLobbyMode()
        {
            var instance = TryGetLiveInstance();
            if (instance == null)
                return;
            instance._mode = UiMode.Lobby;
            instance._menuVisible = false;
            instance._keepLobbyVisible = false;
            if (instance._menuRoot != null)
            {
                try { instance._menuRoot.visible = false; } catch { }
            }
            if (instance.MainTitleflow != null)
            {
                try { instance.MainTitleflow.set_visible(true); } catch { }
            }
            if (instance.playersListWrapper != null)
            {
                try { instance.playersListWrapper.visible = true; } catch { }
            }
            instance.UpdateLobbyIdLabel(forceRefreshText: true);
        }

        // ================================================================ screen build

        private void BuildUI()
        {
            this.clean();

            this.rootFlow = new Flow(null);
            this.rootFlow.set_isVertical(true);
            this.rootFlow.set_verticalAlign(new FlowAlign.Middle());
            this.rootFlow.set_horizontalAlign(new FlowAlign.Right());

            base.root.addChild(this.rootFlow);
            this.onResize();
        }

        private void RebuildMenuScreen()
        {
            if (this.bg == null || this.MainTitleflow == null)
                return;

            var uiScale = UiScale.GetResolutionScale();
            double bgWidth = this.bg.wid;
            double bgHeight = this.bg.hei;

            // Rebuild the absolute-positioned menu list container.
            this._menuRoot?.remove();
            this._menuRoot = new dc.h2d.Object(null);
            this.bg.addChild(this._menuRoot);
            this._menuHitRects.Clear();

            double padX = 14.0 * uiScale;
            double listW = bgWidth - padX * 2.0;
            double cursorY;

            if (this._keepLobbyVisible)
            {
                // Lobby screens: keep the vanilla "Lobby menu" title + player list flow on top,
                // and render the action buttons underneath it.
                this.MainTitleflow.set_minWidth((int)bgWidth);
                this.MainTitleflow.set_minHeight((int)bgHeight);
                this.MainTitleflow.set_visible(true);
                if (this.playersListWrapper != null)
                {
                    try { this.playersListWrapper.visible = true; } catch { }
                }
                this.MainTitleflow.reflow();
                cursorY = System.Math.Max(this.MainTitleflow.get_innerHeight(), 60.0 * uiScale) + 4.0 * uiScale;
            }
            else
            {
                // Navigation screens: no own title, no player list.
                this.MainTitleflow.set_visible(false);
                if (this.playersListWrapper != null)
                {
                    try { this.playersListWrapper.visible = false; } catch { }
                }
                cursorY = 22.0 * uiScale;
            }

            // Info lines first.
            foreach (var info in PendingInfos)
            {
                var line = Assets.Class.makeText(
                    info.Text.AsHaxeString(),
                    Tools.MultiColor.ColorFromHex("#e0e0e0"),
                    false,
                    this._menuRoot);
                line.customScale = 0.42 * uiScale;
                line.onResize();
                line.textColor = info.Color;
                line.x = padX;
                line.y = cursorY;
                cursorY += 24.0 * uiScale;
            }

            if (PendingInfos.Count > 0)
            {
                DrawSeparator(cursorY, padX, listW, uiScale);
                cursorY += 16.0 * uiScale;
            }

            for (int i = 0; i < PendingButtons.Count; i++)
            {
                var btn = PendingButtons[i];
                if (i > 0)
                {
                    DrawSeparator(cursorY, padX, listW, uiScale);
                    cursorY += 12.0 * uiScale;
                }

                double btnH = (string.IsNullOrWhiteSpace(btn.Help) ? 34.0 : 50.0) * uiScale;
                double y = cursorY;
                DrawButtonPlate(btn, padX, y, listW, btnH, uiScale);
                var label = Assets.Class.makeText(
                    btn.Label.AsHaxeString(),
                    Tools.MultiColor.ColorFromHex("#ffffff"),
                    false,
                    this._menuRoot);
                label.customScale = 0.48 * uiScale;
                label.onResize();
                label.textColor = btn.Enabled ? (btn.Color == 0xFFFFFF ? TextColor : btn.Color) : DisabledColor;
                label.x = padX + 10.0 * uiScale;
                label.y = y + 7.0 * uiScale;

                if (!string.IsNullOrWhiteSpace(btn.Help))
                {
                    var help = Assets.Class.makeText(
                        btn.Help.AsHaxeString(),
                        Tools.MultiColor.ColorFromHex("#9098a8"),
                        false,
                        this._menuRoot);
                    help.customScale = 0.34 * uiScale;
                    help.onResize();
                    help.textColor = HelpColor;
                    help.x = padX + 10.0 * uiScale;
                    help.y = y + btnH - 20.0 * uiScale;
                }

                if (btn.Enabled && btn.OnClick != null)
                {
                    this._menuHitRects.Add((padX, y, listW, btnH, btn.OnClick));
                }

                cursorY += btnH + 6.0 * uiScale;
            }

            // Hide lobby-code overlay while a menu screen is up.
            if (this.lobbyCodeFlow != null)
            {
                try { this.lobbyCodeFlow.set_visible(false); } catch { }
            }

            this._menuVisible = true;
            this._menuRoot.set_visible(true);
        }

        private void DrawSeparator(double y, double padX, double w, double uiScale)
        {
            if (this._menuRoot == null)
                return;
            var g = new Graphics(this._menuRoot);
            int sepColor = SeparatorColor;
            double sepAlpha = 1.0;
            g.beginFill(Ref<int>.From(ref sepColor), Ref<double>.From(ref sepAlpha));
            g.drawRect(padX + 4.0, y, w - 8.0, 1.0);
            g.endFill();
        }

        private void DrawButtonPlate(PendingButton btn, double x, double y, double w, double h, double uiScale)
        {
            if (this._menuRoot == null)
                return;
            var g = new Graphics(this._menuRoot);
            double fullAlpha = 1.0;
            bool accent = btn.Enabled && btn.Color != 0xFFFFFF;

            // outer edge
            int edge = btn.Enabled ? PanelInnerEdge : DisabledPlate;
            g.beginFill(Ref<int>.From(ref edge), Ref<double>.From(ref fullAlpha));
            g.drawRect(x, y, w, h);
            g.endFill();

            // inner panel
            int inner = btn.Enabled ? PanelInner : DisabledPlate;
            g.beginFill(Ref<int>.From(ref inner), Ref<double>.From(ref fullAlpha));
            g.drawRect(x + 2.0, y + 2.0, w - 4.0, h - 4.0);
            g.endFill();

            // top highlight
            int top = accent ? AccentColor : PanelInnerTop;
            g.beginFill(Ref<int>.From(ref top), Ref<double>.From(ref fullAlpha));
            g.drawRect(x + 4.0, y + 3.0, w - 8.0, 2.0);
            g.endFill();

            // accent left notch for primary/action buttons
            if (accent)
            {
                int accentColor = AccentColor;
                g.beginFill(Ref<int>.From(ref accentColor), Ref<double>.From(ref fullAlpha));
                g.drawRect(x, y + 4.0, 3.0, h - 8.0);
                g.endFill();
            }
        }

        // ================================================================ lobby display (existing)

        public void updateConnections()
        {
            RefreshConnections(null);
        }

        public static void NotifyConnectionsChanged()
        {
            TryGetLiveInstance()?.updateConnections();
        }

        private void RefreshConnections(List<string>? names)
        {
            if (this.MainTitleflow == null)
                return;

            var uiScale = UiScale.GetResolutionScale();
            for (int i = 0; i < this.connectionLabels.Count; i++)
            {
                var label = this.connectionLabels[i];
                this.MainTitleflow.removeChild(label);
                label.remove();
            }
            this.connectionLabels.Clear();

            List<string> allname = names ?? _ConnectionUI.GetAllPlayerNames();
            foreach (var name in allname)
            {
                bool isSteamLobbyConnecting = string.Equals(name, _ConnectionUI.SteamLobbyConnectingMarker, StringComparison.Ordinal);
                bool isConnecting =
                    isSteamLobbyConnecting
                    || string.Equals(name, "connecting", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "connecting...", StringComparison.OrdinalIgnoreCase);
                string displayName = isSteamLobbyConnecting
                    ? GetText.Instance.GetString("Connecting to Steam lobby...")
                    : isConnecting
                    ? GetText.Instance.GetString("connecting...")
                    : $"{GetText.Instance.GetString("- ")}{name}";
                var nameColor = Tools.MultiColor.ColorFromHex("#c9c9c9");
                dc.ui.Text player2 = Assets.Class.makeText(
                displayName.AsHaxeString(),
                nameColor,
                false,
                null
            );
                player2.customScale = 0.5 * uiScale;
                player2.onResize();
                player2.textColor = nameColor;
                this.MainTitleflow.addChild(player2);
                this.connectionLabels.Add(player2);
            }

            this.lastConnections.Clear();
            this.lastConnections.AddRange(allname);
            UpdateLobbyIdLabel(forceRefreshText: false);
        }

        private void ClearLobbyCodeUi()
        {
            this.lobbyCodeFlow?.remove();
            this.lobbyCodeFlow = null;

            this.lobbyCodeTitleLabel?.remove();
            this.lobbyCodeTitleLabel = null;
            this.lobbyIdLabel?.remove();
            this.lobbyIdLabel = null;
            this.lastLobbyIdLabelText = string.Empty;
        }

        private void EnsureLobbyCodeFlow(double uiScale)
        {
            if (this.bg == null || base.root == null)
                return;

            if (this.lobbyCodeFlow == null)
            {
                this.lobbyCodeFlow = new Flow(null);
                this.lobbyCodeFlow.isVertical = true;
                this.lobbyCodeFlow.set_horizontalAlign(new FlowAlign.Left());
                this.lobbyCodeFlow.set_verticalAlign(new FlowAlign.Bottom());
                this.lobbyCodeFlow.set_verticalSpacing((int)(2 * uiScale));
                this.lobbyCodeFlow.x += 10;
                this.lobbyCodeFlow.y += 80;
                this.bg.addChild(this.lobbyCodeFlow);
            }

            if (this.lobbyCodeTitleLabel == null)
            {
                var titleColor = Tools.MultiColor.ColorFromHex("#9ea8b3");
                this.lobbyCodeTitleLabel = Assets.Class.makeText(
                    GetText.Instance.GetString("Lobby code").AsHaxeString(),
                    titleColor,
                    false,
                    null);
                this.lobbyCodeFlow.addChild(this.lobbyCodeTitleLabel);
                this.lobbyCodeTitleLabel.textColor = titleColor;
            }

            if (this.lobbyIdLabel == null)
            {
                var idColor = Tools.MultiColor.ColorFromHex("#7fd4ff");
                this.lobbyIdLabel = Assets.Class.makeText(
                    string.Empty.AsHaxeString(),
                    idColor,
                    true,
                    null);
                this.lobbyCodeFlow.addChild(this.lobbyIdLabel);
                this.lobbyIdLabel.textColor = idColor;
            }

            var lobbyCodeScale = 0.55 * uiScale;
            this.lobbyCodeTitleLabel.customScale = lobbyCodeScale;
            this.lobbyCodeTitleLabel.onResize();
            this.lobbyCodeTitleLabel.textColor = Tools.MultiColor.ColorFromHex("#9ea8b3");
            this.lobbyIdLabel.customScale = lobbyCodeScale;
            this.lobbyIdLabel.onResize();
            this.lobbyIdLabel.textColor = Tools.MultiColor.ColorFromHex("#7fd4ff");
        }

        private void UpdateLobbyIdLabel(bool forceRefreshText)
        {
            if (this.bg == null)
                return;

            var lobbyCode = GameMenu.GetSteamLobbyCodeForUi();
            if (string.IsNullOrWhiteSpace(lobbyCode))
            {
                if (this.lobbyCodeFlow != null)
                    this.lobbyCodeFlow.set_visible(false);
                this.lastLobbyIdLabelText = string.Empty;
                return;
            }

            var uiScale = UiScale.GetResolutionScale();
            var labelText = lobbyCode.Trim().ToLowerInvariant();
            EnsureLobbyCodeFlow(uiScale);
            if (this.lobbyCodeFlow == null || this.lobbyIdLabel == null || this.lobbyCodeTitleLabel == null)
                return;

            if (forceRefreshText || !string.Equals(this.lastLobbyIdLabelText, labelText, StringComparison.Ordinal))
            {
                this.lobbyIdLabel.set_text(labelText.AsHaxeString());
                this.lastLobbyIdLabelText = labelText;
            }

            var leftPadding = 10.0 * uiScale;
            var bottomPadding = 8.0 * uiScale;
            this.lobbyCodeFlow.reflow();
            var flowHeight = this.lobbyCodeFlow.get_innerHeight();
            this.lobbyCodeFlow.x = this.bg.x + leftPadding;
            this.lobbyCodeFlow.y = this.bg.y + this.bg.hei - flowHeight - bottomPadding;
            this.lobbyCodeFlow.set_visible(this._mode == UiMode.Lobby);
        }

        private bool NeedsConnectionsRefresh(List<string> names)
        {
            if (names.Count != this.lastConnections.Count)
                return true;

            for (int i = 0; i < names.Count; i++)
            {
                if (!string.Equals(names[i], this.lastConnections[i], StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        // ================================================================ legacy visual extras (kept for compat)

        private List<double> sprx = new List<double> { 0.4, -1.0, -0.2, -0.6 };
        private List<string> animlist = new List<string>
        {
           "idle", "idle","idle","idle"
        };
        private List<string> sprmodu = new List<string>
        {
            "Tick4","PrisonerGold","KingWhite","PrisonerDefault"
        };

        private void loadspr(double x, string sprmuld, int count)
        {
            this.spritesflow = new Flow(null);
            this.spritesflow.set_verticalAlign(new FlowAlign.Top());
            this.spritesflow.set_horizontalAlign(new FlowAlign.Middle());
            this.spritesflow.isVertical = false;

            dc.String idle = "idle".AsHaxeString();
            string skinanim = animlist[count];
            SpriteLib g = Assets.Class.getHeroLib(Cdb.Class.getSkinInfo(sprmuld.AsHaxeString()));
            this.spriteui = new HSprite(g, skinanim.AsHaxeString(), Ref<int>.Null, null);

            SpritePivot pivot = this.spriteui.pivot;
            pivot.centerFactorX = x;
            pivot.centerFactorY = 0.5;
            pivot.usingFactor = true;
            pivot.isUndefined = false;

            initColorMap(sprmuld);

            AnimManager animManager = this.spriteui.get_anim().play(skinanim.AsHaxeString(), null, null).loop(null);
            animManager.genSpeed = 0.4;

            this.spriteui.set_visible(true);
            this.spritesflow.addChild(this.spriteui);
            this.bg?.addChild(this.spritesflow);
            this.sprites.Add(this.spriteui);
        }

        private string GetRandomAnimation(List<string> values)
        {
            Random fallbackRandom = new Random();
            int fallbackIndex = fallbackRandom.Next(values.Count);
            return values[fallbackIndex];
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

        public void initColorMap(string colorMap)
        {
            dc.shader.ColorMap shader = (dc.shader.ColorMap)this.spriteui!.getShader(dc.shader.ColorMap.Class);
            if (shader != null)
            {
                this.spriteui.removeShader(shader);
            }

            dc.h3d.mat.Texture texture = Res.Class.load("atlas/beheaded_aladdin_s.png".AsHaxeString()).toTexture();
            dc.h3d.mat.Filter filter = new dc.h3d.mat.Filter.Nearest();
            filter = texture.set_filter(filter);

            virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_ skinInfo = Cdb.Class.getSkinInfo(colorMap.AsHaxeString());
            dc.h3d.mat.Texture heroColorMap = Assets.Class.getHeroColorMap(skinInfo);
            dc.shader.ColorMap colorMapp = (ColorMap)this.spriteui.addShader(new dc.shader.ColorMap(heroColorMap));

            DirLighted s2 = new DirLighted();
            s2 = (DirLighted)this.spriteui.addShader(s2);

            dc.h3d.mat.Texture normalMapFromGroup = this.spriteui.lib.getNormalMapFromSprite(this.spriteui);
            dc.shader.NormalMap normal = new dc.shader.NormalMap(normalMapFromGroup);
            this.spriteui.addShader(normal);
        }

        // ================================================================ lifecycle

        private void clean()
        {
            ClearLobbyCodeUi();
            this._menuRoot?.remove();
            this._menuRoot = null;
            this.bg?.remove();
            this.rootFlow?.remove();
            this.inter?.remove();
            this.sprites.Clear();
        }

        public override void onResize()
        {
            base.onResize();
            if (this.rootFlow == null || base.root == null)
                return;

            var win = dc.hxd.Window.Class.getInstance();
            double screenWidth = win.get_width();
            double screenHeight = win.get_height();
            var uiScale = UiScale.GetResolutionScale();

            this.rootFlow.set_minWidth((int)(screenWidth * 0.90));
            this.rootFlow.set_minHeight((int)(screenHeight * 0.82));
            this.rootFlow.reflow();

            double flowW = this.rootFlow.get_innerWidth();
            double flowH = this.rootFlow.get_innerHeight();

            ClearLobbyCodeUi();
            this.bg?.remove();
            this.bg = UIBox.Class.drawBoxValidation(
                (int)flowW,
                (int)flowH,
                Ref<int>.Null,
                Ref<int>.Null,
                null,
                false
            );
            this.root.addChild(this.bg);

            this.bg.set_visible(true);
            this.bg.wid = (int)255;
            this.bg.hei = (int)flowH;

            // Left-center placement: anchored to the left edge with a small margin, vertically centered.
            double posX = base.get_pixelScale.Invoke() * 22.0 * uiScale;
            double posY = (screenHeight - flowH) / 2.0;
            this.rootFlow.x = posX;
            this.rootFlow.y = posY;

            this.bg.x = posX;
            this.bg.y = posY;

            this.inter?.remove();
            this.inter = new dc.h2d.Interactive(this.bg.wid, this.bg.hei, this.bg, null);
            this.inter.onClick = new HlAction<Event>(this.OnClick);
            BGtext();
            if (this._mode == UiMode.Menu && this._menuVisible)
                RebuildMenuScreen();
            UpdateLobbyIdLabel(forceRefreshText: true);
        }

        private void BGtext()
        {
            this.MainTitleflow = new Flow(null);
            this.MainTitleflow.isVertical = true;
            var uiScale = UiScale.GetResolutionScale();

            FlowAlign flowAlign = this.MainTitleflow.set_horizontalAlign(new FlowAlign.Middle());
            flowAlign = this.MainTitleflow.set_verticalAlign(new FlowAlign.Top());

            double bgWidth = this.bg!.wid;
            double bgHeight = this.bg.hei;
            this.MainTitleflow.set_minWidth((int)bgWidth);
            this.MainTitleflow.set_minHeight((int)bgHeight);

            this.bg!.addChild(this.MainTitleflow);
            dc.ui.Text title = Assets.Class.makeText(
                GetText.Instance.GetString("Lobby menu").AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#f7fc65"),
                true,
                null
            );

            title.scaleX = 0.6 * uiScale;
            title.scaleY = 0.6 * uiScale;

            this.MainTitleflow.addChild(title);

            Flow titleWrapper = new Flow(null);
            titleWrapper.isVertical = false;
            titleWrapper.set_horizontalAlign(new FlowAlign.Middle());

            titleWrapper.addChild(title);
            this.MainTitleflow.addChild(titleWrapper);

            dc.ui.Text subtitle = Assets.Class.makeText(
                GetText.Instance.GetString("Players' list").AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#919191"),
                false,
                null
            );
            subtitle.scaleX = 0.5 * uiScale;
            subtitle.scaleY = 0.5 * uiScale;

            Flow subtitleWrapper = new Flow(null);
            subtitleWrapper.isVertical = false;
            subtitleWrapper.set_horizontalAlign(new FlowAlign.Middle());

            subtitleWrapper.addChild(subtitle);
            this.MainTitleflow.addChild(subtitleWrapper);

            this.playersListWrapper = new Flow(null);
            this.playersListWrapper.isVertical = true;
            this.playersListWrapper.set_horizontalAlign(new FlowAlign.Middle());
            this.playersListWrapper.set_verticalSpacing((int)(4 * uiScale));

            this.MainTitleflow.addChild(this.playersListWrapper);
            updateConnections();
            this.MainTitleflow.reflow();
        }

        public override void update()
        {
            base.update();

            if (this._mode != UiMode.Menu || this._keepLobbyVisible)
            {
                var names = _ConnectionUI.GetAllPlayerNames();
                if (NeedsConnectionsRefresh(names))
                    RefreshConnections(names);
                else
                    UpdateLobbyIdLabel(forceRefreshText: false);
            }
            else if (this._menuVisible)
            {
                UpdateLobbyIdLabel(forceRefreshText: false);
            }
        }

        private void OnClick(Event e)
        {
            if (this.bg == null)
                return;

            // Menu buttons (hit rects are relative to bg).
            if (this._menuVisible && this._menuRoot != null && this._menuRoot.visible)
            {
                var x = e.relX;
                var y = e.relY;
                for (int i = 0; i < this._menuHitRects.Count; i++)
                {
                    var r = this._menuHitRects[i];
                    if (x >= r.X && x <= r.X + r.W && y >= r.Y && y <= r.Y + r.H)
                    {
                        try { r.Cb(); }
                        catch (Exception ex) { Log.Debug("[ConnectionUI] Button callback failed: {Message}", ex.Message); }
                        return;
                    }
                }
            }

            // Lobby code copy.
            if (this.lobbyCodeFlow == null || !this.lobbyCodeFlow.visible)
                return;

            var relX = e.relX;
            var relY = e.relY;
            var width = this.lobbyCodeFlow.get_innerWidth();
            var height = this.lobbyCodeFlow.get_innerHeight();
            var minX = this.lobbyCodeFlow.x - this.bg.x;
            var minY = this.lobbyCodeFlow.y - this.bg.y;
            var maxX = minX + width;
            var maxY = minY + height;

            if (relX < minX || relX > maxX || relY < minY || relY > maxY)
                return;

            if (GameMenu.TryCopySteamLobbyCodeFromUi())
                MultiplayerUI.PushSystemMessage("Lobby id copied to clipboard");
        }

        public static void Initialize(ModEntry entry)
        {
            entry.Logger.Information("\x1b[32m[[ModEntry.ConnectionUI] Initializing ConnectionUI...]\x1b[0m ");
        }

        /// <summary>
        /// Ensures ConnectionUI exists on the given TitleScreen. Called from mainMenu hook.
        /// </summary>
        public static void EnsureCreated(TitleScreen screen)
        {
            var live = TryGetLiveInstance();
            if (live != null && ReferenceEquals(live.parent, screen))
                return;

            Instance = null;
            var connectionUI = new ConnectionUI(screen);
            screen.addChild(connectionUI);
            try
            {
                connectionUI.root?.set_visible(false);
            }
            catch
            {
            }
        }
    }
}
