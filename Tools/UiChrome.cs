using dc.h2d;
using HaxeProxy.Runtime;

namespace DeadCellsMultiplayerMod.Tools
{
    /// <summary>
    /// Shared Graphics chrome for ConnectionUI: soft shadows, round-rect fills, raised plates,
    /// inset wells, and hover rings. Dead Cells' <see cref="Graphics"/> has no drawRoundRect,
    /// so corners are composited from rects + <see cref="Graphics.drawCircle"/>.
    /// </summary>
    internal static class UiChrome
    {
        /// <summary>
        /// Fills a rounded rectangle without overlapping regions (safe for translucent fills).
        /// </summary>
        public static void FillRoundRect(
            Graphics g,
            double x,
            double y,
            double w,
            double h,
            double radius,
            int color,
            double alpha)
        {
            if (g == null || w <= 0.5 || h <= 0.5)
                return;

            double r = radius;
            if (r < 0.0)
                r = 0.0;
            if (r * 2.0 > w)
                r = w * 0.5;
            if (r * 2.0 > h)
                r = h * 0.5;

            int fillColor = color;
            double fillAlpha = alpha;
            g.beginFill(Ref<int>.From(ref fillColor), Ref<double>.From(ref fillAlpha));

            if (r < 0.75)
            {
                g.drawRect(x, y, w, h);
                g.endFill();
                return;
            }

            // Center column (full height) + side strips (excluding corners) + four corner discs.
            g.drawRect(x + r, y, w - 2.0 * r, h);
            g.drawRect(x, y + r, r, h - 2.0 * r);
            g.drawRect(x + w - r, y + r, r, h - 2.0 * r);

            var segments = Ref<int>.Null;
            g.drawCircle(x + r, y + r, r, segments);
            g.drawCircle(x + w - r, y + r, r, segments);
            g.drawCircle(x + r, y + h - r, r, segments);
            g.drawCircle(x + w - r, y + h - r, r, segments);
            g.endFill();
        }

        public static void DrawSoftShadow(
            Graphics g,
            double x,
            double y,
            double w,
            double h,
            double radius,
            double offsetX = 3.0,
            double offsetY = 5.0)
        {
            if (g == null)
                return;

            FillRoundRect(g, x + offsetX, y + offsetY, w, h, radius + 1.0, 0x000000, 0.22);
            FillRoundRect(g, x + offsetX * 0.55, y + offsetY * 0.55, w, h, radius, 0x000000, 0.14);
            FillRoundRect(g, x + 1.0, y + 2.0, w, h, radius, 0x000000, 0.08);
        }

        /// <summary>Raised menu button: shadow + edge + face + top highlight.</summary>
        public static void DrawRaisedPlate(
            Graphics g,
            double x,
            double y,
            double w,
            double h,
            double radius,
            int edgeColor,
            int faceColor,
            int highlightColor,
            bool enabled)
        {
            if (g == null)
                return;

            if (enabled)
                DrawSoftShadow(g, x, y, w, h, radius);
            else
                DrawSoftShadow(g, x, y, w, h, radius, offsetX: 2.0, offsetY: 3.0);

            FillRoundRect(g, x, y, w, h, radius, edgeColor, enabled ? 1.0 : 0.88);

            double inset = 2.0;
            double innerR = System.Math.Max(0.0, radius - inset);
            FillRoundRect(g, x + inset, y + inset, w - inset * 2.0, h - inset * 2.0, innerR, faceColor, enabled ? 1.0 : 0.92);

            // Soft top sheen — quieter when disabled, but still navy (not flat gray).
            double hx = x + radius;
            double hw = w - radius * 2.0;
            if (hw > 8.0)
            {
                int hi = highlightColor;
                double a = enabled ? 1.0 : 0.35;
                g.beginFill(Ref<int>.From(ref hi), Ref<double>.From(ref a));
                g.drawRect(hx, y + inset + 1.0, hw, 2.0);
                g.endFill();
            }
        }

