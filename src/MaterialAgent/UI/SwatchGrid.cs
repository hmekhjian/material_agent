using System;
using System.Collections.Generic;
using Eto.Drawing;
using Eto.Forms;

namespace MaterialAgent.UI
{
    /// <summary>
    /// A wrapping grid of colour swatches drawn in one control (fast even for ~2000 colours).
    /// Click selects; hovering reports the colour under the mouse.
    /// </summary>
    public sealed class SwatchGrid<T> : Drawable where T : class
    {
        public int CellSize { get; set; } = 24;
        public int Gap { get; set; } = 3;

        readonly Func<T, Color> _color;
        readonly Func<T, string> _label;
        IReadOnlyList<T> _items = Array.Empty<T>();
        T _selected;

        public event Action<T> Picked;
        public event Action<T> Hovered;

        public SwatchGrid(Func<T, Color> color, Func<T, string> label)
        {
            _color = color;
            _label = label;
            Paint += OnPaintGrid;
            MouseDown += (s, e) => { var item = HitTest(e.Location); if (item != null) { Select(item); Picked?.Invoke(item); } };
            MouseMove += (s, e) =>
            {
                var item = HitTest(e.Location);
                ToolTip = item == null ? null : _label(item);
                Hovered?.Invoke(item);
            };
            SizeChanged += (s, e) => UpdateHeight();
        }

        public IReadOnlyList<T> Items
        {
            get => _items;
            set { _items = value ?? Array.Empty<T>(); UpdateHeight(); Invalidate(); }
        }

        public T Selected => _selected;

        public void Select(T item)
        {
            _selected = item;
            Invalidate();
        }

        /// <summary>Top of the selected swatch in control coordinates, for scrolling it into view.</summary>
        public int SelectedTop
        {
            get
            {
                int i = IndexOf(_selected);
                return i < 0 ? 0 : (i / Columns) * (CellSize + Gap);
            }
        }

        int Columns => Math.Max(1, (Width - Gap) / (CellSize + Gap));

        void UpdateHeight()
        {
            if (Width <= 0) return;
            int rows = (_items.Count + Columns - 1) / Columns;
            int h = Math.Max(CellSize + 2 * Gap, rows * (CellSize + Gap) + Gap);
            if (Height != h) Height = h;
        }

        int IndexOf(T item)
        {
            if (item == null) return -1;
            for (int i = 0; i < _items.Count; i++) if (ReferenceEquals(_items[i], item)) return i;
            return -1;
        }

        T HitTest(PointF p)
        {
            int col = (int)((p.X - Gap) / (CellSize + Gap)), row = (int)((p.Y - Gap) / (CellSize + Gap));
            if (col < 0 || col >= Columns || row < 0) return null;
            int i = row * Columns + col;
            return i < _items.Count ? _items[i] : null;
        }

        void OnPaintGrid(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int cols = Columns;
            for (int i = 0; i < _items.Count; i++)
            {
                float x = Gap + (i % cols) * (CellSize + Gap), y = Gap + (i / cols) * (CellSize + Gap);
                if (y > e.ClipRectangle.Bottom || y + CellSize < e.ClipRectangle.Top) continue;
                g.FillRectangle(_color(_items[i]), x, y, CellSize, CellSize);
                g.DrawRectangle(Color.FromArgb(0, 0, 0, 40), x, y, CellSize - 1, CellSize - 1);
                if (ReferenceEquals(_items[i], _selected))
                {
                    g.DrawRectangle(Colors.Black, x - 2, y - 2, CellSize + 3, CellSize + 3);
                    g.DrawRectangle(Colors.White, x - 1, y - 1, CellSize + 1, CellSize + 1);
                }
            }
        }
    }
}
