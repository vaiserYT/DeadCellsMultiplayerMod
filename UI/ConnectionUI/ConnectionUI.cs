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
    public partial class ConnectionUI :
    Process,
    IEventReceiver
    {
        // ---------------------------------------------------------------- palette
        private static readonly int PanelInner = 0x14161F;
        private static readonly int PanelInnerEdge = 0x2A3A5E;
        private static readonly int PanelInnerTop = 0x3A4A6E;
        private static readonly int AccentColor = 0x59D5FF;
        private static readonly int TextColor = 0xC9C9C9;
        private static readonly int HelpColor = 0x9098A8;
        private static readonly int DisabledColor = 0x6A6A6A;
        private static readonly int DisabledPlate = 0x232327;

        private enum UiMode
        {
            Lobby,
            Menu
        }

        // Dead Cells renders menu text smaller in windowed mode (the game's own pixelScale drops
        // below 1.0). GhostHero compensates nicknames with a ~1.6x boost in windowed mode; we do
        // the same so button labels stay as readable in a window as in fullscreen.
        private const double WindowedTextBoost = 1.6;
        private const int WindowedDisplayMode = 0;
        private const int FullscreenDisplayMode = 1;
        private const int BorderlessDisplayMode = 2;
        private static int _cachedDisplayMode = int.MinValue;
        private static int _cachedFullScreenMode = int.MinValue;
        private static double _cachedTextBoost = 1.0;

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
        // Nav screens (Host/Join/Back): custom panel — UIBox.drawBoxValidation scales children and
        // caused overlapping / clipped buttons when we tried to resize it.
        private dc.h2d.Object? _navRoot;
        private readonly List<(double X, double Y, double W, double H, Action Cb)> _menuHitRects = new();
        private bool _menuVisible;
        private int _layoutW = 255;
        private int _layoutH = 720;
        private Graphics? _hoverBorder;
        private static readonly int HoverBorderColor = 0xFFFFFF;

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

        /// <summary>
        /// Windowed mode makes the game shrink all UI (including baked text) more than fullscreen;
        /// returns a multiplier that keeps menu text legible in a window. Mirrors the proven
        /// GhostHero nickname-scaling logic (same display-mode detection, ~1.6x in windowed).
        /// </summary>
        private static double GetWindowedTextBoost()
        {
            try
            {
                var win = dc.hxd.Window.Class.getInstance();
                if (win == null)
                    return _cachedTextBoost;

                var displayMode = int.MinValue;
                var sdlWin = win.window;
                if (sdlWin != null)
                    displayMode = sdlWin.displayMode;

                var mode = win.fullScreenMode;
                if (_cachedDisplayMode == displayMode && _cachedFullScreenMode == mode)
                    return _cachedTextBoost;

                _cachedDisplayMode = displayMode;
                _cachedFullScreenMode = mode;

                if (displayMode == FullscreenDisplayMode || displayMode == BorderlessDisplayMode)
                    _cachedTextBoost = 1.0;
                else if (displayMode == WindowedDisplayMode)
                    _cachedTextBoost = WindowedTextBoost;
                else if (mode == FullscreenDisplayMode || mode == BorderlessDisplayMode)
                    _cachedTextBoost = 1.0;
                else
                    _cachedTextBoost = WindowedTextBoost;

                return _cachedTextBoost;
            }
            catch
            {
                return _cachedTextBoost;
            }
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
            // Rebuild panel at the correct width (lobby column vs wider nav), then draw buttons.
            instance._menuVisible = true;
            instance.onResize();
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
            instance.onResize();
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
            var uiScale = UiScale.GetResolutionScale();
            // Text must never shrink below its fullscreen size: windowed mode lowers uiScale below
            // 1.0, which previously made button labels tiny and blurry while the panel stayed large.
            // Windowed mode also shrinks the whole game UI on top of that, so boost text further
            // there (same ~1.6x that GhostHero applies to nicknames).
            var textUi = System.Math.Max(uiScale, 1.0) * GetWindowedTextBoost();
            double bgWidth = this._layoutW;
            double bgHeight = this._layoutH;
            bool navLayout = !this._keepLobbyVisible;

            // Rebuild the absolute-positioned menu list container.
            var host = navLayout ? this._navRoot : (dc.h2d.Object?)this.bg;
            if (host == null)
                return;
            if (!navLayout && this.MainTitleflow == null)
                return;
            this._menuRoot?.remove();
            this._menuRoot = new dc.h2d.Object(null);
            host.addChild(this._menuRoot);
            this._menuHitRects.Clear();

            double padX = 16.0 * uiScale;
            double listW = bgWidth - padX * 2.0;
            double colGap = (navLayout ? 14.0 : 12.0) * uiScale;
            double rowGap = (navLayout ? 14.0 : 12.0) * uiScale;
            double navText = navLayout ? textUi * 1.35 : textUi;
            double cursorY;

            if (this._keepLobbyVisible)
            {
                // Lobby screens: keep the vanilla "Lobby menu" title + player list flow on top,
                // and render the action buttons underneath it.
                if (this.MainTitleflow == null)
                    return;
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
                // Navigation screens: collapse lobby chrome so it cannot push/clip the button cluster.
                if (this.MainTitleflow != null)
                {
                    this.MainTitleflow.set_minWidth(0);
                    this.MainTitleflow.set_minHeight(0);
                    this.MainTitleflow.set_visible(false);
                }
                if (this.playersListWrapper != null)
                {
                    try { this.playersListWrapper.visible = false; } catch { }
                }
                cursorY = 0.0;
            }

            var actions = new List<PendingButton>();
            var backs = new List<PendingButton>();
            if (navLayout)
            {
                for (int i = 0; i < PendingButtons.Count; i++)
                {
                    var btn = PendingButtons[i];
                    if (IsBackButton(btn))
                        backs.Add(btn);
                    else
                        actions.Add(btn);
                }
            }

            // Nav: button cluster at top-center of the LARGE background panel (not y-centered).
            double clusterW = listW;
            double clusterX = padX;
            if (navLayout)
            {
                clusterW = System.Math.Min(listW, NavButtonClusterWidth * uiScale);
                clusterX = (bgWidth - clusterW) * 0.5;
                cursorY = 28.0 * uiScale;
            }

            foreach (var info in PendingInfos)
            {
                var line = Assets.Class.makeText(
                    info.Text.AsHaxeString(),
                    Tools.MultiColor.ColorFromHex("#e0e0e0"),
                    false,
                    this._menuRoot);
                double infoScale = 0.42 * navText;
                line.customScale = infoScale;
                line.onResize();
                line.textColor = info.Color;
                if (navLayout)
                    CenterMenuText(line, info.Text, clusterX, clusterW, infoScale);
                else
                    line.x = padX;
                line.y = cursorY;
                cursorY += 24.0 * uiScale;
            }

            if (PendingInfos.Count > 0)
                cursorY += 8.0 * uiScale;

            if (navLayout)
            {
                // Row 1: Host | Join (with tips). Row 2: Back. Top-center of big panel.
                for (int i = 0; i < actions.Count; i += 2)
                {
                    bool pair = i + 1 < actions.Count;
                    if (pair)
                    {
                        double btnW = (clusterW - colGap) * 0.5;
                        double btnH = System.Math.Max(
                            GetButtonHeight(actions[i], showHelp: true, uiScale, nav: true),
                            GetButtonHeight(actions[i + 1], showHelp: true, uiScale, nav: true));
                        PlaceMenuButton(actions[i], clusterX, cursorY, btnW, btnH, navText, uiScale, showHelp: true, centerText: true, nav: true);
                        PlaceMenuButton(actions[i + 1], clusterX + btnW + colGap, cursorY, btnW, btnH, navText, uiScale, showHelp: true, centerText: true, nav: true);
                        cursorY += btnH + rowGap;
                    }
                    else
                    {
                        double btnH = GetButtonHeight(actions[i], showHelp: true, uiScale, nav: true);
                        PlaceMenuButton(actions[i], clusterX, cursorY, clusterW, btnH, navText, uiScale, showHelp: true, centerText: true, nav: true);
                        cursorY += btnH + rowGap;
                    }
                }

                for (int i = 0; i < backs.Count; i++)
                {
                    double btnH = GetButtonHeight(backs[i], showHelp: true, uiScale, nav: true);
                    PlaceMenuButton(backs[i], clusterX, cursorY, clusterW, btnH, navText, uiScale, showHelp: true, centerText: true, nav: true);
                    cursorY += btnH + rowGap;
                }
            }
            else
            {
                for (int i = 0; i < PendingButtons.Count; i++)
                {
                    var btn = PendingButtons[i];
                    double btnH = GetButtonHeight(btn, showHelp: true, uiScale, nav: false);
                    PlaceMenuButton(btn, padX, cursorY, listW, btnH, textUi, uiScale, showHelp: true, centerText: false, nav: false);
                    cursorY += btnH + 6.0 * uiScale;
                }
            }

            // Hide lobby-code overlay while a menu screen is up.
            if (this.lobbyCodeFlow != null)
            {
                try { this.lobbyCodeFlow.set_visible(false); } catch { }
            }

            this._menuVisible = true;
            this._menuRoot.set_visible(true);
        }

        private static bool IsBackButton(PendingButton btn)
        {
            var label = btn.Label ?? string.Empty;
            if (label.IndexOf("back", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            try
            {
                var localized = GetText.Instance.GetString("Back");
                if (!string.IsNullOrEmpty(localized)
                    && string.Equals(label, localized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
            }
            return false;
        }

        private static double GetButtonHeight(PendingButton btn, bool showHelp, double uiScale, bool nav)
        {
            if (showHelp && !string.IsNullOrWhiteSpace(btn.Help))
                return (nav ? 88.0 : 50.0) * uiScale;
            return (nav ? 58.0 : 34.0) * uiScale;
        }

        private void PlaceMenuButton(
            PendingButton btn,
            double x,
            double y,
            double w,
            double h,
            double textUi,
            double uiScale,
            bool showHelp,
            bool centerText,
            bool nav)
        {
            DrawButtonPlate(btn, x, y, w, h, uiScale);

            double labelScale = (nav ? 0.62 : 0.48) * textUi;
            var label = Assets.Class.makeText(
                btn.Label.AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#ffffff"),
                false,
                this._menuRoot);
            label.customScale = labelScale;
            label.onResize();
            label.textColor = btn.Enabled ? (btn.Color == 0xFFFFFF ? TextColor : btn.Color) : DisabledColor;

            bool hasHelp = showHelp && !string.IsNullOrWhiteSpace(btn.Help);
            if (centerText)
            {
                CenterMenuText(label, btn.Label, x, w, labelScale);
                label.y = hasHelp ? y + (nav ? 14.0 : 8.0) * uiScale : y + (h - 16.0 * uiScale) * 0.5;
            }
            else
            {
                label.x = x + 10.0 * uiScale;
                label.y = y + 7.0 * uiScale;
            }

            if (hasHelp)
            {
                double helpScale = (nav ? 0.42 : 0.34) * textUi;
                var help = Assets.Class.makeText(
                    btn.Help.AsHaxeString(),
                    Tools.MultiColor.ColorFromHex("#9098a8"),
                    false,
                    this._menuRoot);
                help.customScale = helpScale;
                help.onResize();
                help.textColor = HelpColor;
                if (centerText)
                {
                    CenterMenuText(help, btn.Help, x, w, helpScale);
                    help.y = y + h - (nav ? 28.0 : 20.0) * uiScale;
                }
                else
                {
                    help.x = x + 10.0 * uiScale;
                    help.y = y + h - 20.0 * uiScale;
                }
            }

            if (btn.Enabled && btn.OnClick != null)
            {
                // Per-button Interactive: click + white hover border (onOver / onOut).
                var hit = new dc.h2d.Interactive(w, h, this._menuRoot, null);
                hit.x = x;
                hit.y = y;
                var cb = btn.OnClick;
                double hx = x, hy = y, hw = w, hh = h;
                hit.onOver = new HlAction<Event>(_ => SetHoverBorder(hx, hy, hw, hh));
                hit.onOut = new HlAction<Event>(_ => ClearHoverBorder());
                hit.onClick = new HlAction<Event>(_ =>
                {
                    ClearHoverBorder();
                    try { cb(); }
                    catch (Exception ex) { Log.Debug("[ConnectionUI] Button callback failed: {Message}", ex.Message); }
                });
                // Keep rects as fallback for lobby panel Interactive hit-testing.
                this._menuHitRects.Add((x, y, w, h, cb));
            }
        }

        private void SetHoverBorder(double x, double y, double w, double h)
        {
            ClearHoverBorder();
            if (this._menuRoot == null)
                return;
            var g = new Graphics(this._menuRoot);
            this._hoverBorder = g;
            int color = HoverBorderColor;
            double alpha = 1.0;
            const double t = 2.0;
            g.beginFill(Ref<int>.From(ref color), Ref<double>.From(ref alpha));
            g.drawRect(x, y, w, t);
            g.drawRect(x, y + h - t, w, t);
            g.drawRect(x, y, t, h);
            g.drawRect(x + w - t, y, t, h);
            g.endFill();
        }

        private void ClearHoverBorder()
        {
            try { this._hoverBorder?.remove(); } catch { }
            this._hoverBorder = null;
        }

        /// <summary>Hides ConnectionUI chrome and returns to the title main menu.</summary>
        public static void ReturnToMainMenu(TitleScreen screen)
        {
            try
            {
                var instance = TryGetLiveInstance();
                instance?.DismissMenuUi();
                set_visible = false;
                screen.ShouldAutoHideConnectionUI(false);
                screen.mainMenu();
            }
            catch (Exception ex)
            {
                Log.Debug("[ConnectionUI] ReturnToMainMenu failed: {Message}", ex.Message);
                try { set_visible = false; } catch { }
                try { screen.mainMenu(); } catch { }
            }
        }

        private void DismissMenuUi()
        {
            this._menuVisible = false;
            ClearHoverBorder();
            CloseTextPrompt(apply: false);
            try { this._menuRoot?.remove(); } catch { }
            this._menuRoot = null;
            try { this._navRoot?.remove(); } catch { }
            this._navRoot = null;
            this._menuHitRects.Clear();
        }

        private static void CenterMenuText(dc.ui.Text text, string label, double regionX, double regionW, double customScale)
        {
            double textWidth;
            try
            {
                textWidth = text.textWidth;
                if (text.scaleX > 0.01)
                    textWidth *= text.scaleX;
                else if (customScale > 0.01)
                    textWidth *= customScale;
            }
            catch
            {
                textWidth = label.Length * 7.0 * System.Math.Max(customScale, 0.4);
            }

            if (textWidth <= 0)
                textWidth = label.Length * 7.0 * System.Math.Max(customScale, 0.4);

            text.x = System.Math.Max(regionX + 4.0, regionX + (regionW - textWidth) * 0.5);
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
            var textBoost = GetWindowedTextBoost();
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
                player2.customScale = 0.5 * uiScale * textBoost;
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
            ClearHoverBorder();
            CloseTextPrompt(apply: false);
            this._menuRoot?.remove();
            this._menuRoot = null;
            this._navRoot?.remove();
            this._navRoot = null;
            this.bg?.remove();
            this.rootFlow?.remove();
            this.inter?.remove();
            this.sprites.Clear();
        }

        // Lobby player-list column (original UIBox path).
        private const int MenuPanelWidth = 255;
        // Big background panel for Host/Join — nearly full width; buttons top-centered inside.
        private const double NavPanelWidthFraction = 0.92;
        private const int NavPanelMinWidth = 900;
        private const int NavPanelMaxWidth = 2400;
        private const double NavButtonClusterWidth = 780.0;
        private const double ReferencePanelHeight = 720.0;

        public override void onResize()
        {
            base.onResize();
            if (this.rootFlow == null || base.root == null)
                return;

            var win = dc.hxd.Window.Class.getInstance();
            double screenWidth = win.get_width();
            double screenHeight = win.get_height();
            var uiScale = UiScale.GetResolutionScale();
            bool navMenu = this._mode == UiMode.Menu && !this._keepLobbyVisible;

            ClearLobbyCodeUi();
            this.inter?.remove();
            this.inter = null;

            if (navMenu)
            {
                BuildNavPanel(screenWidth, screenHeight, uiScale);
            }
            else
            {
                BuildLobbyPanel(screenWidth, screenHeight, uiScale);
            }

            if (this._mode == UiMode.Menu && this._menuVisible)
                RebuildMenuScreen();
            UpdateLobbyIdLabel(forceRefreshText: true);
        }

        /// <summary>
        /// Host/Join/Back: plain Object + Graphics panel centered on screen.
        /// Avoids UIBox.drawBoxValidation, which scales/clips children and produced the overlap mess.
        /// </summary>
        private void BuildNavPanel(double screenWidth, double screenHeight, double uiScale)
        {
            // Hide lobby chrome.
            if (this.bg != null)
            {
                try { this.bg.set_visible(false); } catch { }
            }
            if (this.rootFlow != null)
            {
                try { this.rootFlow.visible = false; } catch { }
            }

            this._navRoot?.remove();
            this._navRoot = new dc.h2d.Object(null);
            this.root.addChild(this._navRoot);

            // Large background frame (like the old hub), NOT sized to the buttons.
            int panelW = (int)(screenWidth * NavPanelWidthFraction);
            if (panelW < NavPanelMinWidth)
                panelW = NavPanelMinWidth;
            if (panelW > NavPanelMaxWidth)
                panelW = NavPanelMaxWidth;
            if (screenWidth > 0 && panelW > screenWidth - 40)
                panelW = (int)System.Math.Max(320, screenWidth - 40);

            double panelH = ReferencePanelHeight * uiScale;
            if (screenHeight > 0)
                panelH = System.Math.Min(panelH, screenHeight * 0.88);

            this._layoutW = panelW;
            this._layoutH = (int)panelH;

            // Soft panel fill + thin accent edge (no gray button-frame rings on the window).
            var g = new Graphics(this._navRoot);
            int fill = PanelInner;
            double fillAlpha = 0.94;
            g.beginFill(Ref<int>.From(ref fill), Ref<double>.From(ref fillAlpha));
            g.drawRect(0, 0, panelW, panelH);
            g.endFill();
            int edge = AccentColor;
            double edgeAlpha = 0.55;
            g.beginFill(Ref<int>.From(ref edge), Ref<double>.From(ref edgeAlpha));
            g.drawRect(0, 0, panelW, 2);
            g.drawRect(0, panelH - 2, panelW, 2);
            g.drawRect(0, 0, 2, panelH);
            g.drawRect(panelW - 2, 0, 2, panelH);
            g.endFill();

            this._navRoot.x = (screenWidth - panelW) * 0.5;
            this._navRoot.y = (screenHeight - panelH) * 0.5;
            this._navRoot.set_visible(true);

            this.inter = new dc.h2d.Interactive(panelW, (int)panelH, this._navRoot, null);
            this.inter.onClick = new HlAction<Event>(this.OnClick);
            
        }

        /// <summary>Lobby / player list: original left UIBox column.</summary>
        private void BuildLobbyPanel(double screenWidth, double screenHeight, double uiScale)
        {
            this._navRoot?.remove();
            this._navRoot = null;

            if (this.rootFlow != null)
            {
                try { this.rootFlow.visible = true; } catch { }
            }

            this.rootFlow!.set_minWidth((int)(screenWidth * 0.90));
            double panelH = ReferencePanelHeight * uiScale;
            if (screenHeight > 0)
                panelH = System.Math.Min(panelH, screenHeight * 0.92);
            this.rootFlow.set_minHeight((int)panelH);
            this.rootFlow.reflow();

            double flowW = this.rootFlow.get_innerWidth();
            this._layoutW = MenuPanelWidth;
            this._layoutH = (int)panelH;

            this.bg?.remove();
            this.bg = UIBox.Class.drawBoxValidation(
                (int)flowW,
                (int)panelH,
                Ref<int>.Null,
                Ref<int>.Null,
                null,
                false
            );
            this.root.addChild(this.bg);
            this.bg.set_visible(true);
            this.bg.wid = MenuPanelWidth;
            this.bg.hei = (int)panelH;

            double posX = base.get_pixelScale.Invoke() * 22.0 * uiScale;
            double posY = (screenHeight - panelH) / 2.0;
            this.rootFlow.x = posX;
            this.rootFlow.y = posY;
            this.bg.x = posX;
            this.bg.y = posY;

            this.inter = new dc.h2d.Interactive(this.bg.wid, this.bg.hei, this.bg, null);
            this.inter.onClick = new HlAction<Event>(this.OnClick);
            BGtext();
        }

        private void BGtext()
        {
            this.MainTitleflow = new Flow(null);
            this.MainTitleflow.isVertical = true;
            var uiScale = UiScale.GetResolutionScale();

            FlowAlign flowAlign = this.MainTitleflow.set_horizontalAlign(new FlowAlign.Middle());
            flowAlign = this.MainTitleflow.set_verticalAlign(new FlowAlign.Top());

            // Use layout size, not UIBox.wid/hei (those can disagree with the drawn frame).
            double bgWidth = this._layoutW;
            double bgHeight = this._layoutH;
            bool navMenu = this._mode == UiMode.Menu && !this._keepLobbyVisible;
            this.MainTitleflow.set_minWidth(navMenu ? 0 : (int)bgWidth);
            this.MainTitleflow.set_minHeight(navMenu ? 0 : (int)bgHeight);

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
            TickTextPrompt();

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
            // Menu buttons (hit rects are relative to the Interactive's parent: _navRoot or bg).
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
            if (this.bg == null || this.lobbyCodeFlow == null || !this.lobbyCodeFlow.visible)
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
