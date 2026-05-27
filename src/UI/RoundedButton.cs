using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace EQ2Lexicon.ACTPlugin
{
    /// <summary>
    /// Button subclass with an anti-aliased rounded-rect background.
    /// WinForms' stock Button only paints a rectangle (FlatStyle=Flat)
    /// or whatever the system visual style draws (FlatStyle=Standard);
    /// neither rounds corners. Owner-drawing the background ourselves
    /// is the only way to get a consistent radius across XP/7/10/11
    /// without dragging in a third-party control library.
    ///
    /// Visual states tracked locally so we don't fight the system
    /// hot-tracking that the base button would apply:
    ///   • normal     → BackColor
    ///   • hover      → HoverColor
    ///   • pressed    → PressedColor (left mouse held down inside)
    ///   • disabled   → BackColor at 50% alpha mix with Card bg
    ///
    /// Focus is shown via a 1px inset stroke in the foreground colour
    /// — cleaner than the stock dotted FocusRectangle which clashes
    /// with rounded corners.
    /// </summary>
    internal class RoundedButton : Button
    {
        public int CornerRadius { get; set; } = 6;
        public Color HoverColor { get; set; }
        public Color PressedColor { get; set; }

        private bool _hovered;
        private bool _pressed;

        public RoundedButton()
        {
            // ControlStyles.UserPaint kicks the framework off our paint
            // path so we own pixel one to N. AllPaintingInWmPaint +
            // OptimizedDoubleBuffer prevents the flicker the stock
            // background eraser would cause.
            SetStyle(
                ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor,
                true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
        }

        /// <summary>
        /// Fill the entire control rectangle with the PARENT's
        /// background color before OnPaint draws the rounded fill on
        /// top. Without this, the four triangular pixels outside the
        /// rounded path retain whatever the back-buffer was cleared
        /// to — for .NET Framework 4.8 with UserPaint+
        /// AllPaintingInWmPaint, that's the control's own BackColor —
        /// which paints visible square corners around the rounded
        /// fill and defeats the rounding effect entirely. Painting
        /// the parent BackColor first means the corner triangles
        /// blend seamlessly into the card behind us.
        /// </summary>
        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            var parentBg = Parent?.BackColor ?? BackColor;
            using (var brush = new SolidBrush(parentBg))
            {
                pevent.Graphics.FillRectangle(brush, ClientRectangle);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }
        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (mevent.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }
            base.OnMouseDown(mevent);
        }
        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // Pick the fill colour for the current state. Disabled
            // gets a 60% blend with the parent's background to read
            // visually as "muted but still a button". Falls back to
            // a tasteful default if the parent has no BackColor we
            // can blend with (e.g. transparent ancestor).
            Color fill;
            if (!Enabled)
            {
                var parentBg = Parent?.BackColor ?? Color.FromArgb(38, 42, 51);
                fill = BlendColors(BackColor, parentBg, 0.55f);
            }
            else if (_pressed)
            {
                fill = PressedColor.IsEmpty ? BackColor : PressedColor;
            }
            else if (_hovered)
            {
                fill = HoverColor.IsEmpty ? BackColor : HoverColor;
            }
            else
            {
                fill = BackColor;
            }

            // Use the full client rectangle — no 0.5px inset. AntiAlias
            // handles the edge by alpha-blending corner/edge pixels
            // with whatever's underneath, which is the parent's
            // BackColor we already painted in OnPaintBackground. An
            // earlier 0.5px inset attempt left a visible 1px parent-
            // colour halo around every button.
            var rect = new RectangleF(0, 0, Width, Height);
            using (var path = BuildRoundedPath(rect, CornerRadius))
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
                if (Focused)
                {
                    using (var pen = new Pen(ForeColor, 1f) { Alignment = PenAlignment.Inset })
                    {
                        var focusRect = RectangleF.Inflate(rect, -3f, -3f);
                        using (var focusPath = BuildRoundedPath(focusRect, Math.Max(0, CornerRadius - 2)))
                        {
                            g.DrawPath(pen, focusPath);
                        }
                    }
                }
            }

            // Centred text. TextRenderer (GDI) gives Uniscribe fallback
            // for any Unicode glyphs in the label — keeps lock 🔒 or ●
            // glyphs from rendering as boxes inside a styled button.
            var fg = Enabled ? ForeColor : BlendColors(ForeColor, fill, 0.5f);
            TextRenderer.DrawText(
                g, Text, Font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.EndEllipsis);
        }

        /// <summary>
        /// Build a closed rounded-rectangle path. Degenerates to a
        /// regular rectangle when the corner radius is 0 or larger
        /// than half the smallest side (avoids the GDI+ assertion
        /// AddArc throws on negative sweep angles).
        /// </summary>
        private static GraphicsPath BuildRoundedPath(RectangleF rect, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0 || rect.Width <= 0 || rect.Height <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            // Clamp so corners don't overlap on a tiny button.
            float r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f);
            float d = r * 2f;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// Linear-blend two ARGB colours. Used for disabled-state
        /// fades — gentler than a flat grey override.
        /// </summary>
        private static Color BlendColors(Color a, Color b, float ratio)
        {
            ratio = Math.Max(0f, Math.Min(1f, ratio));
            return Color.FromArgb(
                (int)(a.A * (1 - ratio) + b.A * ratio),
                (int)(a.R * (1 - ratio) + b.R * ratio),
                (int)(a.G * (1 - ratio) + b.G * ratio),
                (int)(a.B * (1 - ratio) + b.B * ratio));
        }

        /// <summary>
        /// Helper for SettingsPanel's card-border paint handler:
        /// renders a rounded rectangle stroke at the given radius.
        /// Lives here so the corner-rounding logic isn't duplicated.
        /// </summary>
        public static void PaintRoundedCardBorder(Graphics g, Rectangle r, Color borderColor, int radius)
        {
            var prevSmooth = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(0.5f, 0.5f, r.Width - 1f, r.Height - 1f);
            using (var path = BuildRoundedPath(rect, radius))
            using (var pen = new Pen(borderColor, 1f))
            {
                g.DrawPath(pen, path);
            }
            g.SmoothingMode = prevSmooth;
        }
    }
}
