using System;
using dc.hxd;

namespace DeadCellsMultiplayerMod.Tools
{
    public static class UiScale
    {
        private const double ReferenceWidth = 1920.0;
        private const double MinScale = 0.9;
        private const double MaxScale = 1.15;

        /// <summary>After device connect/disconnect the window can briefly report 0×0; avoid blurry/wrong UI scaling.</summary>
        private static double s_lastGoodScale = 1.0;

        /// <summary>
        /// Width-based scale for ConnectionUI. Dead Cells windowed/fullscreen toggles often change
        /// usable height (title bar / taskbar) without changing width; Min(scaleW, scaleH) made the
        /// hub typography and spacing jump with that height. Width matches the game's horizontal
        /// stage sizing more stably across display modes.
        /// </summary>
        public static double GetResolutionScale()
        {
            var win = Window.Class.getInstance();
            if (win == null)
                return s_lastGoodScale;

            double width = win.get_width();
            if (width <= 0)
                return s_lastGoodScale;

            var scale = width / ReferenceWidth;
            if (scale <= 0)
                return 1.0;

            // Ease scaling: boost small windows, tame large resolutions.
            scale = System.Math.Sqrt(scale);
            if (scale < MinScale)
                scale = MinScale;
            if (scale > MaxScale)
                scale = MaxScale;
            s_lastGoodScale = scale;
            return scale;
        }
    }
}
