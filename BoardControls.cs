using System.Drawing.Drawing2D;

namespace StartupManager;

sealed partial class ManagerForm
{
    static GraphicsPath RoundedShape(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }

    // Retain native Button semantics (keyboard, AcceptButton and accessibility),
    // while painting the same rounded shape as the rest of the board.
    sealed class RoundedButton : Button
    {
        bool hovered, pressed;
        public bool DrawPlus { get; set; }
        public bool DrawEllipsis { get; set; }
        public RoundedButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? SurfaceColor);
            if (Width < 3 || Height < 3) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = DeviceDpi / 96f;
            var bounds = new RectangleF(1, 1, Width - 3, Height - 3);
            using var shape = RoundedShape(bounds, 8 * scale);
            Color fill = !Enabled ? TileColor : pressed ? BorderColor : hovered ? FlatAppearance.MouseOverBackColor : BackColor;
            using var brush = new SolidBrush(fill); e.Graphics.FillPath(brush, shape);
            if (FlatAppearance.BorderSize > 0 || (Focused && ShowFocusCues))
            {
                using var pen = new Pen(Focused && ShowFocusCues ? AccentColor : FlatAppearance.BorderColor, scale);
                e.Graphics.DrawPath(pen, shape);
            }
            if (DrawPlus)
            {
                using var pen = new Pen(Enabled ? ForeColor : MutedColor, 2 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                float centerX = Width / 2f, centerY = Height / 2f, half = 7 * scale;
                e.Graphics.DrawLine(pen, centerX - half, centerY, centerX + half, centerY);
                e.Graphics.DrawLine(pen, centerX, centerY - half, centerX, centerY + half);
            }
            else if (DrawEllipsis)
            {
                using var dot = new SolidBrush(Enabled ? ForeColor : MutedColor);
                float radius = 1.5f * scale;
                for (int i = -1; i <= 1; i++) e.Graphics.FillEllipse(dot, Width / 2f + i * 5 * scale - radius, Height / 2f - radius, radius * 2, radius * 2);
            }
            else TextRenderer.DrawText(e.Graphics, Text, Font, Rectangle.Round(bounds), Enabled ? ForeColor : MutedColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    sealed class AppIconTile : Control
    {
        public Image? IconImage { get; set; }
        public bool Dimmed { get; init; }
        public Button MenuButton { get; }
        bool hovered;
        public AppIconTile()
        {
            Size = new Size(90, 82); Margin = new Padding(0, 0, 4, 8); TabStop = true; Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            MenuButton = ActionButton("⋯", () => { }); MenuButton.AutoSize = false; MenuButton.MinimumSize = Size.Empty;
            MenuButton.SetBounds(62, 3, 25, 23); MenuButton.Padding = Padding.Empty; MenuButton.Margin = Padding.Empty; MenuButton.Visible = false;
            MenuButton.AccessibleName = "App options";
            ((RoundedButton)MenuButton).DrawEllipsis = true;
            MenuButton.MouseLeave += (_, _) => UpdateHover(); MenuButton.LostFocus += (_, _) => UpdateHover();
            Controls.Add(MenuButton);
        }
        void UpdateHover()
        {
            hovered = RectangleToScreen(ClientRectangle).Contains(Cursor.Position);
            MenuButton.Visible = hovered || ContainsFocus;
            Invalidate();
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); UpdateHover(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); UpdateHover(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); MenuButton.Visible = true; Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); UpdateHover(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(SurfaceColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = DeviceDpi / 96f;
            var iconSurface = new RectangleF(3 * scale, 3 * scale, Width - 6 * scale, 70 * scale);
            using var path = RoundedShape(iconSurface, 8 * scale);
            if (ContainsFocus) { using var pen = new Pen(AccentColor); e.Graphics.DrawPath(pen, path); }
            var iconBounds = new Rectangle((Width - (int)(48 * scale)) / 2, (int)(17 * scale), (int)(48 * scale), (int)(48 * scale));
            if (IconImage != null)
            {
                using var attributes = new System.Drawing.Imaging.ImageAttributes();
                if (Dimmed) attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(new[]
                {
                    new[] { .299f, .299f, .299f, 0, 0 }, new[] { .587f, .587f, .587f, 0, 0 }, new[] { .114f, .114f, .114f, 0, 0 }, new[] { 0f, 0, 0, .35f, 0 }, new[] { 0f, 0, 0, 0, 1 }
                }));
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.DrawImage(IconImage, iconBounds, 0, 0, IconImage.Width, IconImage.Height, GraphicsUnit.Pixel, attributes);
            }
            else
            {
                string initials = string.Concat(Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(word => word[0])).ToUpperInvariant();
                using var font = new Font("Segoe UI", 16, FontStyle.Bold);
                TextRenderer.DrawText(e.Graphics, initials, font, iconBounds, Dimmed ? BorderColor : AccentColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    // A clipped app viewport with a narrow dark thumb instead of a native light scrollbar.
    sealed class AppList : Panel
    {
        readonly Panel content = new() { Margin = Padding.Empty };
        readonly AppScrollThumb scrollbar;
        bool arranging;
        int offset;
        public int MaxOffset => Math.Max(0, content.Height - ClientSize.Height);
        public int ContentHeight => content.Height;
        public int ScrollOffset
        {
            get => offset;
            set { offset = Math.Clamp(value, 0, MaxOffset); content.Top = -offset; scrollbar.Invalidate(); }
        }
        public AppList()
        {
            DoubleBuffered = true; BackColor = SurfaceColor;
            scrollbar = new AppScrollThumb(this) { Width = 12, Dock = DockStyle.Right, AccessibleName = "Scroll apps", AccessibleRole = AccessibleRole.ScrollBar, TabStop = true };
            Controls.Add(content); Controls.Add(scrollbar);
        }
        public void AddItem(Control item)
        {
            void WireWheel(Control control) { control.MouseWheel += (_, e) => { ScrollOffset -= e.Delta; if (e is HandledMouseEventArgs handled) handled.Handled = true; }; foreach (Control child in control.Controls) WireWheel(child); }
            WireWheel(item); content.Controls.Add(item); PerformLayout();
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (arranging || scrollbar == null) return;
            arranging = true;
            try
            {
                float scale = DeviceDpi / 96f;
                int width = Math.Max(1, ClientSize.Width - scrollbar.Width), cellWidth = (int)(94 * scale), cellHeight = (int)(90 * scale), top = (int)(8 * scale);
                int columns = Math.Max(1, width / cellWidth), index = 0, height = top;
                foreach (Control item in content.Controls)
                {
                    if (item is AppIconTile)
                    {
                        item.SetBounds(index % columns * cellWidth, top + index / columns * cellHeight, (int)(90 * scale), (int)(82 * scale));
                        index++; height = top + ((index + columns - 1) / columns) * cellHeight;
                    }
                    else { item.Location = new Point(6, top + 16); height = item.Bottom + 8; }
                }
                content.SetBounds(0, -offset, width, height);
                scrollbar.Visible = MaxOffset > 0; ScrollOffset = offset;
            }
            finally { arranging = false; }
        }
        protected override void OnMouseWheel(MouseEventArgs e) { ScrollOffset -= e.Delta; if (e is HandledMouseEventArgs handled) handled.Handled = true; }
    }

    sealed class AppScrollThumb(AppList owner) : Control
    {
        int dragStart, startingOffset;
        bool dragging, hovered;
        Rectangle Thumb
        {
            get
            {
                int thumbHeight = Math.Min(Height, Math.Max(32, (int)((long)Height * owner.ClientSize.Height / Math.Max(1, owner.ContentHeight))));
                int top = owner.MaxOffset == 0 ? 0 : (int)((long)(Height - thumbHeight) * owner.ScrollOffset / owner.MaxOffset);
                return new Rectangle(3, top, Math.Max(2, Width - 6), thumbHeight);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(SurfaceColor);
            if (Height < 3 || owner.MaxOffset == 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = RoundedShape(Thumb, 3);
            using var brush = new SolidBrush(dragging || hovered || Focused ? MutedColor : BorderColor);
            e.Graphics.FillPath(brush, shape);
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return;
            Focus();
            if (Thumb.Contains(e.Location)) { dragging = true; dragStart = e.Y; startingOffset = owner.ScrollOffset; Capture = true; }
            else owner.ScrollOffset += e.Y < Thumb.Top ? -owner.ClientSize.Height : owner.ClientSize.Height;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) owner.ScrollOffset = startingOffset + (int)((long)(e.Y - dragStart) * owner.MaxOffset / Math.Max(1, Height - Thumb.Height));
        }
        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Capture = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { owner.ScrollOffset -= e.Delta; if (e is HandledMouseEventArgs handled) handled.Handled = true; }
        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            int? next = e.KeyCode switch { Keys.Up => owner.ScrollOffset - 40, Keys.Down => owner.ScrollOffset + 40, Keys.PageUp => owner.ScrollOffset - owner.ClientSize.Height, Keys.PageDown => owner.ScrollOffset + owner.ClientSize.Height, Keys.Home => 0, Keys.End => owner.MaxOffset, _ => null };
            if (next.HasValue) { owner.ScrollOffset = next.Value; e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }
}
