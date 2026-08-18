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
    /// Visual hub for the multiplayer menu. LobbySession keeps all networking/state logic; this
    /// Process renders the pretty button screens (host/join LAN &amp; Steam, lobby status, errors).
    /// LobbySession feeds it through <see cref="BeginMenu"/>/<see cref="AddPendingButton"/>/
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
        // Disabled stays in the navy/blue family — no neutral gray that fights the palette.
        private static readonly int DisabledColor = 0x5A657C;
        private static readonly int DisabledHelpColor = 0x4A5568;
        private static readonly int DisabledPlateEdge = 0x1C2638;
        private static readonly int DisabledPlateFace = 0x12161F;
        private static readonly int DisabledPlateTop = 0x243044;
        private static readonly int FieldFace = 0x0C121D;
        private static readonly int FieldBorder = 0x46546F;
        private static readonly int ContentCardFill = 0x10131C;
        private static readonly int ContentCardEdge = 0x2E3F66;
        private const double ButtonCornerRadius = 10.0;
        private const double FieldCornerRadius = 8.0;
        private const double CardCornerRadius = 16.0;

        internal enum UiMode
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

        internal sealed class PendingButton
        {
            public string Label = string.Empty;
            public string Help = string.Empty;
            public bool Enabled = true;
            public int Color = 0xFFFFFF;
            /// <summary>Rendered as an editable input field (bordered box), not a button plate.</summary>
            public bool FieldStyle;
            public Action? OnClick;
        }

        internal sealed class PendingInfo
        {
            public string Text = string.Empty;
            public int Color = 0xFFFFFF;
        }

        // ---------------------------------------------------------------- pending menu (fed by LobbySession)
        private static readonly List<PendingButton> PendingButtons = new();
        private static readonly List<PendingInfo> PendingInfos = new();

        // ---------------------------------------------------------------- instance state
        private Flow? rootFlow;
        private dc.h2d.Interactive? inter;
        private Flow? spritesflow;
        private readonly List<HSprite> sprites = new();
        private readonly List<dc.ui.Text> connectionLabels = new();
        private readonly List<string> lastConnections = new();
        private string lastLobbySlotsSignature = string.Empty;
        private Flow? lobbyCodeFlow;
        private dc.ui.Text? lobbyCodeTitleLabel;
        private dc.ui.Text? lobbyIdLabel;
        private string lastLobbyIdLabelText = string.Empty;
        /// <summary>Top-right styled lobby players card.</summary>
        private dc.h2d.Object? _lobbyPanelRoot;
        private double _lobbyPanelHeight;
        private UiMode _mode = UiMode.Lobby;
        private bool _keepLobbyVisible;
        /// <summary>Only the first Host/Join hub uses the wide centered panel + 2-column row.</summary>
        private bool _hubLayout;

        // menu list rendering (absolute layout inside the styled panel)
        private dc.h2d.Object? _menuRoot;
        /// <summary>Plates / content card — below hover and labels.</summary>
        private dc.h2d.Object? _menuChromeRoot;
        /// <summary>Hover rings — above plates, below labels so text never gets covered.</summary>
        private dc.h2d.Object? _menuHoverRoot;
        /// <summary>Button / field labels.</summary>
        private dc.h2d.Object? _menuLabelRoot;
        /// <summary>Hit targets on top.</summary>
        private dc.h2d.Object? _menuHitRoot;
        // Custom styled panel root (replaces UIBox for hub + other menus).
        private dc.h2d.Object? _panelRoot;
        private readonly List<(double X, double Y, double W, double H, Action Cb)> _menuHitRects = new();
        private bool _menuVisible;
        private int _layoutW = 255;
        private int _layoutH = 720;
        private double _lastLayoutUiScale = double.NaN;
        private double _lastLayoutTextBoost = double.NaN;
        private int _layoutMetricProbeCountdown;
        private const int LayoutMetricProbeIntervalFrames = 10;
        private int _lobbyCodeProbeCountdown;
        private Graphics? _hoverBorder;
        private static readonly int HoverBorderColor = 0x59D5FF;
        /// <summary>Same callback as the screen's Back/Disconnect button; fired on Escape.</summary>
        private Action? _menuEscapeAction;

        private static ConnectionUI? Instance;
        private HSprite? spriteui;
        private readonly LifecycleTracker _lifecycle = new("ConnectionUI");
        private readonly long _lifecycleGeneration;

        public ConnectionUI(Process parent) : base(parent)
        {
            _lifecycleGeneration = _lifecycle.Start();
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
                if (!instance._lifecycle.IsCurrent(instance._lifecycleGeneration) ||
                    instance.root == null || instance.destroyed)
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

        // ================================================================ menu screen API (called from LobbySession)

        /// <summary>Clears the pending screen, ensures the hub is visible and switches to menu mode.</summary>
        public static void BeginMenu()
        {
            PendingButtons.Clear();
            PendingInfos.Clear();
            _ConnectionUI.InvalidateLobbyPlayerSlots();
            var instance = TryGetLiveInstance();
            if (instance != null)
            {
                instance._mode = UiMode.Menu;
                instance._menuVisible = false;
            }
            set_visible = true;
        }

        /// <summary>Adds a pretty button to the pending screen.</summary>
        public static void AddPendingButton(string label, string help, bool enabled, int color, Action onClick, bool fieldStyle = false)
        {
            PendingButtons.Add(new PendingButton
            {
                Label = label ?? string.Empty,
                Help = help ?? string.Empty,
                Enabled = enabled,
                Color = color,
                FieldStyle = fieldStyle,
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

        /// <summary>
        /// Renders the accumulated pending screen. Call once at the end of a LobbySession Show* method.
        /// <paramref name="hubLayout"/> is only for the first Host/Join screen.
        /// </summary>
        public static void CommitMenu(bool showLobby = false, bool hubLayout = false)
        {
            var instance = TryGetLiveInstance();
            if (instance == null)
                return;
            instance._mode = UiMode.Menu;
            instance._keepLobbyVisible = showLobby;
            instance._hubLayout = hubLayout && !showLobby;
            // Rebuild panel at the correct width (lobby column vs hub), then draw buttons.
            instance._menuVisible = true;
            instance.onResize();
        }

        /// <summary>Tear down menu chrome and hide the hub (does not call TitleScreen.mainMenu).</summary>
        public static void DismissAndHide()
        {
            var instance = TryGetLiveInstance();
            instance?.DismissMenuUi();
            set_visible = false;
        }

        /// <summary>Switches to the lobby display (player list + lobby code).</summary>
        public static void ShowLobbyMode()
        {
            var instance = TryGetLiveInstance();
            if (instance == null)
                return;
            _ConnectionUI.InvalidateLobbyPlayerSlots();
            instance._mode = UiMode.Lobby;
            instance._menuVisible = false;
            instance._keepLobbyVisible = false;
            instance._hubLayout = false;
            if (instance._menuRoot != null)
            {
                try { instance._menuRoot.visible = false; } catch { }
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
            // Hub layout = first Host/Join screen only. All other menus use the left column.
            bool hubLayout = this._hubLayout && !this._keepLobbyVisible;

            // Rebuild the absolute-positioned menu list container.
            var host = this._panelRoot;
            if (host == null)
                return;
            this._menuRoot?.remove();
            this._menuRoot = new dc.h2d.Object(null);
            host.addChild(this._menuRoot);
            this._menuChromeRoot = new dc.h2d.Object(this._menuRoot);
            this._menuHoverRoot = new dc.h2d.Object(this._menuRoot);
            this._menuLabelRoot = new dc.h2d.Object(this._menuRoot);
            this._menuHitRoot = new dc.h2d.Object(this._menuRoot);
            this._menuHitRects.Clear();
            ClearHoverBorder();
            this._menuEscapeAction = null;
            // Lobby-created screens (host status / client waiting): Escape must not fire Back/Disconnect.
            if (!this._keepLobbyVisible)
            {
                for (int i = 0; i < PendingButtons.Count; i++)
                {
                    var pending = PendingButtons[i];
                    if (pending.Enabled && pending.OnClick != null && IsEscapeNavButton(pending))
                        this._menuEscapeAction = pending.OnClick;
                }
            }

            double padX = 28.0 * uiScale;
            // Hub uses a centered cluster; other menus keep a left content column on the full-screen panel.
            // When the lobby card is up, leave room on the right so columns don't collide.
            double lobbyReserve = 0.0;
            if (this._keepLobbyVisible)
                lobbyReserve = GetLobbyPanelWidth(uiScale) + 24.0 * uiScale;
            double listW = hubLayout
                ? bgWidth - padX * 2.0
                : System.Math.Min(bgWidth - padX * 2.0 - lobbyReserve, SideContentWidth * uiScale);
            double colGap = 14.0 * uiScale;
            double rowGap = 16.0 * uiScale;
            // Keep button labels at their existing scale; menu information gets the requested
            // additional readability boost without changing button or prompt text.
            double buttonText = textUi * 1.55;
            double menuText = buttonText * MenuTextScaleBoost;
            double cursorY;

            if (this._keepLobbyVisible)
            {
                EnsureLobbyPanel();
                // Actions stay left; lobby card is top-right — no longer stacked under the list.
                cursorY = 22.0 * uiScale;
            }
            else
            {
                // Non-lobby screens: hide player-list chrome.
                HideLobbyPanel();
                cursorY = hubLayout ? 0.0 : 22.0 * uiScale;
            }

            var actions = new List<PendingButton>();
            var backs = new List<PendingButton>();
            if (hubLayout)
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

            // Hub only: button cluster at top-center of the LARGE background panel.
            double clusterW = listW;
            double clusterX = padX;
            if (hubLayout)
            {
                clusterW = System.Math.Min(listW, NavButtonClusterWidth * uiScale);
                clusterX = (bgWidth - clusterW) * 0.5;
                cursorY = 28.0 * uiScale;
            }

            // Soft content card behind the button stack so the full-screen panel feels less empty.
            {
                double cardPad = 18.0 * uiScale;
                double contentH = EstimateMenuStackHeight(hubLayout, actions, backs, uiScale, rowGap);
                double cardX = (hubLayout ? clusterX : padX) - cardPad;
                double cardY = cursorY - cardPad;
                double cardW = (hubLayout ? clusterW : listW) + cardPad * 2.0;
                double cardH = contentH + cardPad * 2.0;
                var cardGfx = new Graphics(this._menuChromeRoot ?? this._menuRoot);
                UiChrome.DrawContentCard(
                    cardGfx,
                    cardX,
                    cardY,
                    cardW,
                    cardH,
                    CardCornerRadius * uiScale,
                    ContentCardFill,
                    ContentCardEdge,
                    accentColor: AccentColor);
            }

            foreach (var info in PendingInfos)
            {
                var line = Assets.Class.makeText(
                    info.Text.AsHaxeString(),
                    Tools.MultiColor.ColorFromHex("#e0e0e0"),
                    false,
                    this._menuLabelRoot ?? this._menuRoot);
                double infoScale = 0.42 * menuText;
                line.customScale = infoScale;
                line.onResize();
                line.textColor = info.Color;
                if (hubLayout)
                    CenterMenuText(line, info.Text, clusterX, clusterW, infoScale);
                else
                    line.x = padX;
                line.y = cursorY;
                cursorY += 26.0 * uiScale;
            }

            if (PendingInfos.Count > 0)
                cursorY += 10.0 * uiScale;

            if (hubLayout)
            {
                // Row 1: Host | Join (with tips). Row 2: Back. Top-center of big panel.
                for (int i = 0; i < actions.Count; i += 2)
                {
                    bool pair = i + 1 < actions.Count;
                    if (pair)
                    {
                        double btnW = (clusterW - colGap) * 0.5;
                        double btnH = System.Math.Max(
                            GetButtonHeight(actions[i], showHelp: true, uiScale, styled: true),
                            GetButtonHeight(actions[i + 1], showHelp: true, uiScale, styled: true));
                        PlaceMenuButton(actions[i], clusterX, cursorY, btnW, btnH, buttonText, uiScale, showHelp: true, centerText: true, styled: true);
                        PlaceMenuButton(actions[i + 1], clusterX + btnW + colGap, cursorY, btnW, btnH, buttonText, uiScale, showHelp: true, centerText: true, styled: true);
                        cursorY += btnH + rowGap;
                    }
                    else
                    {
                        double btnH = GetButtonHeight(actions[i], showHelp: true, uiScale, styled: true);
                        PlaceMenuButton(actions[i], clusterX, cursorY, clusterW, btnH, buttonText, uiScale, showHelp: true, centerText: true, styled: true);
                        cursorY += btnH + rowGap;
                    }
                }

                for (int i = 0; i < backs.Count; i++)
                {
                    double btnH = GetButtonHeight(backs[i], showHelp: true, uiScale, styled: true);
                    PlaceMenuButton(backs[i], clusterX, cursorY, clusterW, btnH, buttonText, uiScale, showHelp: true, centerText: true, styled: true);
                    cursorY += btnH + rowGap;
                }
            }
            else
            {
                // Other menus: same new button style, stacked in the left styled panel.
                for (int i = 0; i < PendingButtons.Count; i++)
                {
                    var btn = PendingButtons[i];
                    double btnH = GetButtonHeight(btn, showHelp: true, uiScale, styled: true);
                    PlaceMenuButton(btn, padX, cursorY, listW, btnH, buttonText, uiScale, showHelp: true, centerText: false, styled: true);
                    cursorY += btnH + rowGap;
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

        /// <summary>Buttons Escape should trigger (Back, or Disconnect on the client lobby screen).</summary>
        private static bool IsEscapeNavButton(PendingButton btn)
        {
            if (IsBackButton(btn))
                return true;

            var label = btn.Label ?? string.Empty;
            if (label.IndexOf("disconnect", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            try
            {
                var localized = GetText.Instance.GetString("Disconnect");
                if (!string.IsNullOrEmpty(localized)
                    && string.Equals(label, localized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
            }
            return false;
        }

        private static double GetButtonHeight(PendingButton btn, bool showHelp, double uiScale, bool styled)
        {
            // Textblocks need a taller well once the value glyphs are larger.
            if (btn.FieldStyle)
                return 84.0 * uiScale;
            if (showHelp && !string.IsNullOrWhiteSpace(btn.Help))
                return (styled ? 96.0 : 50.0) * uiScale;
            return (styled ? 64.0 : 34.0) * uiScale;
        }

        /// <summary>Pre-measures the button/info stack so the content card can hug it.</summary>
        private static double EstimateMenuStackHeight(
            bool hubLayout,
            List<PendingButton> actions,
            List<PendingButton> backs,
            double uiScale,
            double rowGap)
        {
            double h = 0.0;
            if (PendingInfos.Count > 0)
            {
                h += PendingInfos.Count * 26.0 * uiScale;
                h += 10.0 * uiScale;
            }

            if (hubLayout)
            {
                for (int i = 0; i < actions.Count; i += 2)
                {
                    bool pair = i + 1 < actions.Count;
                    double btnH = pair
                        ? System.Math.Max(
                            GetButtonHeight(actions[i], showHelp: true, uiScale, styled: true),
                            GetButtonHeight(actions[i + 1], showHelp: true, uiScale, styled: true))
                        : GetButtonHeight(actions[i], showHelp: true, uiScale, styled: true);
                    h += btnH + rowGap;
                }

                for (int i = 0; i < backs.Count; i++)
                    h += GetButtonHeight(backs[i], showHelp: true, uiScale, styled: true) + rowGap;
            }
            else
            {
                for (int i = 0; i < PendingButtons.Count; i++)
                    h += GetButtonHeight(PendingButtons[i], showHelp: true, uiScale, styled: true) + rowGap;
            }

            // Drop the trailing gap; keep a little breathing room for the soft shadow.
            if (h > rowGap)
                h -= rowGap;
            h += 6.0 * uiScale;
            return System.Math.Max(h, 80.0 * uiScale);
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
            bool styled)
        {
            if (btn.FieldStyle)
            {
                PlaceMenuTextBlock(btn, x, y, w, h, textUi, uiScale);
                return;
            }

            DrawButtonPlate(btn, x, y, w, h, uiScale);

            double labelScale = (styled ? 0.72 : 0.48) * textUi;
            var label = Assets.Class.makeText(
                btn.Label.AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#ffffff"),
                false,
                this._menuLabelRoot ?? this._menuRoot);
            label.customScale = labelScale;
            label.onResize();
            label.textColor = btn.Enabled ? (btn.Color == 0xFFFFFF ? TextColor : btn.Color) : DisabledColor;

            bool hasHelp = showHelp && !string.IsNullOrWhiteSpace(btn.Help);
            if (centerText)
            {
                CenterMenuText(label, btn.Label, x, w, labelScale);
                label.y = hasHelp ? y + (styled ? 16.0 : 8.0) * uiScale : y + (h - 18.0 * uiScale) * 0.5;
            }
            else
            {
                label.x = x + 14.0 * uiScale;
                label.y = hasHelp ? y + 14.0 * uiScale : y + 16.0 * uiScale;
            }

            if (hasHelp)
            {
                double helpScale = (styled ? 0.60 : 0.40) * textUi;
                var help = Assets.Class.makeText(
                    btn.Help.AsHaxeString(),
                    Tools.MultiColor.ColorFromHex("#9098a8"),
                    false,
                    this._menuLabelRoot ?? this._menuRoot);
                help.customScale = helpScale;
                help.onResize();
                help.textColor = btn.Enabled ? HelpColor : DisabledHelpColor;
                if (centerText)
                {
                    CenterMenuText(help, btn.Help, x, w, helpScale);
                    help.y = y + h - (styled ? 36.0 : 22.0) * uiScale;
                }
                else
                {
                    help.x = x + 14.0 * uiScale;
                    help.y = y + h - 34.0 * uiScale;
                }
            }

            AttachMenuHit(btn, x, y, w, h, fieldHover: false);
        }

        /// <summary>
        /// Form-style textblock: muted caption above + recessed value well. Must not read as a button.
        /// </summary>
        private void PlaceMenuTextBlock(
            PendingButton btn,
            double x,
            double y,
            double w,
            double h,
            double textUi,
            double uiScale)
        {
            SplitFieldCaption(btn, out var caption, out var value);

            double captionScale = 0.48 * textUi;
            var captionText = Assets.Class.makeText(
                caption.AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#8a93a6"),
                false,
                this._menuLabelRoot ?? this._menuRoot);
            captionText.customScale = captionScale;
            captionText.onResize();
            captionText.textColor = btn.Enabled ? 0x8A93A6 : DisabledHelpColor;
            captionText.x = x + 2.0 * uiScale;
            captionText.y = y;

            double wellY = y + 28.0 * uiScale;
            double wellH = System.Math.Max(44.0 * uiScale, h - 32.0 * uiScale);
            DrawFieldBox(btn, x, wellY, w, wellH);

            double valueScale = 0.78 * textUi;
            var valueText = Assets.Class.makeText(
                value.AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#e8eef7"),
                false,
                this._menuLabelRoot ?? this._menuRoot);
            valueText.customScale = valueScale;
            valueText.onResize();
            valueText.textColor = btn.Enabled ? 0xE8EEF7 : DisabledColor;
            valueText.x = x + 14.0 * uiScale;
            // DC bitmap fonts report a tall box with empty ascent; visible pixels hug the
            // bottom of that box, so a naive mid-well Y leaves glyphs on the floor.
            valueText.y = GetFieldGlyphY(wellY, wellH, valueText, valueScale);

            // Quiet edit affordance — not a second button label.
            var editHint = Assets.Class.makeText(
                "›".AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#59d5ff"),
                false,
                this._menuLabelRoot ?? this._menuRoot);
            double hintScale = 0.72 * textUi;
            editHint.customScale = hintScale;
            editHint.onResize();
            editHint.textColor = AccentColor;
            double hintW = 10.0 * uiScale;
            try
            {
                hintW = editHint.textWidth;
                if (editHint.scaleX > 0.01)
                    hintW *= editHint.scaleX;
                else
                    hintW *= hintScale;
            }
            catch { }
            editHint.x = x + w - hintW - 14.0 * uiScale;
            editHint.y = GetFieldGlyphY(wellY, wellH, editHint, hintScale);

            AttachMenuHit(btn, x, wellY, w, wellH, fieldHover: true);
        }

        /// <summary>
        /// Y so the *visible* glyph band sits mid-well. Dead Cells ui.Text boxes are taller
        /// than the ink; centering the raw box parks the ink on the bottom edge.
        /// </summary>
        private static double GetFieldGlyphY(double wellY, double wellH, dc.ui.Text text, double scale)
        {
            double boxH = 20.0 * System.Math.Max(scale, 0.4);
            try
            {
                boxH = text.textHeight;
                if (text.scaleY > 0.01)
                    boxH *= text.scaleY;
                else
                    boxH *= scale;
            }
            catch
            {
            }

            if (boxH < 8.0)
                boxH = 20.0 * System.Math.Max(scale, 0.4);

            // Empty ascent ≈ top 38% of the reported box for this font atlas.
            // (Was 0.45 — sat a bit above optical center.)
            const double emptyAscentFrac = 0.38;
            double inkTopInBox = boxH * emptyAscentFrac;
            double inkH = boxH * (1.0 - emptyAscentFrac);
            // Place ink band in the vertical center of the well.
            return wellY + (wellH - inkH) * 0.5 - inkTopInBox;
        }

        private static void SplitFieldCaption(PendingButton btn, out string caption, out string value)
        {
            string raw = btn.Label ?? string.Empty;
            int idx = raw.IndexOf(':');
            if (idx > 0)
            {
                caption = raw.Substring(0, idx).Trim();
                value = raw.Substring(idx + 1).Trim();
                if (string.IsNullOrEmpty(value))
                    value = "—";
                return;
            }

            caption = !string.IsNullOrWhiteSpace(btn.Help) ? btn.Help.Trim() : "Value";
            value = string.IsNullOrWhiteSpace(raw) ? "—" : raw.Trim();
        }

        private void AttachMenuHit(PendingButton btn, double x, double y, double w, double h, bool fieldHover)
        {
            if (!btn.Enabled || btn.OnClick == null || this._menuRoot == null)
                return;

            var hitParent = this._menuHitRoot ?? this._menuRoot;
            var hit = new dc.h2d.Interactive(w, h, hitParent, null);
            hit.x = x;
            hit.y = y;
            var cb = btn.OnClick;
            double hx = x, hy = y, hw = w, hh = h;
            hit.onOver = new HlAction<Event>(_ =>
            {
                if (fieldHover)
                    SetFieldHoverBorder(hx, hy, hw, hh);
                else
                    SetHoverBorder(hx, hy, hw, hh);
            });
            hit.onOut = new HlAction<Event>(_ => ClearHoverBorder());
            hit.onClick = new HlAction<Event>(_ =>
            {
                ClearHoverBorder();
                try { cb(); }
                catch (Exception ex) { Log.Debug("[ConnectionUI] Button callback failed: {Message}", ex.Message); }
            });
            this._menuHitRects.Add((x, y, w, h, cb));
        }

        private void SetHoverBorder(double x, double y, double w, double h)
        {
            ClearHoverBorder();
            var parent = this._menuHoverRoot ?? this._menuRoot;
            if (parent == null)
                return;
            var g = new Graphics(parent);
            this._hoverBorder = g;
            double uiScale = UiScale.GetResolutionScale();
            UiChrome.DrawHoverRing(
                g,
                x,
                y,
                w,
                h,
                ButtonCornerRadius * uiScale,
                HoverBorderColor,
                PanelInner);
        }

        /// <summary>Same cyan hover language as buttons, punched with the field face color.</summary>
        private void SetFieldHoverBorder(double x, double y, double w, double h)
        {
            ClearHoverBorder();
            var parent = this._menuHoverRoot ?? this._menuRoot;
            if (parent == null)
                return;
            var g = new Graphics(parent);
            this._hoverBorder = g;
            double uiScale = UiScale.GetResolutionScale();
            UiChrome.DrawHoverRing(
                g,
                x,
                y,
                w,
                h,
                FieldCornerRadius * uiScale,
                HoverBorderColor,
                FieldFace);
        }

        private void ClearHoverBorder()
        {
            try { this._hoverBorder?.remove(); } catch { }
            this._hoverBorder = null;
        }

        private void DismissMenuUi()
        {
            this._menuVisible = false;
            this._hubLayout = false;
            this._mode = UiMode.Lobby;
            this._menuEscapeAction = null;
            ClearHoverBorder();
            CloseTextPrompt(apply: false);
            this.inter?.remove();
            this.inter = null;
            try { this._menuRoot?.remove(); } catch { }
            this._menuRoot = null;
            this._menuChromeRoot = null;
            this._menuHoverRoot = null;
            this._menuLabelRoot = null;
            this._menuHitRoot = null;
            ClearLobbyPanel();
            try { this._panelRoot?.remove(); } catch { }
            this._panelRoot = null;
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

        /// <summary>
        /// Recessed textblock well: soft shadow + rounded border + dug-in face.
        /// </summary>
        private void DrawFieldBox(PendingButton btn, double x, double y, double w, double h)
        {
            var parent = this._menuChromeRoot ?? this._menuRoot;
            if (parent == null)
                return;
            var g = new Graphics(parent);
            double uiScale = UiScale.GetResolutionScale();
            UiChrome.DrawInsetWell(
                g,
                x,
                y,
                w,
                h,
                FieldCornerRadius * uiScale,
                btn.Enabled ? FieldBorder : DisabledPlateEdge,
                btn.Enabled ? FieldFace : DisabledPlateFace,
                btn.Enabled);
        }

        private void DrawButtonPlate(PendingButton btn, double x, double y, double w, double h, double uiScale)
        {
            var parent = this._menuChromeRoot ?? this._menuRoot;
            if (parent == null)
                return;
            var g = new Graphics(parent);
            UiChrome.DrawRaisedPlate(
                g,
                x,
                y,
                w,
                h,
                ButtonCornerRadius * uiScale,
                btn.Enabled ? PanelInnerEdge : DisabledPlateEdge,
                btn.Enabled ? PanelInner : DisabledPlateFace,
                btn.Enabled ? PanelInnerTop : DisabledPlateTop,
                btn.Enabled);
        }

        // ================================================================ lobby display

        private const double LobbyPanelBaseWidth = 440.0;

        private static double GetLobbyPanelWidth(double uiScale)
        {
            return LobbyPanelBaseWidth * uiScale;
        }

        private void EnsureLobbyPanel()
        {
            if (this._panelRoot == null)
                return;

            if (this._lobbyPanelRoot == null)
            {
                this._lobbyPanelRoot = new dc.h2d.Object(null);
                this._panelRoot.addChild(this._lobbyPanelRoot);
            }

            try { this._lobbyPanelRoot.set_visible(true); } catch { }
            RebuildLobbyPanelContent(_ConnectionUI.GetLobbyPlayerSlots());
        }

        private void HideLobbyPanel()
        {
            if (this._lobbyPanelRoot != null)
            {
                try { this._lobbyPanelRoot.set_visible(false); } catch { }
            }
            HideLobbyBeheadedSprites();
        }

        private void ClearLobbyPanel()
        {
            ClearLobbyBeheadedSprites();
            try { this._lobbyPanelRoot?.remove(); } catch { }
            this._lobbyPanelRoot = null;
            this.connectionLabels.Clear();
            this.lastConnections.Clear();
            this.lastLobbySlotsSignature = string.Empty;
        }

        public void updateConnections()
        {
            RefreshConnections(null);
        }

        public static void NotifyConnectionsChanged()
        {
            _ConnectionUI.InvalidateLobbyPlayerSlots();
            TryGetLiveInstance()?.updateConnections();
        }

        private void RefreshConnections(List<string>? names)
        {
            if (!(this._mode == UiMode.Lobby || this._keepLobbyVisible))
                return;

            if (this._lobbyPanelRoot == null)
            {
                if (this._panelRoot == null)
                    return;
                EnsureLobbyPanel();
                return;
            }

            var slots = _ConnectionUI.GetLobbyPlayerSlots();
            if (!NeedsLobbySlotsRefresh(slots))
                return;
            RebuildLobbyPanelContent(slots);
        }

        private bool NeedsLobbySlotsRefresh(List<_ConnectionUI.LobbyPlayerSlot> slots)
        {
            var next = _ConnectionUI.BuildLobbySlotsSignature(slots);
            return !string.Equals(this.lastLobbySlotsSignature, next, StringComparison.Ordinal);
        }

        private void RebuildLobbyPanelContent(List<_ConnectionUI.LobbyPlayerSlot> slots)
        {
            if (this._lobbyPanelRoot == null || this._panelRoot == null)
                return;

            var uiScale = UiScale.GetResolutionScale();
            var textBoost = GetWindowedTextBoost();
            double textUi = System.Math.Max(uiScale, 1.0) * textBoost * 1.35;
            double panelW = GetLobbyPanelWidth(uiScale);
            double pad = 20.0 * uiScale;
            double screenPad = 28.0 * uiScale;

            string? lobbyCode = null;
            try
            {
                lobbyCode = LobbySession.GetSteamLobbyCodeForUi();
                if (string.IsNullOrWhiteSpace(lobbyCode))
                    lobbyCode = null;
                else
                    lobbyCode = lobbyCode.Trim().ToLowerInvariant();
            }
            catch
            {
                lobbyCode = null;
            }

            this._lobbyPanelRoot.removeChildren();
            this.connectionLabels.Clear();

            // Beheaded row IS the players list — only keep a small card when a Steam lobby code is shown.
            if (lobbyCode != null)
            {
                double codeBlockH = 70.0 * uiScale;
                double panelH = pad + codeBlockH + pad;

                var chrome = new Graphics(this._lobbyPanelRoot);
                UiChrome.DrawContentCard(
                    chrome,
                    0,
                    0,
                    panelW,
                    panelH,
                    CardCornerRadius * uiScale,
                    ContentCardFill,
                    ContentCardEdge);

                var codeCaption = Assets.Class.makeText(
                    GetText.Instance.GetString("Lobby code").AsHaxeString(),
                    Tools.MultiColor.ColorFromHex("#8a93a6"),
                    false,
                    this._lobbyPanelRoot);
                codeCaption.customScale = 0.42 * textUi;
                codeCaption.onResize();
                codeCaption.textColor = 0x8A93A6;
                codeCaption.x = pad;
                codeCaption.y = pad;

                var codeValue = Assets.Class.makeText(
                    lobbyCode.AsHaxeString(),
                    Tools.MultiColor.ColorFromHex("#59d5ff"),
                    false,
                    this._lobbyPanelRoot);
                codeValue.customScale = 0.62 * textUi;
                codeValue.onResize();
                codeValue.textColor = AccentColor;
                codeValue.x = pad;
                codeValue.y = pad + 26.0 * uiScale;

                this.lastLobbyIdLabelText = lobbyCode;
                this._lobbyPanelHeight = panelH;
                this._lobbyPanelRoot.x = this._layoutW - panelW - screenPad;
                this._lobbyPanelRoot.y = screenPad;
                try { this._lobbyPanelRoot.set_visible(true); } catch { }
            }
            else
            {
                this.lastLobbyIdLabelText = string.Empty;
                this._lobbyPanelHeight = 0;
                this._lobbyPanelRoot.x = this._layoutW - panelW - screenPad;
                this._lobbyPanelRoot.y = screenPad;
                try { this._lobbyPanelRoot.set_visible(false); } catch { }
            }

            this.lastConnections.Clear();
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].Occupied)
                    this.lastConnections.Add(slots[i].Nick);
            }
            this.lastLobbySlotsSignature = _ConnectionUI.BuildLobbySlotsSignature(slots);

            PlaceLobbyBeheadedUnderPlayerList(slots, panelW, uiScale, textUi, screenPad);

            if (this.lobbyCodeFlow != null)
            {
                try { this.lobbyCodeFlow.set_visible(false); } catch { }
            }
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

        private void UpdateLobbyIdLabel(bool forceRefreshText)
        {
            if (!(this._mode == UiMode.Lobby || this._keepLobbyVisible))
            {
                if (this.lobbyCodeFlow != null)
                {
                    try { this.lobbyCodeFlow.set_visible(false); } catch { }
                }
                return;
            }

            if (this._lobbyPanelRoot == null)
                return;

            if (!forceRefreshText)
            {
                if (this._lobbyCodeProbeCountdown > 0)
                {
                    this._lobbyCodeProbeCountdown--;
                    return;
                }

                this._lobbyCodeProbeCountdown = LayoutMetricProbeIntervalFrames;
            }

            string? lobbyCode = null;
            try
            {
                lobbyCode = LobbySession.GetSteamLobbyCodeForUi();
                if (string.IsNullOrWhiteSpace(lobbyCode))
                    lobbyCode = null;
                else
                    lobbyCode = lobbyCode.Trim().ToLowerInvariant();
            }
            catch
            {
                lobbyCode = null;
            }

            var next = lobbyCode ?? string.Empty;
            if (!forceRefreshText && string.Equals(this.lastLobbyIdLabelText, next, StringComparison.Ordinal))
                return;

            // Code appeared/changed — rebuild the card so the footer updates.
            RebuildLobbyPanelContent(_ConnectionUI.GetLobbyPlayerSlots());
        }

        // ================================================================ lifecycle

        private void clean()
        {
            ClearLobbyCodeUi();
            ClearLobbyPanel();
            ClearHoverBorder();
            CloseTextPrompt(apply: false);
            this._menuRoot?.remove();
            this._menuRoot = null;
            this._menuChromeRoot = null;
            this._menuHoverRoot = null;
            this._menuLabelRoot = null;
            this._menuHitRoot = null;
            this._panelRoot?.remove();
            this._panelRoot = null;
            this.rootFlow?.remove();
            this.inter?.remove();
            this.sprites.Clear();
        }

        // Button column width inside the full-screen panel (non-hub menus).
        private const int SideContentWidth = 560;
        private const double NavButtonClusterWidth = 780.0;
        private const double MenuTextScaleBoost = 1.15;
        public override void onResize()
        {
            base.onResize();
            if (this.rootFlow == null || base.root == null)
                return;

            var win = dc.hxd.Window.Class.getInstance();
            double screenWidth = win.get_width();
            double screenHeight = win.get_height();

            ClearLobbyCodeUi();
            this.inter?.remove();
            this.inter = null;

            if (this.rootFlow != null)
            {
                try { this.rootFlow.visible = false; } catch { }
            }

            // Panel is always absolute full screen — never a short/cropped box.
            BuildFullScreenPanel(screenWidth, screenHeight);
            this._lastLayoutUiScale = UiScale.GetResolutionScale();
            this._lastLayoutTextBoost = GetWindowedTextBoost();
            this._layoutMetricProbeCountdown = LayoutMetricProbeIntervalFrames;
            this._lobbyCodeProbeCountdown = LayoutMetricProbeIntervalFrames;

            bool showLobbyCard = this._mode == UiMode.Lobby || this._keepLobbyVisible;
            if (showLobbyCard)
                EnsureLobbyPanel();
            else
                HideLobbyPanel();

            if (this._mode == UiMode.Menu && this._menuVisible)
                RebuildMenuScreen();

            UpdateLobbyIdLabel(forceRefreshText: true);

            // Display-mode / resolution changes rebuild the panel on top of an open prompt.
            // Re-draw the prompt last so it stays above and matches the new size.
            if (this._promptOpen)
                RebuildTextPromptUi();
        }

        private void DrawStyledPanelFrame(dc.h2d.Object parent, double panelW, double panelH)
        {
            var g = new Graphics(parent);
            int fill = PanelInner;
            double fillAlpha = 0.94;
            g.beginFill(Ref<int>.From(ref fill), Ref<double>.From(ref fillAlpha));
            g.drawRect(0, 0, panelW, panelH);
            g.endFill();

            // Soft vignette corners so the full-bleed panel feels less flat.
            int vignette = 0x000000;
            double vA = 0.18;
            g.beginFill(Ref<int>.From(ref vignette), Ref<double>.From(ref vA));
            g.drawRect(0, 0, panelW, 18);
            g.drawRect(0, panelH - 28, panelW, 28);
            g.endFill();

            int edge = AccentColor;
            double edgeAlpha = 0.45;
            g.beginFill(Ref<int>.From(ref edge), Ref<double>.From(ref edgeAlpha));
            g.drawRect(0, 0, panelW, 2);
            g.drawRect(0, panelH - 2, panelW, 2);
            g.drawRect(0, 0, 2, panelH);
            g.drawRect(panelW - 2, 0, 2, panelH);
            g.endFill();
        }

        /// <summary>Full-screen styled backdrop for every ConnectionUI menu.</summary>
        private void BuildFullScreenPanel(double screenWidth, double screenHeight)
        {
            this._panelRoot?.remove();
            this._panelRoot = new dc.h2d.Object(null);
            this.root.addChild(this._panelRoot);

            int panelW = System.Math.Max(1, (int)screenWidth);
            int panelH = System.Math.Max(1, (int)screenHeight);

            this._layoutW = panelW;
            this._layoutH = panelH;
            DrawStyledPanelFrame(this._panelRoot, panelW, panelH);

            // Full panel rebuild invalidates any previous lobby card reference.
            this._lobbyPanelRoot = null;

            this._panelRoot.x = 0;
            this._panelRoot.y = 0;
            this._panelRoot.set_visible(true);

            this.inter = new dc.h2d.Interactive(panelW, panelH, this._panelRoot, null);
            this.inter.onClick = new HlAction<Event>(this.OnClick);
        }

        private void BGtext()
        {
            // Legacy entry point — lobby chrome now lives in EnsureLobbyPanel / RebuildLobbyPanelContent.
            EnsureLobbyPanel();
        }

        public override void update()
        {
            var perfEnabled = RuntimeHitchWatch.Enabled;
            var perfStart = perfEnabled ? RuntimeHitchWatch.Start() : 0L;
            base.update();

            // Some display-mode changes do not dispatch Process.onResize. Detect them from the
            // live window and rebuild after the game has applied the new metrics, otherwise text
            // keeps the old bitmap scale/positions while the panel has already moved.
            if (this._layoutMetricProbeCountdown > 0)
            {
                this._layoutMetricProbeCountdown--;
            }
            else
            {
                this._layoutMetricProbeCountdown = LayoutMetricProbeIntervalFrames;
                try
                {
                    var win = dc.hxd.Window.Class.getInstance();
                    var liveUiScale = UiScale.GetResolutionScale();
                    var liveTextBoost = GetWindowedTextBoost();
                    if (win != null &&
                        (win.get_width() != this._layoutW ||
                         win.get_height() != this._layoutH ||
                         double.IsNaN(this._lastLayoutUiScale) ||
                         System.Math.Abs(liveUiScale - this._lastLayoutUiScale) > 0.001 ||
                         System.Math.Abs(liveTextBoost - this._lastLayoutTextBoost) > 0.001))
                    {
                        this.onResize();
                    }
                }
                catch
                {
                }
            }

            bool promptWasOpen = this._promptOpen;
            TickTextPrompt();
            TickMenuEscape(promptWasOpen);

            // Lobby head particle emitters advance on a fixed 60fps step (game baseFps).
            try { LobbyHeadFx.TickAll(1.0 / 60.0); } catch { }

            if (this._mode != UiMode.Menu || this._keepLobbyVisible)
            {
                var slots = _ConnectionUI.GetLobbyPlayerSlots();
                if (NeedsLobbySlotsRefresh(slots))
                    RebuildLobbyPanelContent(slots);
                else
                    UpdateLobbyIdLabel(forceRefreshText: false);

                FlushPendingLobbyBeheadedSkinApply();
            }
            else if (this._menuVisible)
            {
                UpdateLobbyIdLabel(forceRefreshText: false);
            }

            // After skin rebind: step idle, then place heads on this frame's headBone.
            try { TickLobbyHeadBones(); } catch { }

            if (perfEnabled)
            {
                var elapsedMs = RuntimeHitchWatch.GetElapsedMilliseconds(perfStart);
                if (elapsedMs >= RuntimeHitchWatch.ModFrameSlowThresholdMs)
                    RuntimeHitchWatch.LogSlow(Log.Logger, "ConnectionUI.Update", elapsedMs);
            }
        }

        public override void onDispose()
        {
            if (!_lifecycle.TryBeginStop())
            {
                base.onDispose();
                return;
            }

            try
            {
                clean();
            }
            catch
            {
            }
            finally
            {
                _lifecycle.MarkDisposed();
                if (ReferenceEquals(Instance, this))
                    Instance = null;
                base.onDispose();
            }
        }

        public override void postUpdate()
        {
            base.postUpdate();
            // Title-screen processes can overwrite DirLighted globals after our update().
            // Push them again so ColorMap stays in the linked shader while the lobby is up.
            if (this._lobbyBeheadedRoot != null)
                PushLobbyBeheadedLighting();
        }

        /// <summary>Escape = same as Back/Disconnect, unless the text prompt consumed Escape this frame.</summary>
        private void TickMenuEscape(bool promptWasOpen)
        {
            if (promptWasOpen || this._promptOpen)
                return;
            // Host/client lobby (session already created): never treat Escape as Back/Disconnect.
            if (this._keepLobbyVisible)
                return;
            if (!this._menuVisible || this._menuEscapeAction == null)
                return;
            if (!set_visible)
                return;

            try
            {
                if (!Key.Class.isPressed(27))
                    return;
                var cb = this._menuEscapeAction;
                try { cb(); }
                catch (Exception ex) { Log.Debug("[ConnectionUI] Escape back failed: {Message}", ex.Message); }
            }
            catch
            {
            }
        }

        private void OnClick(Event e)
        {
            // Menu buttons (hit rects relative to the styled panel Interactive).
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

            // Lobby card click → copy Steam lobby code when present.
            if (this._lobbyPanelRoot == null || !this._lobbyPanelRoot.visible)
                return;
            if (string.IsNullOrEmpty(this.lastLobbyIdLabelText))
                return;

            var relX = e.relX;
            var relY = e.relY;
            double cardX = this._lobbyPanelRoot.x;
            double cardY = this._lobbyPanelRoot.y;
            double cardW = GetLobbyPanelWidth(UiScale.GetResolutionScale());
            double cardH = this._lobbyPanelHeight > 1.0
                ? this._lobbyPanelHeight
                : 420.0 * UiScale.GetResolutionScale();
            if (relX < cardX || relX > cardX + cardW || relY < cardY || relY > cardY + cardH)
                return;

            if (LobbySession.TryCopySteamLobbyCodeFromUi())
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