        /// <summary>Recessed text field well: light shadow + border + dug-in face.</summary>
        public static void DrawInsetWell(
            Graphics g,
            double x,
            double y,
            double w,
            double h,
            double radius,
            int borderColor,
            int faceColor,
            bool enabled)
        {
            if (g == null)
                return;

            if (enabled)
                DrawSoftShadow(g, x, y, w, h, radius, offsetX: 2.0, offsetY: 3.0);

            FillRoundRect(g, x, y, w, h, radius, borderColor, 1.0);

            double inset = 1.5;
            double innerR = System.Math.Max(0.0, radius - inset);
            FillRoundRect(g, x + inset, y + inset, w - inset * 2.0, h - inset * 2.0, innerR, faceColor, 1.0);

            // Top shade so the well reads as inset, not a raised plate.
            int shade = 0x000000;
            double shadeA = enabled ? 0.42 : 0.2;
            double sx = x + radius;
            double sw = w - radius * 2.0;
            if (sw > 4.0)
            {
                g.beginFill(Ref<int>.From(ref shade), Ref<double>.From(ref shadeA));
                g.drawRect(sx, y + inset, sw, 2.5);
                g.endFill();
            }
        }

        /// <summary>
        /// Rounded cyan hover ring. Drawn on a layer beneath labels, so the opaque
        /// face punch restores the plate without covering text.
        /// </summary>
        public static void DrawHoverRing(
            Graphics g,
            double x,
            double y,
            double w,
            double h,
            double radius,
            int ringColor,
            int faceColor)
        {
            if (g == null || w <= 1.0 || h <= 1.0)
                return;

            const double glow = 4.0;
            const double thick = 2.5;

            // Soft outer halo (rounded).
            FillRoundRect(g, x - glow, y - glow, w + glow * 2.0, h + glow * 2.0, radius + glow, ringColor, 0.20);
            // Solid rounded ring shell.
            FillRoundRect(g, x - thick, y - thick, w + thick * 2.0, h + thick * 2.0, radius + thick, ringColor, 0.95);
            // Punch the interior back to the plate/field face (hover layer sits under labels).
            FillRoundRect(g, x, y, w, h, radius, faceColor, 1.0);
        }

        /// <summary>Content card behind a button stack — softens empty full-screen navy.</summary>
        public static void DrawContentCard(
            Graphics g,
            double x,
            double y,
            double w,
            double h,
            double radius,
            int fillColor,
            int edgeColor,
            int accentColor = 0)
        {
            if (g == null || w <= 1.0 || h <= 1.0)
                return;

            DrawSoftShadow(g, x, y, w, h, radius, offsetX: 4.0, offsetY: 7.0);
            FillRoundRect(g, x, y, w, h, radius, edgeColor, 0.55);
            FillRoundRect(g, x + 1.5, y + 1.5, w - 3.0, h - 3.0, System.Math.Max(0.0, radius - 1.5), fillColor, 0.92);

            // Quiet top lip.
            int lip = 0x3A4A6E;
            double lipA = 0.55;
            double lx = x + radius;
            double lw = w - radius * 2.0;
            if (lw > 8.0)
            {
                g.beginFill(Ref<int>.From(ref lip), Ref<double>.From(ref lipA));
                g.drawRect(lx, y + 2.0, lw, 2.0);
                g.endFill();
            }

            // Optional menu-only signal mark. The lobby card keeps the quieter vanilla chrome.
            if (accentColor != 0)
            {
                double accentA = 0.72;
                int accentW = (int)System.Math.Min(92.0, System.Math.Max(24.0, w * 0.22));
                g.beginFill(Ref<int>.From(ref accentColor), Ref<double>.From(ref accentA));
                g.drawRect(x + radius, y + 2.0, accentW, 2.0);
                g.drawRect(x + 2.0, y + radius, 2.0, System.Math.Min(46.0, h - radius * 2.0));
                g.endFill();
            }
        }
    }
}
