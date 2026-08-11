using System;
using System.Reflection;
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
    /// Supports caret movement, selection highlight, and clipboard.
    /// </summary>
    public partial class ConnectionUI
    {
        private const int KeyBackspace = 8;
        private const int KeyTab = 9;
        private const int KeyEnter = 13;
        private const int KeyEscape = 27;
        private const int KeySpace = 32;
        private const int KeyCtrl = 17;
        private const int KeyLCtrl = 162;
        private const int KeyRCtrl = 163;
        private const int KeyShift = 16;
        private const int KeyLShift = 160;
        private const int KeyRShift = 161;
        private const int KeyLeft = 37;
        private const int KeyUp = 38;
        private const int KeyRight = 39;
        private const int KeyDown = 40;
        private const int KeyHome = 36;
        private const int KeyEnd = 35;
        private const int KeyDelete = 46;
        private const int KeyA = 65;
        private const int KeyC = 67;
        private const int KeyV = 86;
        private const int KeyX = 88;
        private const int CfUnicodeText = 13;
        private const uint GmemMoveable = 0x0002;

        private static readonly int PromptPlaceholderColor = 0x6E778A;
        private static readonly int PromptValueColor = 0xE8EEF7;
        private static readonly int PromptCaretColor = 0x59D5FF;
        private static readonly int PromptSelectionColor = 0x2F6FA8;
        private static readonly int PromptFieldBorder = 0x46546F;
        private static readonly int PromptFieldFace = 0x0C121D;

        private dc.h2d.Object? _promptRoot;
        private dc.h2d.Object? _promptFieldParent;
        private dc.ui.Text? _promptValueText;
        private dc.ui.Text? _promptPlaceholderText;
        private dc.ui.Text? _promptMeasureText;
        private Graphics? _promptSelectionGfx;
        private Graphics? _promptCaretGfx;
        private string _promptBuffer = string.Empty;
        private string _promptTitle = string.Empty;
        private string _promptPlaceholder = string.Empty;
        private string _promptRenderedBuffer = "\u0001";
        private int _promptCaret;
        private int _promptSelStart;
        private int _promptSelEnd;
        private int _promptRenderedCaret = int.MinValue;
        private int _promptRenderedSelStart = int.MinValue;
        private int _promptRenderedSelEnd = int.MinValue;
        private bool _promptRenderedCaretVisible = true;
        private bool _promptNoSpaces;
        private bool _promptOpen;
        private Action<string>? _promptOnOk;
        private Action? _promptOnCancel;
        private HlAction<Event>? _promptWindowHandler;
        private double _promptCaretBlink;
        private bool _promptBackspaceHandledFrame;
        private bool _promptCharFromEventFrame;
        private double _promptTextUi;
        private double _promptFieldTextX;
        private double _promptFieldTextY;
        private double _promptFieldX;
        private double _promptFieldY;
        private double _promptFieldW;
        private double _promptFieldH;
        private double _promptValueScale;

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
            this._promptPlaceholder = string.IsNullOrWhiteSpace(title) ? "…" : title.Trim();
            this._promptBuffer = initial ?? string.Empty;
            this._promptCaret = this._promptBuffer.Length;
            this._promptSelStart = 0;
            this._promptSelEnd = this._promptBuffer.Length; // open with full selection (standard dialog UX)
            this._promptNoSpaces = noSpaces;
            this._promptOnOk = onOk;
            this._promptOnCancel = onCancel;
            this._promptOpen = true;
            this._promptCaretBlink = 0;
            this._promptCharFromEventFrame = false;
            this._promptRenderedBuffer = "\u0001";
            this._promptRenderedCaret = int.MinValue;
            this._promptRenderedSelStart = int.MinValue;
            this._promptRenderedSelEnd = int.MinValue;

            EnsurePromptWindowHook();
            SetHxdTextInput(enabled: true);
            RebuildTextPromptUi();
        }

        private void CloseTextPrompt(bool apply)
        {
            SetHxdTextInput(enabled: false);

            if (!this._promptOpen && this._promptRoot == null)
                return;

            var onOk = this._promptOnOk;
            var onCancel = this._promptOnCancel;
            var value = this._promptBuffer;

            this._promptOpen = false;
            this._promptOnOk = null;
            this._promptOnCancel = null;
            this._promptValueText = null;
            this._promptPlaceholderText = null;
            this._promptMeasureText = null;
            this._promptSelectionGfx = null;
            this._promptCaretGfx = null;
            this._promptFieldParent = null;
            this._promptRenderedBuffer = "\u0001";

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

        private static void SetHxdTextInput(bool enabled)
        {
            try
            {
                var win = Window.Class.getInstance();
                if (win == null)
                    return;

                var type = win.GetType();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

                if (!enabled)
                {
                    foreach (var name in new[] { "stopTextInput", "textInputStop", "endTextInput" })
                    {
                        if (TryInvokeNoArgs(win, type, name, flags))
                            return;
                    }
                    return;
                }

                foreach (var name in new[] { "startTextInput", "setTextInput", "textInputStart", "beginTextInput" })
                {
                    if (TryInvokeNoArgs(win, type, name, flags))
                        return;

                    foreach (var method in type.GetMethods(flags))
                    {
                        if (!string.Equals(method.Name, name, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var ps = method.GetParameters();
                        if (ps.Length == 0)
                            continue;
                        try
                        {
                            object?[] args = new object?[ps.Length];
                            for (int i = 0; i < ps.Length; i++)
                                args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : CreateDefaultArg(ps[i].ParameterType);
                            method.Invoke(win, args);
                            return;
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static bool TryInvokeNoArgs(object win, System.Type type, string name, BindingFlags flags)
        {
            try
            {
                var method = type.GetMethod(name, flags, binder: null, types: System.Type.EmptyTypes, modifiers: null)
                    ?? type.GetMethod(name, flags);
                if (method == null || method.GetParameters().Length != 0)
                    return false;
                method.Invoke(win, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object? CreateDefaultArg(System.Type t)
        {
            if (t == typeof(int) || t == typeof(uint) || t == typeof(short) || t == typeof(byte))
                return 0;
            if (t == typeof(double) || t == typeof(float))
                return 0.0;
            if (t == typeof(bool))
                return false;
            if (t.IsValueType)
            {
                try { return Activator.CreateInstance(t); } catch { return null; }
            }
            try
            {
                var ctors = t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var ctor in ctors)
                {
                    var cps = ctor.GetParameters();
                    if (cps.Length == 4
                        && cps[0].ParameterType == typeof(int)
                        && cps[1].ParameterType == typeof(int)
                        && cps[2].ParameterType == typeof(int)
                        && cps[3].ParameterType == typeof(int))
                    {
                        return ctor.Invoke(new object[] { 0, 0, 400, 40 });
                    }
                    if (cps.Length == 4
                        && (cps[0].ParameterType == typeof(double) || cps[0].ParameterType == typeof(float)))
                    {
                        return ctor.Invoke(new object[] { 0.0, 0.0, 400.0, 40.0 });
                    }
                }
                var empty = t.GetConstructor(System.Type.EmptyTypes);
                if (empty != null)
                    return empty.Invoke(null);
            }
            catch
            {
            }
            return null;
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

        private bool PromptHasSelection => this._promptSelEnd > this._promptSelStart;

        private void ClampPromptCaretState()
        {
            int len = this._promptBuffer?.Length ?? 0;
            if (this._promptCaret < 0) this._promptCaret = 0;
            if (this._promptCaret > len) this._promptCaret = len;
            if (this._promptSelStart < 0) this._promptSelStart = 0;
            if (this._promptSelEnd < 0) this._promptSelEnd = 0;
            if (this._promptSelStart > len) this._promptSelStart = len;
            if (this._promptSelEnd > len) this._promptSelEnd = len;
            if (this._promptSelStart > this._promptSelEnd)
            {
                int t = this._promptSelStart;
                this._promptSelStart = this._promptSelEnd;
                this._promptSelEnd = t;
            }
        }

        private void ClearPromptSelectionToCaret()
        {
            this._promptSelStart = this._promptCaret;
            this._promptSelEnd = this._promptCaret;
        }

        private void SelectPromptAll()
        {
            this._promptSelStart = 0;
            this._promptSelEnd = this._promptBuffer.Length;
            this._promptCaret = this._promptSelEnd;
            this._promptCaretBlink = 0;
        }

        private int _promptSelAnchor = -1;

        private void HandlePromptCaretMove(int newIndex, bool extend)
        {
            int len = this._promptBuffer.Length;
            if (newIndex < 0) newIndex = 0;
            if (newIndex > len) newIndex = len;

            if (extend)
            {
                if (this._promptSelAnchor < 0)
                    this._promptSelAnchor = this._promptCaret;
                this._promptCaret = newIndex;
                this._promptSelStart = System.Math.Min(this._promptSelAnchor, this._promptCaret);
                this._promptSelEnd = System.Math.Max(this._promptSelAnchor, this._promptCaret);
            }
            else
            {
                this._promptSelAnchor = -1;
                this._promptCaret = newIndex;
                ClearPromptSelectionToCaret();
            }

            this._promptCaretBlink = 0;
            ClampPromptCaretState();
            RefreshPromptValueText();
        }

        private string GetPromptSelectedText()
        {
            ClampPromptCaretState();
            if (!PromptHasSelection)
                return string.Empty;
            return this._promptBuffer.Substring(this._promptSelStart, this._promptSelEnd - this._promptSelStart);
        }

        private void DeletePromptSelection()
        {
            ClampPromptCaretState();
            if (!PromptHasSelection)
                return;
            this._promptBuffer = this._promptBuffer.Remove(this._promptSelStart, this._promptSelEnd - this._promptSelStart);
            this._promptCaret = this._promptSelStart;
            this._promptSelAnchor = -1;
            ClearPromptSelectionToCaret();
        }

        private void InsertPromptText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            if (this._promptNoSpaces)
                text = text.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (text.Length == 0)
                return;

            if (PromptHasSelection)
                DeletePromptSelection();

            ClampPromptCaretState();
            this._promptBuffer = this._promptBuffer.Insert(this._promptCaret, text);
            this._promptCaret += text.Length;
            this._promptSelAnchor = -1;
            ClearPromptSelectionToCaret();
            this._promptCaretBlink = 0;
            RefreshPromptValueText();
        }

        private void RemovePromptBackward()
        {
            // EKeyDown + ETextInput (WM_CHAR 8) + Tick isPressed can all see one Backspace.
            if (this._promptBackspaceHandledFrame)
                return;
            this._promptBackspaceHandledFrame = true;
            if (PromptHasSelection)
            {
                DeletePromptSelection();
                RefreshPromptValueText();
                return;
            }
            if (this._promptCaret <= 0 || this._promptBuffer.Length == 0)
                return;
            this._promptBuffer = this._promptBuffer.Remove(this._promptCaret - 1, 1);
            this._promptCaret--;
            ClearPromptSelectionToCaret();
            this._promptCaretBlink = 0;
            RefreshPromptValueText();
        }

        private void RemovePromptForward()
        {
            if (this._promptBackspaceHandledFrame)
                return;
            this._promptBackspaceHandledFrame = true;
            if (PromptHasSelection)
            {
                DeletePromptSelection();
                RefreshPromptValueText();
                return;
            }
            if (this._promptCaret >= this._promptBuffer.Length)
                return;
            this._promptBuffer = this._promptBuffer.Remove(this._promptCaret, 1);
            ClearPromptSelectionToCaret();
            this._promptCaretBlink = 0;
            RefreshPromptValueText();
        }

        private void OnPromptWindowEvent(Event e)
        {
            if (!this._promptOpen || e?.kind == null)
                return;

            if (e.kind.Index == EventKind.Indexes.ETextInput)
            {
                int code = e.charCode;
                // Backspace/Delete/nav are handled only on EKeyDown (+ Tick fallback).
                // Handling them here as well deleted two characters per keypress.
                if (code < 32)
                    return;
                if (code == KeyEnter || code == KeyEscape || code == KeyTab)
                    return;

                char ch = (char)code;
                if (this._promptNoSpaces && ch == ' ')
                    return;

                this._promptCharFromEventFrame = true;
                InsertPromptText(ch.ToString());
                return;
            }

            if (e.kind.Index == EventKind.Indexes.EKeyDown)
            {
                int code = e.keyCode;
                bool shift = IsPromptShiftDown();
                bool ctrl = IsPromptCtrlDown();

                if (code == KeyEnter)
                {
                    this._promptBackspaceHandledFrame = true;
                    CloseTextPrompt(apply: true);
                }
                else if (code == KeyEscape)
                {
                    this._promptBackspaceHandledFrame = true;
                    CloseTextPrompt(apply: false);
                }
                else if (code == KeyBackspace)
                {
                    RemovePromptBackward();
                }
                else if (code == KeyDelete)
                {
                    RemovePromptForward();
                }
                else if (code == KeyLeft)
                {
                    this._promptBackspaceHandledFrame = true;
                    HandlePromptCaretMove(this._promptCaret - 1, shift);
                }
                else if (code == KeyRight)
                {
                    this._promptBackspaceHandledFrame = true;
                    HandlePromptCaretMove(this._promptCaret + 1, shift);
                }
                else if (code == KeyHome || code == KeyUp)
                {
                    this._promptBackspaceHandledFrame = true;
                    HandlePromptCaretMove(0, shift);
                }
                else if (code == KeyEnd || code == KeyDown)
                {
                    this._promptBackspaceHandledFrame = true;
                    HandlePromptCaretMove(this._promptBuffer.Length, shift);
                }
                else if (ctrl && code == KeyA)
                {
                    this._promptBackspaceHandledFrame = true;
                    this._promptSelAnchor = 0;
                    SelectPromptAll();
                    RefreshPromptValueText();
                }
            }
        }

        private static bool IsPromptShiftDown()
        {
            return Key.Class.isDown(KeyShift) || Key.Class.isDown(KeyLShift) || Key.Class.isDown(KeyRShift);
        }

        private static bool IsPromptCtrlDown()
        {
            return Key.Class.isDown(KeyCtrl) || Key.Class.isDown(KeyLCtrl) || Key.Class.isDown(KeyRCtrl);
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
                bool ctrl = IsPromptCtrlDown();

                // Backspace / Delete / arrows / Enter / Escape are handled only in
                // OnPromptWindowEvent (EKeyDown). Polling them here again deleted two
                // characters (or moved the caret twice) whenever both paths saw the press.

                if (ctrl && Key.Class.isPressed(KeyA))
                {
                    this._promptSelAnchor = 0;
                    SelectPromptAll();
                    RefreshPromptValueText();
                }
                else if (ctrl && Key.Class.isPressed(KeyC))
                {
                    var selected = PromptHasSelection ? GetPromptSelectedText() : this._promptBuffer;
                    TrySetClipboardText(selected ?? string.Empty);
                }
                else if (ctrl && Key.Class.isPressed(KeyX))
                {
                    var selected = PromptHasSelection ? GetPromptSelectedText() : this._promptBuffer;
                    TrySetClipboardText(selected ?? string.Empty);
                    if (PromptHasSelection)
                    {
                        DeletePromptSelection();
                        RefreshPromptValueText();
                    }
                    else
                    {
                        this._promptBuffer = string.Empty;
                        this._promptCaret = 0;
                        ClearPromptSelectionToCaret();
                        RefreshPromptValueText();
                    }
                }
                else if (ctrl && Key.Class.isPressed(KeyV))
                {
                    var clip = TryGetClipboardText();
                    if (!string.IsNullOrEmpty(clip))
                        InsertPromptText(clip);
                }
                else if (!ctrl && !this._promptCharFromEventFrame)
                {
                    PollPromptTypedKeys();
                }
            }
            catch
            {
            }

            this._promptCharFromEventFrame = false;
            this._promptBackspaceHandledFrame = false;
            RefreshPromptValueText(blink: true);
        }

        private void PollPromptTypedKeys()
        {
            bool shift = IsPromptShiftDown();

            if (!this._promptNoSpaces && Key.Class.isPressed(KeySpace))
            {
                InsertPromptText(" ");
                return;
            }

            for (int k = 65; k <= 90; k++)
            {
                if (!Key.Class.isPressed(k))
                    continue;
                char ch = shift ? (char)k : (char)(k + 32);
                InsertPromptText(ch.ToString());
                return;
            }

            if (Key.Class.isPressed(48)) { InsertPromptText(shift ? ")" : "0"); return; }
            if (Key.Class.isPressed(49)) { InsertPromptText(shift ? "!" : "1"); return; }
            if (Key.Class.isPressed(50)) { InsertPromptText(shift ? "@" : "2"); return; }
            if (Key.Class.isPressed(51)) { InsertPromptText(shift ? "#" : "3"); return; }
            if (Key.Class.isPressed(52)) { InsertPromptText(shift ? "$" : "4"); return; }
            if (Key.Class.isPressed(53)) { InsertPromptText(shift ? "%" : "5"); return; }
            if (Key.Class.isPressed(54)) { InsertPromptText(shift ? "^" : "6"); return; }
            if (Key.Class.isPressed(55)) { InsertPromptText(shift ? "&" : "7"); return; }
            if (Key.Class.isPressed(56)) { InsertPromptText(shift ? "*" : "8"); return; }
            if (Key.Class.isPressed(57)) { InsertPromptText(shift ? "(" : "9"); return; }

            for (int k = 96; k <= 105; k++)
            {
                if (!Key.Class.isPressed(k))
                    continue;
                InsertPromptText(((char)('0' + (k - 96))).ToString());
                return;
            }

            if (Key.Class.isPressed(186)) { InsertPromptText(shift ? ":" : ";"); return; }
            if (Key.Class.isPressed(187)) { InsertPromptText(shift ? "+" : "="); return; }
            if (Key.Class.isPressed(188)) { InsertPromptText(shift ? "<" : ","); return; }
            if (Key.Class.isPressed(189)) { InsertPromptText(shift ? "_" : "-"); return; }
            if (Key.Class.isPressed(190)) { InsertPromptText(shift ? ">" : "."); return; }
            if (Key.Class.isPressed(191)) { InsertPromptText(shift ? "?" : "/"); return; }
            if (Key.Class.isPressed(192)) { InsertPromptText(shift ? "~" : "`"); return; }
            if (Key.Class.isPressed(219)) { InsertPromptText(shift ? "{" : "["); return; }
            if (Key.Class.isPressed(220)) { InsertPromptText(shift ? "|" : "\\"); return; }
            if (Key.Class.isPressed(221)) { InsertPromptText(shift ? "}" : "]"); return; }
            if (Key.Class.isPressed(222)) { InsertPromptText(shift ? "\"" : "'"); return; }
            if (Key.Class.isPressed(110)) { InsertPromptText("."); return; }
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
            this._promptTextUi = textUi;
            this._promptValueScale = 0.92 * textUi;

            double panelW = System.Math.Min(screenW * 0.56, 720.0 * uiScale);
            if (panelW < 440)
                panelW = System.Math.Min(440, screenW - 40);
            double pad = 24.0 * uiScale;
            double titleH = 32.0 * uiScale;
            double fieldH = 68.0 * uiScale;
            double btnH = 56.0 * uiScale;
            double gap = 20.0 * uiScale;
            double panelH = pad + titleH + gap + fieldH + gap + btnH + pad;

            var dim = new Graphics(this._promptRoot);
            int dimColor = 0x000000;
            double dimA = 0.55;
            dim.beginFill(Ref<int>.From(ref dimColor), Ref<double>.From(ref dimA));
            dim.drawRect(0, 0, screenW, screenH);
            dim.endFill();
            var dimHit = new dc.h2d.Interactive(screenW, screenH, this._promptRoot, null);
            dimHit.x = 0;
            dimHit.y = 0;

            var card = new dc.h2d.Object(this._promptRoot);
            card.x = (screenW - panelW) * 0.5;
            card.y = (screenH - panelH) * 0.5;
            this._promptFieldParent = card;

            var block = new dc.h2d.Interactive(panelW, panelH, card, null);
            block.x = 0;
            block.y = 0;

            var g = new Graphics(card);
            UiChrome.DrawSoftShadow(g, 0, 0, panelW, panelH, CardCornerRadius * uiScale, offsetX: 5.0, offsetY: 8.0);
            UiChrome.FillRoundRect(g, 0, 0, panelW, panelH, CardCornerRadius * uiScale, PanelInnerEdge, 0.85);
            UiChrome.FillRoundRect(
                g,
                1.5,
                1.5,
                panelW - 3.0,
                panelH - 3.0,
                System.Math.Max(0.0, CardCornerRadius * uiScale - 1.5),
                PanelInner,
                0.98);
            UiChrome.FillRoundRect(g, 0, 0, panelW, panelH, CardCornerRadius * uiScale, AccentColor, 0.18);
            // Re-draw face so the cyan is only a soft outer lip, not a full tint.
            UiChrome.FillRoundRect(
                g,
                2.0,
                2.0,
                panelW - 4.0,
                panelH - 4.0,
                System.Math.Max(0.0, CardCornerRadius * uiScale - 2.0),
                PanelInner,
                0.98);

            var title = Assets.Class.makeText(
                this._promptTitle.AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#f7fc65"),
                false,
                card);
            title.customScale = 0.58 * textUi;
            title.onResize();
            title.textColor = 0xF7FC65;
            CenterMenuText(title, this._promptTitle, pad, panelW - pad * 2, 0.58 * textUi);
            title.y = pad;

            double fieldX = pad;
            double fieldY = pad + titleH + gap;
            double fieldW = panelW - pad * 2;
            DrawPromptFieldBox(g, fieldX, fieldY, fieldW, fieldH);

            this._promptFieldX = fieldX;
            this._promptFieldY = fieldY;
            this._promptFieldW = fieldW;
            this._promptFieldH = fieldH;
            this._promptFieldTextX = fieldX + 16.0 * uiScale;
            // Optical vertical center — dc.ui.Text.textHeight is inflated and pushes glyphs down.
            this._promptFieldTextY = GetPromptTextY();

            this._promptSelectionGfx = new Graphics(card);

            this._promptPlaceholderText = Assets.Class.makeText(
                this._promptPlaceholder.AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#6e778a"),
                false,
                card);
            this._promptPlaceholderText.customScale = this._promptValueScale;
            this._promptPlaceholderText.onResize();
            this._promptPlaceholderText.textColor = PromptPlaceholderColor;
            this._promptPlaceholderText.x = this._promptFieldTextX;
            this._promptPlaceholderText.y = this._promptFieldTextY;

            this._promptMeasureText = Assets.Class.makeText(
                "".AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#ffffff"),
                false,
                card);
            this._promptMeasureText.customScale = this._promptValueScale;
            this._promptMeasureText.onResize();
            this._promptMeasureText.x = -10000;
            this._promptMeasureText.y = -10000;
            try { this._promptMeasureText.set_visible(false); } catch { }

            this._promptValueText = null;
            this._promptCaretGfx = null;
            this._promptRenderedBuffer = "\u0001";
            this._promptRenderedCaret = int.MinValue;
            this._promptRenderedSelStart = int.MinValue;
            this._promptRenderedSelEnd = int.MinValue;
            RefreshPromptValueText();

            // Caret above glyphs.
            this._promptCaretGfx = new Graphics(card);
            this._promptRenderedCaret = int.MinValue;
            RefreshPromptValueText();

            double btnW = (fieldW - 12.0 * uiScale) * 0.5;
            double btnY = panelH - pad - btnH;
            PlacePromptButton(GetText.Instance.GetString("OK"), fieldX, btnY, btnW, btnH, textUi, uiScale, () => CloseTextPrompt(apply: true), card);
            PlacePromptButton(GetText.Instance.GetString("Cancel"), fieldX + btnW + 12.0 * uiScale, btnY, btnW, btnH, textUi, uiScale, () => CloseTextPrompt(apply: false), card);
        }

        private static void DrawPromptFieldBox(Graphics g, double x, double y, double w, double h)
        {
            double uiScale = UiScale.GetResolutionScale();
            UiChrome.DrawInsetWell(
                g,
                x,
                y,
                w,
                h,
                FieldCornerRadius * uiScale,
                PromptFieldBorder,
                PromptFieldFace,
                enabled: true);
        }

        private void PlacePromptButton(
            string label,
            double x,
            double y,
            double w,
            double h,
            double textUi,
            double uiScale,
            Action onClick,
            dc.h2d.Object parent)
        {
            var g = new Graphics(parent);
            UiChrome.DrawRaisedPlate(
                g,
                x,
                y,
                w,
                h,
                ButtonCornerRadius * uiScale,
                PanelInnerEdge,
                PanelInner,
                PanelInnerTop,
                enabled: true);

            var text = Assets.Class.makeText(
                label.AsHaxeString(),
                Tools.MultiColor.ColorFromHex("#ffffff"),
                false,
                parent);
            double scale = 0.78 * textUi;
            text.customScale = scale;
            text.onResize();
            text.textColor = TextColor;
            CenterMenuText(text, label, x, w, scale);
            text.y = y + (h - 22.0 * uiScale) * 0.5;

            var hit = new dc.h2d.Interactive(w, h, parent, null);
            hit.x = x;
            hit.y = y;
            Graphics? hover = null;
            hit.onOver = new HlAction<Event>(_ =>
            {
                try { hover?.remove(); } catch { }
                hover = new Graphics(parent);
                UiChrome.DrawHoverRing(
                    hover,
                    x,
                    y,
                    w,
                    h,
                    ButtonCornerRadius * uiScale,
                    HoverBorderColor,
                    PanelInner);
                // Keep the ring under the label so text stays visible.
                int textIndex = 0;
                try { textIndex = parent.getChildIndex(text); } catch { textIndex = 0; }
                if (textIndex < 0)
                    textIndex = 0;
                parent.addChildAt(hover, textIndex);
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

        /// <summary>
        /// Optical Y for prompt glyphs inside the field. Do not use textHeight — DC fonts
        /// report a tall box and the label ends up sitting too low (extra empty space on top).
        /// </summary>
        private double GetPromptTextY()
        {
            // Same empty-ascent compensation as menu textblocks.
            double boxH = 30.0 * System.Math.Max(this._promptValueScale, 0.4);
            const double emptyAscentFrac = 0.38;
            double inkTopInBox = boxH * emptyAscentFrac;
            double inkH = boxH * (1.0 - emptyAscentFrac);
            return this._promptFieldY + (this._promptFieldH - inkH) * 0.5 - inkTopInBox;
        }

        private double MeasurePromptTextWidth(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            try
            {
                if (this._promptMeasureText == null)
                    return text.Length * 11.0 * this._promptValueScale;

                this._promptMeasureText.set_text(text.AsHaxeString());
                this._promptMeasureText.customScale = this._promptValueScale;
                this._promptMeasureText.onResize();
                double w = this._promptMeasureText.textWidth;
                if (this._promptMeasureText.scaleX > 0.01)
                    w *= this._promptMeasureText.scaleX;
                else
                    w *= this._promptValueScale;
                return System.Math.Max(0, w);
            }
            catch
            {
                return text.Length * 11.0 * this._promptValueScale;
            }
        }

        private void RefreshPromptValueText(bool blink = false)
        {
            if (this._promptFieldParent == null)
                return;

            ClampPromptCaretState();
            string buffer = this._promptBuffer ?? string.Empty;
            bool empty = string.IsNullOrEmpty(buffer);
            bool showCaret = (!blink || this._promptCaretBlink < 0.5) && !PromptHasSelection;

            if (this._promptPlaceholderText != null)
            {
                try
                {
                    this._promptFieldTextY = GetPromptTextY();
                    this._promptPlaceholderText.customScale = this._promptValueScale;
                    this._promptPlaceholderText.onResize();
                    this._promptPlaceholderText.x = this._promptFieldTextX;
                    this._promptPlaceholderText.y = this._promptFieldTextY;
                    this._promptPlaceholderText.set_visible(empty && !PromptHasSelection);
                }
                catch
                {
                    try { this._promptPlaceholderText.visible = empty; } catch { }
                }
            }

            bool bufferChanged = !string.Equals(this._promptRenderedBuffer, buffer, StringComparison.Ordinal);
            bool caretChanged = this._promptRenderedCaret != this._promptCaret
                || this._promptRenderedCaretVisible != showCaret;
            bool selChanged = this._promptRenderedSelStart != this._promptSelStart
                || this._promptRenderedSelEnd != this._promptSelEnd;

            if (!bufferChanged && !caretChanged && !selChanged && this._promptValueText != null)
                return;

            this._promptRenderedBuffer = buffer;
            this._promptRenderedCaret = this._promptCaret;
            this._promptRenderedSelStart = this._promptSelStart;
            this._promptRenderedSelEnd = this._promptSelEnd;
            this._promptRenderedCaretVisible = showCaret;

            // Selection highlight behind the glyphs.
            try { this._promptSelectionGfx?.clear(); } catch { }
            if (this._promptSelectionGfx != null && PromptHasSelection)
            {
                string before = buffer.Substring(0, this._promptSelStart);
                string selected = buffer.Substring(this._promptSelStart, this._promptSelEnd - this._promptSelStart);
                double x0 = this._promptFieldTextX + MeasurePromptTextWidth(before);
                double selW = System.Math.Max(4.0, MeasurePromptTextWidth(selected));
                double padY = 6.0;
                int col = PromptSelectionColor;
                double a = 0.55;
                this._promptSelectionGfx.beginFill(Ref<int>.From(ref col), Ref<double>.From(ref a));
                this._promptSelectionGfx.drawRect(
                    x0,
                    this._promptFieldY + padY,
                    selW,
                    this._promptFieldH - padY * 2.0);
                this._promptSelectionGfx.endFill();
            }

            if (bufferChanged || this._promptValueText == null)
            {
                try { this._promptValueText?.remove(); } catch { }
                this._promptValueText = Assets.Class.makeText(
                    buffer.AsHaxeString(),
                    Tools.MultiColor.ColorFromHex("#e8eef7"),
                    false,
                    this._promptFieldParent);
                this._promptValueText.customScale = this._promptValueScale;
                this._promptValueText.onResize();
                this._promptValueText.textColor = PromptValueColor;
                this._promptValueText.x = this._promptFieldTextX;
                this._promptValueText.y = this._promptFieldTextY;
            }

            try
            {
                this._promptFieldTextY = GetPromptTextY();
                this._promptValueText!.set_text(buffer.AsHaxeString());
                this._promptValueText.customScale = this._promptValueScale;
                this._promptValueText.textColor = PromptValueColor;
                this._promptValueText.onResize();
                this._promptValueText.x = this._promptFieldTextX;
                this._promptValueText.y = this._promptFieldTextY;
                this._promptValueText.set_visible(!empty);
            }
            catch { }

            // Keep value under caret; selection gfx was created earlier so it stays behind.
            try { if (this._promptValueText != null) this._promptFieldParent.addChild(this._promptValueText); } catch { }
            try { if (this._promptCaretGfx != null) this._promptFieldParent.addChild(this._promptCaretGfx); } catch { }

            // Caret bar (not part of the string — so arrow movement is visible).
            try { this._promptCaretGfx?.clear(); } catch { }
            if (this._promptCaretGfx != null && showCaret)
            {
                string beforeCaret = buffer.Substring(0, this._promptCaret);
                double caretX = this._promptFieldTextX + MeasurePromptTextWidth(beforeCaret);
                double caretH = this._promptFieldH - 16.0;
                double caretY = this._promptFieldY + 8.0;
                int col = PromptCaretColor;
                double a = 1.0;
                this._promptCaretGfx.beginFill(Ref<int>.From(ref col), Ref<double>.From(ref a));
                this._promptCaretGfx.drawRect(caretX, caretY, 2.0, caretH);
                this._promptCaretGfx.endFill();
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
