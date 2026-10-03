using System.Drawing.Drawing2D;

namespace StartupManager;

sealed partial class ManagerForm
{
    // Cards fill the available height and share width above their minimum. When
    // more groups fit than the window allows, only the sequence scrolls sideways.
    sealed class ResponsiveBoard : Panel
    {
        readonly BoardScrollThumb scrollbar;
        bool arranging;
        int offset, contentWidth;
        public IEnumerable<RoundedPanel> Cards => Controls.OfType<RoundedPanel>();
        public int MaxOffset => Math.Max(0, contentWidth - ClientSize.Width);
        public int ContentWidth => contentWidth;
        public int ScrollOffset { get => offset; set { offset = Math.Clamp(value, 0, MaxOffset); PerformLayout(); scrollbar.Invalidate(); } }
        public ResponsiveBoard()
        {
            DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true);
            scrollbar = new BoardScrollThumb(this) { AccessibleName = "Scroll startup groups", AccessibleRole = AccessibleRole.ScrollBar, TabStop = true };
            Controls.Add(scrollbar);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if (arranging || scrollbar == null) return;
            arranging = true;
            try
            {
                var cards = Cards.ToArray();
                if (cards.Length == 0) return;
                float scale = DeviceDpi / 96f;
                int gap = (int)(18 * scale), minimum = (int)(332 * scale), addWidth = (int)(120 * scale);
                int groups = cards.Length - 1;
                int available = Math.Max(1, ClientSize.Width - Padding.Horizontal);
                int width = groups == 0 ? available : Math.Max(minimum, (available - addWidth - groups * gap) / groups);
                contentWidth = groups == 0 ? ClientSize.Width : groups * (width + gap) + addWidth + Padding.Horizontal;
                bool scrolls = contentWidth > ClientSize.Width;
                int trackHeight = scrolls ? (int)(18 * scale) : 0;
                int height = Math.Max(1, ClientSize.Height - Padding.Vertical - trackHeight);
                offset = Math.Clamp(offset, 0, MaxOffset);
                int x = Padding.Left - offset;
                for (int i = 0; i < cards.Length; i++)
                {
                    int cardWidth = groups == 0 ? available : i == groups ? addWidth : width;
                    cards[i].SetBounds(x, Padding.Top, cardWidth, height); x += cardWidth + gap;
                }
                scrollbar.Visible = scrolls; scrollbar.SetBounds(Padding.Left, ClientSize.Height - trackHeight, available, trackHeight); scrollbar.BringToFront();
                Invalidate(); scrollbar.Invalidate();
            }
            finally { arranging = false; }
        }
        protected override void OnMouseWheel(MouseEventArgs e) { ScrollOffset -= e.Delta; if (e is HandledMouseEventArgs handled) handled.Handled = true; }
    }

    sealed class BoardScrollThumb(ResponsiveBoard owner) : Control
    {
        bool dragging, hovered;
        int startX, startOffset;
        Rectangle Thumb
        {
            get
            {
                int width = Math.Min(Width, Math.Max(40, (int)((long)Width * owner.ClientSize.Width / Math.Max(1, owner.ContentWidth))));
                int left = owner.MaxOffset == 0 ? 0 : (int)((long)(Width - width) * owner.ScrollOffset / owner.MaxOffset);
                return new Rectangle(left, Math.Max(0, (Height - 6) / 2), width, 6);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(CanvasColor); if (Width < 3 || Height < 3 || owner.MaxOffset == 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedShape(Thumb, 3); using var brush = new SolidBrush(dragging || hovered || Focused ? MutedColor : BorderColor);
            e.Graphics.FillPath(brush, path);
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return; Focus();
            if (Thumb.Contains(e.Location)) { dragging = true; startX = e.X; startOffset = owner.ScrollOffset; Capture = true; }
            else owner.ScrollOffset += e.X < Thumb.Left ? -owner.ClientSize.Width : owner.ClientSize.Width;
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) owner.ScrollOffset = startOffset + (int)((long)(e.X - startX) * owner.MaxOffset / Math.Max(1, Width - Thumb.Width)); }
        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Capture = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { owner.ScrollOffset -= e.Delta; if (e is HandledMouseEventArgs handled) handled.Handled = true; }
        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            int? next = e.KeyCode switch { Keys.Left => owner.ScrollOffset - 60, Keys.Right => owner.ScrollOffset + 60, Keys.Home => 0, Keys.End => owner.MaxOffset, _ => null };
            if (next.HasValue) { owner.ScrollOffset = next.Value; e.Handled = true; } base.OnKeyDown(e);
        }
    }
}
