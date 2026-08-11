using System;
using System.Runtime.InteropServices;
using dc;
using dc.h2d;
using dc.hxd;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Modules;
using ModCore.Utilities;
using Serilog;
using DeadCellsMultiplayerMod.Tools;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.Connection
{
    /// <summary>
    /// Styled text-prompt overlay for ConnectionUI (replaces the stock ugly TextInput dialog).
    /// </summary>
    public partial class ConnectionUI
    {
        private const int KeyBackspace = 8;
        private const int KeyEnter = 13;
        private const int KeyEscape = 27;
        private const int KeyCtrl = 17;
        private const int KeyLCtrl = 162;
        private const int KeyRCtrl = 163;
        private const int KeyC = 67;
        private const int KeyV = 86;
        private const int CfUnicodeText = 13;
        private const uint GmemMoveable = 0x0002;

        private dc.h2d.Object? _promptRoot;
        private dc.ui.Text? _promptValueText;
        private string _promptBuffer = string.Empty;
        private string _promptTitle = string.Empty;
        private bool _promptNoSpaces;
        private bool _promptOpen;
        private Action<string>? _promptOnOk;
        private Action? _promptOnCancel;
        private HlAction<Event>? _promptWindowHandler;
        private double _promptCaretBlink;

        /// <summary>Opens a styled text prompt centered on screen.</summary>
        public static void ShowTextPrompt(
            string title,
            string initial,
            Action<string> onOk,
            Action? onCancel = null,
            bool noSpaces = false)
        {
            var instance = TryGetLiveInstance();
            if (instance == null)
                return;
            set_visible = true;
            instance.OpenTextPrompt(title, initial ?? string.Empty, onOk, onCancel, noSpaces);
        }

        public static bool IsTextPromptOpen()
        {
            var instance = TryGetLiveInstance();
            return instance != null && instance._promptOpen;
        }

        private void OpenTextPrompt(
            string title,
            string initial,
            Action<string> onOk,
            Action? onCancel,
            bool noSpaces)
        {
            CloseTextPrompt(apply: false);

            if (noSpaces && initial.Contains(' ', StringComparison.Ordinal))
                initial = initial.Replace(" ", string.Empty, StringComparison.Ordinal);

            this._promptTitle = title ?? string.Empty;
            this._promptBuffer = initial ?? string.Empty;
            this._promptNoSpaces = noSpaces;
            this._promptOnOk = onOk;
            this._promptOnCancel = onCancel;
            this._promptOpen = true;
            this._promptCaretBlink = 0;

            EnsurePromptWindowHook();
            RebuildTextPromptUi();
        }

        private void CloseTextPrompt(bool apply)
        {
            if (!this._promptOpen && this._promptRoot == null)
                return;

            var onOk = this._promptOnOk;
            var onCancel = this._promptOnCancel;
            var value = this._promptBuffer;

            this._promptOpen = false;
            this._promptOnOk = null;
            this._promptOnCancel = null;
            this._promptValueText = null;

            try { this._promptRoot?.remove(); } catch { }
            this._promptRoot = null;

            if (apply)
            {
                try { onOk?.Invoke(value); } catch (Exception ex) { Log.Debug("[ConnectionUI] TextPrompt OK failed: {Message}", ex.Message); }
            }
            else
            {
                try { onCancel?.Invoke(); } catch { }
            }
        }

        private void EnsurePromptWindowHook()
        {
            if (this._promptWindowHandler != null)
                return;
            try
            {
                var win = Window.Class.getInstance();
                if (win == null)
                    return;
                this._promptWindowHandler = new HlAction<Event>(OnPromptWindowEvent);
                win.addEventTarget(this._promptWindowHandler);
            }
            catch
            {
            }
        }

        private void OnPromptWindowEvent(Event e)
        {
            if (!this._promptOpen || e?.kind == null)
                return;
            if (e.kind.Index != EventKind.Indexes.ETextInput)
                return;

            int code = e.charCode;
            if (code < 32)
                return;
            // Ignore control chars.
            if (code == KeyBackspace || code == KeyEnter || code == KeyEscape)
                return;

            char ch = (char)code;
            if (this._promptNoSpaces && ch == ' ')
                return;

            this._promptBuffer += ch;
            RefreshPromptValueText();
        }

        private void TickTextPrompt()
        {
            if (!this._promptOpen)
                return;

            this._promptCaretBlink += 0.05;
            if (this._promptCaretBlink > 1.0)
                this._promptCaretBlink = 0;

            try
            {
                if (Key.Class.isPressed(KeyEscape))
                {
                    CloseTextPrompt(apply: false);
                    return;
                }
                if (Key.Class.isPressed(KeyEnter))
                {
                    CloseTextPrompt(apply: true);
                    return;
                }
                if (Key.Class.isPressed(KeyBackspace) && this._promptBuffer.Length > 0)
                {
                    this._promptBuffer = this._promptBuffer.Substring(0, this._promptBuffer.Length - 1);
                    RefreshPromptValueText();
                }

                bool ctrl =
                    Key.Class.isDown(KeyCtrl) ||
                    Key.Class.isDown(KeyLCtrl) ||
                    Key.Class.isDown(KeyRCtrl);
                if (ctrl && Key.Class.isPressed(KeyV))
                {
                    var clip = TryGetClipboardText();
                    if (!string.IsNullOrEmpty(clip))
                    {
                        if (this._promptNoSpaces)
                            clip = clip.Replace(" ", string.Empty, StringComparison.Ordinal);
                        this._promptBuffer += clip;
                        RefreshPromptValueText();
                    }
                }
                else if (ctrl && Key.Class.isPressed(KeyC))
                {
                    TrySetClipboardText(this._promptBuffer);
                }
            }
            catch
            {
            }

            // Blink caret in the value label.
            RefreshPromptValueText(blink: true);
        }

        private void RebuildTextPromptUi()
        {
            try { this._promptRoot?.remove(); } catch { }
            this._promptRoot = new dc.h2d.Object(null);
            this.root.addChild(this._promptRoot);

            var win = Window.Class.getInstance();
            double screenW = win?.get_width() ?? 1280;
            double screenH = win?.get_height() ?? 720;
            var uiScale = UiScale.GetResolutionScale();
            var textUi = System.Math.Max(uiScale, 1.0) * GetWindowedTextBoost();

            double panelW = System.Math.Min(screenW * 0.55, 720.0 * uiScale);
            if (panelW < 420)
                panelW = System.Math.Min(420, screenW - 40);
            double panelH = 220.0 * uiScale;
            double pad = 22.0 * uiScale;

            this._promptRoot.x = (screenW - panelW) * 0.5;
            this._promptRoot.y = (screenH - panelH) * 0.5;

            // Full-card blocker first so empty areas don't click through; OK/Cancel are added after.
            var block = new dc.h2d.Interactive(panelW, panelH, this._promptRoot, null);
            block.x = 0;
            block.y = 0;

            var g = new Graphics(this._promptRoot);
            int fill = PanelInner;
            double fillA = 0.97;
            g.beginFill(Ref<int>.From(ref fill), Ref<double>.From(ref fillA));
            g.drawRect(0, 0, panelW, panelH);
            g.endFill();

            int edge = AccentColor;
            double edgeA = 0.75;
            g.beginFill(Ref<int>.From(ref edge), Ref<double>.From(ref edgeA));
            g.drawRect(0, 0, panelW, 2);
            g.drawRect(0, panelH - 2, panelW, 2);
            g.drawRect(0, 0, 2, panelH);
            g.drawRect(panelW - 2, 0, 2, panelH);
            g.endFill();

            var title = Assets.Class.makeText(
                this._promptTitle.AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#f7fc65"),
                false,
                this._promptRoot);
            title.customScale = 0.55 * textUi;
            title.onResize();
            title.textColor = 0xF7FC65;
            CenterMenuText(title, this._promptTitle, pad, panelW - pad * 2, 0.55 * textUi);
            title.y = pad;

            // Input field plate.
            double fieldX = pad;
            double fieldY = pad + 48.0 * uiScale;
            double fieldW = panelW - pad * 2;
            double fieldH = 56.0 * uiScale;
            int fieldFill = 0x0C1018;
            double fieldA = 1.0;
            g.beginFill(Ref<int>.From(ref fieldFill), Ref<double>.From(ref fieldA));
            g.drawRect(fieldX, fieldY, fieldW, fieldH);
            g.endFill();
            int fieldEdge = 0x59D5FF;
            double fieldEdgeA = 0.65;
            g.beginFill(Ref<int>.From(ref fieldEdge), Ref<double>.From(ref fieldEdgeA));
            g.drawRect(fieldX, fieldY, fieldW, 2);
            g.drawRect(fieldX, fieldY + fieldH - 2, fieldW, 2);
            g.drawRect(fieldX, fieldY, 2, fieldH);
            g.drawRect(fieldX + fieldW - 2, fieldY, 2, fieldH);
            g.endFill();

            this._promptValueText = Assets.Class.makeText(
                "".AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#ffffff"),
                false,
                this._promptRoot);
            this._promptValueText.customScale = 0.50 * textUi;
            this._promptValueText.onResize();
            this._promptValueText.textColor = TextColor;
            this._promptValueText.x = fieldX + 12.0 * uiScale;
            this._promptValueText.y = fieldY + 14.0 * uiScale;
            RefreshPromptValueText();

            // OK / Cancel row.
            double btnW = (fieldW - 12.0 * uiScale) * 0.5;
            double btnH = 44.0 * uiScale;
            double btnY = panelH - pad - btnH;
            PlacePromptButton(GetText.Instance.GetString("OK"), fieldX, btnY, btnW, btnH, textUi, uiScale, () => CloseTextPrompt(apply: true));
            PlacePromptButton(GetText.Instance.GetString("Cancel"), fieldX + btnW + 12.0 * uiScale, btnY, btnW, btnH, textUi, uiScale, () => CloseTextPrompt(apply: false));
        }

        private void PlacePromptButton(
            string label,
            double x,
            double y,
            double w,
            double h,
            double textUi,
            double uiScale,
            Action onClick)
        {
            if (this._promptRoot == null)
                return;

            var g = new Graphics(this._promptRoot);
            int edge = PanelInnerEdge;
            double a = 1.0;
            g.beginFill(Ref<int>.From(ref edge), Ref<double>.From(ref a));
            g.drawRect(x, y, w, h);
            g.endFill();
            int inner = PanelInner;
            g.beginFill(Ref<int>.From(ref inner), Ref<double>.From(ref a));
            g.drawRect(x + 2, y + 2, w - 4, h - 4);
            g.endFill();
            int top = PanelInnerTop;
            g.beginFill(Ref<int>.From(ref top), Ref<double>.From(ref a));
            g.drawRect(x + 4, y + 3, w - 8, 2);
            g.endFill();

            var text = Assets.Class.makeText(
                label.AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#ffffff"),
                false,
                this._promptRoot);
            double scale = 0.48 * textUi;
            text.customScale = scale;
            text.onResize();
            text.textColor = TextColor;
            CenterMenuText(text, label, x, w, scale);
            text.y = y + (h - 16.0 * uiScale) * 0.5;

            var hit = new dc.h2d.Interactive(w, h, this._promptRoot, null);
            hit.x = x;
            hit.y = y;
            Graphics? hover = null;
            hit.onOver = new HlAction<Event>(_ =>
            {
                try { hover?.remove(); } catch { }
                hover = new Graphics(this._promptRoot);
                int white = HoverBorderColor;
                double ha = 1.0;
                const double t = 2.0;
                hover.beginFill(Ref<int>.From(ref white), Ref<double>.From(ref ha));
                hover.drawRect(x, y, w, t);
                hover.drawRect(x, y + h - t, w, t);
                hover.drawRect(x, y, t, h);
                hover.drawRect(x + w - t, y, t, h);
                hover.endFill();
            });
            hit.onOut = new HlAction<Event>(_ =>
            {
                try { hover?.remove(); } catch { }
                hover = null;
            });
            hit.onClick = new HlAction<Event>(_ =>
            {
                try { hover?.remove(); } catch { }
                onClick();
            });
        }

        private void RefreshPromptValueText(bool blink = false)
        {
            if (this._promptValueText == null)
                return;
            string shown = this._promptBuffer ?? string.Empty;
            if (blink && this._promptCaretBlink < 0.5)
                shown += "|";
            try
            {
                this._promptValueText.text = shown.AsHaxeString();
                this._promptValueText.onResize();
            }
            catch
            {
            }
        }

        private static string? TryGetClipboardText()
        {
            try
            {
                if (!IsClipboardFormatAvailable(CfUnicodeText))
                    return null;
                if (!OpenClipboard(IntPtr.Zero))
                    return null;
                try
                {
                    var handle = GetClipboardData(CfUnicodeText);
                    if (handle == IntPtr.Zero)
                        return null;
                    var ptr = GlobalLock(handle);
                    if (ptr == IntPtr.Zero)
                        return null;
                    try { return Marshal.PtrToStringUni(ptr); }
                    finally { GlobalUnlock(handle); }
                }
                finally { CloseClipboard(); }
            }
            catch { return null; }
        }

        private static bool TrySetClipboardText(string text)
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero))
                    return false;
                try
                {
                    if (!EmptyClipboard())
                        return false;
                    var bytes = (text.Length + 1) * 2;
                    var hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
                    if (hGlobal == IntPtr.Zero)
                        return false;
                    var target = GlobalLock(hGlobal);
                    if (target == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }
                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                        Marshal.WriteInt16(target, text.Length * 2, 0);
                    }
                    finally { GlobalUnlock(hGlobal); }

                    if (SetClipboardData(CfUnicodeText, hGlobal) == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }
                    return true;
                }
                finally { CloseClipboard(); }
            }
            catch { return false; }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);
    }
}
