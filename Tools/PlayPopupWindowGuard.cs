using System;
using dc.h2d;
using dc.tool;
using dc.ui;
using Serilog;
using LibsProcess = dc.libs.Process;
using Sound = dc.hxd.res.Sound;

namespace DeadCellsMultiplayerMod.Tools
{
    /// <summary>
    /// Suppresses the title-screen cross-promo widget ("Play Windblown now!" / similar).
    /// That UI is <see cref="NewsPanel"/> (and sometimes <see cref="UpdatePopUp"/>), not a Win32 window.
    /// </summary>
    internal static class PlayPopupWindowGuard
    {
        private const string MatchPhrase = "Windblow";
        private const long FallbackPollIntervalMs = 500;

        private static bool _hooksInstalled;
        private static bool _loggedNewsSuppress;
        private static bool _loggedPopUpSuppress;
        private static long _nextFallbackTicks = -1;
        private static WeakReference<NewsPanel>? _newsRef;

        internal static void Tick()
        {
            EnsureHooks();

            var now = Environment.TickCount64;
            if (now < _nextFallbackTicks)
                return;
            _nextFallbackTicks = now + FallbackPollIntervalMs;

            try
            {
                if (_newsRef != null && _newsRef.TryGetTarget(out var news) && news != null)
                    SuppressNewsPanel(news, "fallback", scrubContent: false);
            }
            catch
            {
            }
        }

        private static void EnsureHooks()
        {
            if (_hooksInstalled)
                return;

            try
            {
                Hook__NewsPanel.__constructor__ += OnNewsPanelConstructed;
                Hook_NewsPanel.onData += OnNewsPanelOnData;
                Hook_NewsPanel.updateVisible += OnNewsPanelUpdateVisible;
                Hook_NewsPanel.update += OnNewsPanelUpdate;
                Hook_NewsPanel.openNews += OnNewsPanelOpenNews;

                Hook__UpdatePopUp.__constructor__ += OnUpdatePopUpConstructed;
                Hook_UpdatePopUp.update += OnUpdatePopUpUpdate;

                _hooksInstalled = true;
                Log.Debug("[NetMod] PlayPopupWindowGuard hooks installed (NewsPanel / UpdatePopUp)");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[NetMod] PlayPopupWindowGuard failed to install hooks");
            }
        }

        private static void OnNewsPanelConstructed(Hook__NewsPanel.orig___constructor__ orig, NewsPanel self, LibsProcess parent)
        {
            orig(self, parent);
            _newsRef = new WeakReference<NewsPanel>(self);
            SuppressNewsPanel(self, "ctor", scrubContent: true);
        }

        private static void OnNewsPanelOnData(Hook_NewsPanel.orig_onData orig, NewsPanel self, Result result)
        {
            orig(self, result);
            _newsRef = new WeakReference<NewsPanel>(self);
            SuppressNewsPanel(self, "onData", scrubContent: true);
        }

        private static void OnNewsPanelUpdateVisible(Hook_NewsPanel.orig_updateVisible orig, NewsPanel self)
        {
            orig(self);
            SuppressNewsPanel(self, "updateVisible", scrubContent: false);
        }

        private static void OnNewsPanelUpdate(Hook_NewsPanel.orig_update orig, NewsPanel self)
        {
            orig(self);
            SuppressNewsPanel(self, "update", scrubContent: false);
        }

        private static void OnNewsPanelOpenNews(Hook_NewsPanel.orig_openNews orig, NewsPanel self)
        {
            // Block Steam / browser redirect for the promo tile.
            SuppressNewsPanel(self, "openNews", scrubContent: true);
        }

        private static void OnUpdatePopUpConstructed(Hook__UpdatePopUp.orig___constructor__ orig, UpdatePopUp self, Process from, Sound validSfx)
        {
            orig(self, from, validSfx);
            TrySuppressUpdatePopUp(self, "ctor");
        }

        private static void OnUpdatePopUpUpdate(Hook_UpdatePopUp.orig_update orig, UpdatePopUp self)
        {
            orig(self);
            TrySuppressUpdatePopUp(self, "update");
        }

        private static void SuppressNewsPanel(NewsPanel? self, string reason, bool scrubContent)
        {
            if (self == null)
                return;

            try
            {
                if (!self.hidden)
                    self.hidden = true;

                if (scrubContent)
                {
                    try
                    {
                        self.clean();
                    }
                    catch
                    {
                    }
                }

                HideRoot(self.root);

                if (!_loggedNewsSuppress)
                {
                    _loggedNewsSuppress = true;
                    Log.Information("[NetMod] Suppressed title NewsPanel ({Reason})", reason);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[NetMod] NewsPanel suppress failed ({Reason})", reason);
            }
        }

        private static void TrySuppressUpdatePopUp(UpdatePopUp? self, string reason)
        {
            if (self == null || self.closing)
                return;

            if (!LooksLikeWindblownPromo(ReadLabel(self.title), ReadLabel(self.text)))
                return;

            try
            {
                HideRoot(self.root);
                self.close();

                if (!_loggedPopUpSuppress)
                {
                    _loggedPopUpSuppress = true;
                    Log.Information("[NetMod] Closed Windblown UpdatePopUp ({Reason})", reason);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[NetMod] UpdatePopUp suppress failed ({Reason})", reason);
            }
        }

        private static bool LooksLikeWindblownPromo(string? title, string? body)
        {
            if (ContainsPhrase(title) || ContainsPhrase(body))
                return true;

            if (!string.IsNullOrEmpty(body)
                && body.IndexOf("DASH, DIVE", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        private static bool ContainsPhrase(string? value)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(MatchPhrase, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string? ReadLabel(dc.ui.Text? text)
        {
            if (text == null)
                return null;

            try
            {
                var raw = text.rawText?.ToString();
                if (!string.IsNullOrEmpty(raw))
                    return raw;
            }
            catch
            {
            }

            try
            {
                return text.text?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static void HideRoot(Layers? root)
        {
            if (root == null)
                return;

            try
            {
                if (root.visible)
                    root.visible = false;
            }
            catch
            {
            }
        }
    }
}
