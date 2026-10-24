using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TaskbarMonitor.Usage
{
    /// <summary>
    /// Small topmost, non-activating window used for per-counter process
    /// details. It never becomes the foreground window and is positioned next
    /// to the counter on the monitor that owns the taskbar.
    /// </summary>
    public sealed class ProcessUsagePopup : Form
    {
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExTopMost = 0x00000008;
        private const int CsDropShadow = 0x00020000;

        private ProcessUsageSnapshot snapshot;
        private GraphTheme theme;
        private float scale = 1f;
        private float fontScale = 0f;

        private Font titleFont;
        private Font rowFont;
        private Font valueFont;

        private int padding;
        private int titleHeight;
        private int rowHeight;

        public ProcessUsagePopup()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint, true);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false;
            ShowIcon = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(32, 32, 32);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                System.Windows.Forms.CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTopMost;
                cp.ClassStyle |= CsDropShadow;
                return cp;
            }
        }

        public void ShowSnapshot(ProcessUsageSnapshot snapshot, Rectangle anchorScreen,
            float displayScale, GraphTheme theme)
        {
            this.snapshot = snapshot;
            this.theme = theme;
            this.scale = Math.Max(0.75f, displayScale);
            Text = snapshot != null ? snapshot.Title : "Process usage";
            EnsureFonts();
            UpdateSize();
            Position(anchorScreen);

            if (!Visible)
                Show();

            Invalidate();
        }

        private void EnsureFonts()
        {
            if (Math.Abs(fontScale - scale) < 0.001f && titleFont != null)
                return;

            DisposeFonts();

            fontScale = scale;
            float titlePixels = Math.Max(9f, 13f * scale);
            float rowPixels = Math.Max(8f, 12f * scale);

            titleFont = new Font("Segoe UI", titlePixels, FontStyle.Bold, GraphicsUnit.Pixel);
            rowFont = new Font("Segoe UI", rowPixels, FontStyle.Regular, GraphicsUnit.Pixel);
            valueFont = new Font("Segoe UI", rowPixels, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        private void UpdateSize()
        {
            padding = Math.Max(6, (int)Math.Round(9 * scale));
            titleHeight = Math.Max(16, (int)Math.Round(20 * scale));
            rowHeight = Math.Max(16, (int)Math.Round(19 * scale));

            int rowCount = snapshot != null && snapshot.Rows.Count > 0
                ? snapshot.Rows.Count
                : 1;

            string title = snapshot != null ? snapshot.Title : string.Empty;
            int titleWidth = MeasureText(title, titleFont);
            int nameWidth = 0;
            int valueWidth = 0;

            if (snapshot != null)
            {
                foreach (var row in snapshot.Rows)
                {
                    nameWidth = Math.Max(nameWidth, MeasureText(row.Name, rowFont));
                    valueWidth = Math.Max(valueWidth, MeasureText(row.Value, valueFont));
                }
            }

            int minimumValueWidth = (int)Math.Round(60 * scale);
            valueWidth = Math.Max(valueWidth, minimumValueWidth);
            int rowGap = Math.Max(8, (int)Math.Round(14 * scale));
            int rowWidth = nameWidth + valueWidth + rowGap;
            int contentWidth = Math.Max(titleWidth, rowWidth);
            int minWidth = (int)Math.Round(230 * scale);
            int maxWidth = (int)Math.Round(430 * scale);
            int width = Math.Max(minWidth, Math.Min(maxWidth, contentWidth + padding * 2 + 2));
            int height = padding * 2 + titleHeight + 1 + rowCount * rowHeight;

            if (ClientSize.Width != width || ClientSize.Height != height)
                ClientSize = new Size(width, height);

            UpdateRegion();
        }

        private static int MeasureText(string text, Font font)
        {
            if (string.IsNullOrEmpty(text) || font == null)
                return 0;

            return TextRenderer.MeasureText(text, font, new Size(10000, 1000),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPrefix).Width;
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0)
                return;

            int radius = Math.Max(0, (int)Math.Round(6 * scale));
            using (GraphicsPath path = CreateRoundRect(new Rectangle(0, 0, Width, Height), radius))
            {
                System.Drawing.Region oldRegion = Region;
                Region = new System.Drawing.Region(path);
                if (oldRegion != null)
                    oldRegion.Dispose();
            }
        }

        private void Position(Rectangle anchorScreen)
        {
            Screen screen = anchorScreen.Width > 0 && anchorScreen.Height > 0
                ? Screen.FromRectangle(anchorScreen)
                : Screen.PrimaryScreen;
            if (screen == null)
                return;

            Location = CalculatePosition(screen.Bounds, screen.WorkingArea,
                anchorScreen, Size, scale);
        }

        /// <summary>
        /// Calculates the popup position for a screen, working area and counter
        /// rectangle. Keeping this independent makes the placement testable
        /// for top, bottom and side taskbars.
        /// </summary>
        public static Point CalculatePosition(Rectangle bounds, Rectangle work,
            Rectangle anchorScreen, Size popupSize, float scale)
        {
            int width = popupSize.Width;
            int height = popupSize.Height;
            float layoutScale = Math.Max(0.5f, scale);
            int gap = Math.Max(2, (int)Math.Round(6 * layoutScale));
            int x;
            int y;

            int diffWidth = Math.Max(0, bounds.Width - work.Width);
            int diffHeight = Math.Max(0, bounds.Height - work.Height);
            int edgeMargin = Math.Max(16, (int)Math.Round(50 * layoutScale));

            bool nearLeft = anchorScreen.Left <= bounds.Left + edgeMargin;
            bool nearRight = anchorScreen.Right >= bounds.Right - edgeMargin;
            bool nearTop = anchorScreen.Top <= bounds.Top + edgeMargin;
            bool nearBottom = anchorScreen.Bottom >= bounds.Bottom - edgeMargin;
            bool verticalTaskbar;

            if (diffWidth > diffHeight && diffWidth > 0)
                verticalTaskbar = true;
            else if (diffHeight > diffWidth && diffHeight > 0)
                verticalTaskbar = false;
            else if ((nearLeft || nearRight) && !(nearTop || nearBottom))
                verticalTaskbar = true;
            else if ((nearTop || nearBottom) && !(nearLeft || nearRight))
                verticalTaskbar = false;
            else
                verticalTaskbar = anchorScreen.Height > anchorScreen.Width;

            if (verticalTaskbar)
            {
                // Left or right taskbar: put the popup beside the taskbar.
                if (work.Left > bounds.Left)
                    x = work.Left + gap;
                else if (work.Right < bounds.Right)
                    x = work.Right - width - gap;
                else if (nearRight || anchorScreen.Left > bounds.Left + bounds.Width / 2)
                    x = anchorScreen.Left - width - gap;
                else
                    x = anchorScreen.Right + gap;

                y = anchorScreen.Top + (anchorScreen.Height - height) / 2;
            }
            else
            {
                x = anchorScreen.Left + (anchorScreen.Width - width) / 2;

                bool taskbarAtTop = nearTop;
                int above = anchorScreen.Top - height - gap;
                int below = anchorScreen.Bottom + gap;

                if (taskbarAtTop)
                    y = below;
                else if (above >= work.Top + gap)
                    y = above;
                else
                    y = below;
            }

            x = Math.Max(work.Left + gap, Math.Min(x, work.Right - width - gap));
            y = Math.Max(work.Top + gap, Math.Min(y, work.Bottom - height - gap));
            return new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (titleFont == null || rowFont == null || valueFont == null)
                return;

            bool light = IsLightTheme();
            Color background = light
                ? Color.FromArgb(250, 250, 250)
                : Color.FromArgb(32, 32, 32);
            Color border = light
                ? Color.FromArgb(80, 30, 30, 30)
                : Color.FromArgb(130, 255, 255, 255);
            Color foreground = light
                ? Color.FromArgb(28, 28, 28)
                : Color.FromArgb(245, 245, 245);
            Color titleColor = light
                ? Color.FromArgb(20, 20, 20)
                : Color.FromArgb(255, 255, 255);
            Color valueColor = light
                ? Color.FromArgb(0, 102, 184)
                : Color.FromArgb(125, 205, 255);
            Color muted = light
                ? Color.FromArgb(95, 95, 95)
                : Color.FromArgb(175, 175, 175);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int radius = Math.Max(0, (int)Math.Round(6 * scale));

            using (GraphicsPath backgroundPath = CreateRoundRect(
                new Rectangle(0, 0, Width, Height), radius))
            using (SolidBrush backgroundBrush = new SolidBrush(background))
            {
                e.Graphics.FillPath(backgroundBrush, backgroundPath);
            }

            using (GraphicsPath borderPath = CreateRoundRect(
                new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), radius))
            using (Pen borderPen = new Pen(border))
            {
                e.Graphics.DrawPath(borderPen, borderPath);
            }

            Rectangle titleRect = new Rectangle(padding, padding,
                Math.Max(1, Width - padding * 2), titleHeight);
            TextRenderer.DrawText(e.Graphics,
                snapshot != null ? snapshot.Title : string.Empty,
                titleFont, titleRect, titleColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            int separatorY = padding + titleHeight;
            using (Pen separatorPen = new Pen(border))
            {
                e.Graphics.DrawLine(separatorPen, padding, separatorY,
                    Math.Max(padding, Width - padding), separatorY);
            }

            int rowY = separatorY + 1;
            if (snapshot == null || snapshot.Rows.Count == 0)
            {
                string text = snapshot != null && !snapshot.IsReady
                    ? "Sampling..."
                    : "No data";
                Rectangle emptyRect = new Rectangle(padding, rowY,
                    Math.Max(1, Width - padding * 2), rowHeight);
                TextRenderer.DrawText(e.Graphics, text, rowFont, emptyRect, muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                    | TextFormatFlags.NoPadding);
                return;
            }

            int valueWidth = 0;
            foreach (var row in snapshot.Rows)
                valueWidth = Math.Max(valueWidth, MeasureText(row.Value, valueFont));
            valueWidth = Math.Max(valueWidth, (int)Math.Round(60 * scale));

            int contentWidth = Math.Max(1, Width - padding * 2);
            int valueGap = Math.Max(8, (int)Math.Round(14 * scale));
            int nameWidth = Math.Max(1, contentWidth - valueWidth - valueGap);

            foreach (var row in snapshot.Rows)
            {
                Rectangle nameRect = new Rectangle(padding, rowY, nameWidth, rowHeight);
                Rectangle valueRect = new Rectangle(padding, rowY, contentWidth, rowHeight);

                TextRenderer.DrawText(e.Graphics, row.Name, rowFont, nameRect, foreground,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine
                    | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

                // Right-align against the full content width. This is more
                // robust than sizing a separate rectangle from a measured
                // string, especially for clear-type overhang at the edge.
                TextRenderer.DrawText(e.Graphics, row.Value, valueFont, valueRect, valueColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                    | TextFormatFlags.NoPadding);

                rowY += rowHeight;
            }
        }

        private bool IsLightTheme()
        {
            if (theme == null)
                return false;

            return theme.TextColor.GetBrightness() < 0.5f;
        }

        private static GraphicsPath CreateRoundRect(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0)
                return path;

            radius = Math.Max(0, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));
            if (radius == 0)
            {
                path.AddRectangle(rect);
                return path;
            }

            int diameter = radius * 2;
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void DisposeFonts()
        {
            if (titleFont != null)
            {
                titleFont.Dispose();
                titleFont = null;
            }
            if (rowFont != null)
            {
                rowFont.Dispose();
                rowFont = null;
            }
            if (valueFont != null)
            {
                valueFont.Dispose();
                valueFont = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                DisposeFonts();

            base.Dispose(disposing);
        }
    }
}
