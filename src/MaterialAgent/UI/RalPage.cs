using System;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using MaterialAgent.Core;
using MaterialAgent.Core.Colors;

namespace MaterialAgent.UI
{
    /// <summary>
    /// Built-in RAL colours: browse or filter the swatches, pick a finish, import. Offline and free.
    /// Typing "RAL 9010" in the Material tab's search box jumps here.
    /// </summary>
    public sealed class RalPage : Panel
    {
        readonly MaterialAgentPanel _host;
        readonly DropDown _system = new DropDown();
        readonly TextBox _filter = new TextBox { PlaceholderText = "Filter: code or name, e.g. 7016 or anthracite" };
        readonly SwatchGrid<RalColor> _grid = new SwatchGrid<RalColor>(c => Color.FromArgb(c.R, c.G, c.B), c => c.Display + "  " + c.Hex);
        readonly Scrollable _gridScroll;
        readonly Drawable _big = new Drawable { Height = 64 };
        readonly Label _name = new Label { Font = SystemFonts.Bold() };
        readonly Label _info = new Label { Wrap = WrapMode.Word, TextColor = Colors.Gray };
        readonly Label _hover = new Label { TextColor = Colors.Gray };
        readonly DropDown _finish = new DropDown();
        readonly Button _importButton = new Button { Text = "Import to selection", Enabled = false };
        readonly Button _importLayerButton = new Button { Text = "Import to layer…", Enabled = false };
        readonly Label _status = new Label { Wrap = WrapMode.Word };
        RalColor _selected;

        public RalPage(MaterialAgentPanel host)
        {
            _host = host;
            _system.Items.Add($"RAL Classic ({RalCatalog.Classic.Count})", "classic");
            _system.Items.Add($"RAL Design System ({RalCatalog.Design.Count})", "design");
            _system.SelectedKey = "classic";
            foreach (Finish f in Enum.GetValues(typeof(Finish)))
                _finish.Items.Add($"{f} (roughness {EnumText.Roughness(f):0.##})", f.ToString());
            _finish.SelectedKey = nameof(Finish.Satin);

            _gridScroll = new Scrollable { Content = _grid, Height = 260, Border = BorderType.Line, ExpandContentWidth = true };
            _big.Paint += (s, e) =>
            {
                if (_selected == null) return;
                e.Graphics.FillRectangle(Color.FromArgb(_selected.R, _selected.G, _selected.B), 0, 0, _big.Width, _big.Height);
            };

            _system.SelectedIndexChanged += (s, e) => Refill();
            _filter.TextChanged += (s, e) => Refill();
            _grid.Picked += c => SelectColor(c, scroll: false);
            _grid.Hovered += c => _hover.Text = c == null ? "" : $"{c.Display}  {c.Hex}";
            _importButton.Click += (s, e) => Import(toLayers: false);
            _importLayerButton.Click += (s, e) => Import(toLayers: true);

            var small = SystemFonts.Default(SystemFonts.Default().Size - 1);
            _info.Font = small; _hover.Font = small; _status.Font = small;

            var layout = new DynamicLayout { Padding = new Padding(6), DefaultSpacing = new Size(4, 4) };
            layout.AddRow(new TableLayout { Spacing = new Size(4, 0), Rows = { new TableRow(_system, new TableCell(_filter, true)) } });
            layout.AddRow(_gridScroll);
            layout.AddRow(_hover);
            layout.AddRow(_big);
            layout.AddRow(_name);
            layout.AddRow(_info);
            layout.AddRow(new TableLayout { Spacing = new Size(4, 0), Rows = { new TableRow(new Label { Text = "Finish", VerticalAlignment = VerticalAlignment.Center }, new TableCell(_finish, true)) } });
            layout.AddRow(new TableLayout { Spacing = new Size(4, 0), Rows = { new TableRow(new TableCell(_importButton, true), new TableCell(_importLayerButton, true)) } });
            layout.AddRow(_status);
            layout.Add(null);
            Content = layout;
            Refill();
        }

        /// <summary>
        /// Shows a colour found by the Material tab's search. For name searches ("RAL anthracite") the filter keeps
        /// the words, so the other matches stay visible next to the best one.
        /// </summary>
        public void Show(RalMatch match, string query)
        {
            var c = match.Color;
            _system.SelectedKey = c.System == RalSystem.Classic ? "classic" : "design";
            var words = (query ?? "").Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !w.Equals("ral", StringComparison.OrdinalIgnoreCase) && !w.Any(char.IsDigit));
            _filter.Text = match.Alternatives.Count > 0 ? string.Join(" ", words) : "";
            Refill();
            SelectColor(c, scroll: true);
        }

        void Refill()
        {
            var list = _system.SelectedKey == "design" ? RalCatalog.Design : RalCatalog.Classic;
            var f = _filter.Text?.Trim();
            if (!string.IsNullOrEmpty(f))
            {
                var digits = new string(f.Where(char.IsDigit).ToArray());
                var words = f.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(w => !w.Any(char.IsDigit)).ToArray();
                list = list.Where(c =>
                    (digits.Length == 0 || new string(c.Code.Where(char.IsDigit).ToArray()).Contains(digits)) &&
                    words.All(w => c.Name.ToLowerInvariant().Contains(w) || w == "ral")).ToList();
            }
            _grid.Items = list;
            _grid.Select(_selected);
        }

        void SelectColor(RalColor c, bool scroll)
        {
            _selected = c;
            _grid.Select(c);
            _big.Invalidate();
            _name.Text = c.Display;
            var info = $"{c.Hex} · built-in {(c.System == RalSystem.Classic ? "RAL Classic" : "RAL Design System")} colour. Screen approximation: check a physical RAL fan for colour-critical work.";
            if (c.Special == RalSpecial.Metallic) info += " Pearl/metallic: rendered with some metalness.";
            if (c.Special == RalSpecial.Luminous) info += " Fluorescent: real samples are brighter than any screen can show.";
            _info.Text = info;
            _finish.SelectedKey = c.System == RalSystem.Classic ? nameof(Finish.Satin) : nameof(Finish.Matt);
            _importButton.Enabled = _importLayerButton.Enabled = true;
            _status.Text = "";
            if (scroll) Application.Instance.AsyncInvoke(() => _gridScroll.ScrollPosition = new Point(0, Math.Max(0, _grid.SelectedTop - 60)));
        }

        void Import(bool toLayers)
        {
            if (_selected == null) return;
            var finish = Enum.TryParse(_finish.SelectedKey, out Finish f) ? f : Finish.Satin;
            var msg = _host.ImportColor(_selected, finish, toLayers, out bool ok);
            if (msg == null) return; // layer dialog cancelled
            _status.Text = msg;
            _status.TextColor = ok ? SystemColors.ControlText : Colors.Red;
        }
    }
}
