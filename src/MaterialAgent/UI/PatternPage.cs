using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using MaterialAgent.Core;
using MaterialAgent.Core.Agent;
using MaterialAgent.Core.Patterns;
using FillMode = MaterialAgent.Core.Patterns.FillMode;
using ImageFormat = MaterialAgent.Core.ImageFormat;
using Palette = MaterialAgent.Core.Patterns.Palette;

namespace MaterialAgent.UI
{
    /// <summary>
    /// Architextures-style pattern builder using a real product: brick bonds, tile and plank layouts.
    /// Units are filled with crops of the product texture at true scale, or with colours from the product photo;
    /// joint size, colour and depth are set here. The result has an exactly known size and goes to the
    /// Material tab for import.
    /// </summary>
    public sealed class PatternPage : Panel
    {
        readonly MaterialAgentPanel _host;

        // Source
        readonly ImageView _srcThumb = new ImageView { Size = new Size(56, 56) };
        readonly Label _srcLabel = new Label { Wrap = WrapMode.Word, Text = "No product image yet." };
        readonly Button _useMaterial = new Button { Text = "Use Material tab image", ToolTip = "Takes the texture currently selected in the Material tab, with its real-world size." };
        readonly Button _loadImage = new Button { Text = "Load image…" };
        readonly NumericStepper _srcWidth = new NumericStepper { MinValue = 0, MaxValue = 100000, DecimalPlaces = 0, Increment = 50, ToolTip = "Real width the product image covers, in mm (0 = unknown)." };
        readonly DropDown _fill = new DropDown();
        readonly Drawable _paletteStrip = new Drawable { Height = 22, ToolTip = "Colours from the product photo. Click one to leave it out (e.g. mortar or background)." };

        // Pattern
        readonly DropDown _pattern = new DropDown();
        readonly NumericStepper _length = Stepper(215);
        readonly NumericStepper _height = Stepper(65);
        readonly NumericStepper _depth = Stepper(102.5);
        readonly NumericStepper _joint = new NumericStepper { MinValue = 0, MaxValue = 50, DecimalPlaces = 1, Value = 10, Increment = 1 };
        readonly DropDown _jointPreset = new DropDown();
        readonly ColorPicker _jointColor = new ColorPicker { Value = Color.FromArgb(190, 184, 172) };
        readonly DropDown _jointDepth = new DropDown();
        readonly Slider _variation = new Slider { MinValue = 0, MaxValue = 100, Value = 35, ToolTip = "Unit-to-unit variation" };
        readonly Button _shuffle = new Button { Text = "Shuffle", ToolTip = "A different random arrangement" };
        readonly CheckBox _grainVertical = new CheckBox { Text = "Grain runs vertically in the image" };

        // Output
        readonly ImageView _preview = new ImageView { Size = new Size(-1, 200) };
        readonly Label _info = new Label { Wrap = WrapMode.Word, TextColor = Colors.Gray };
        readonly Button _send = new Button { Text = "Use this texture →", Enabled = false, ToolTip = "Sends the pattern to the Material tab (exact real size, with its normal and roughness maps) for import." };
        readonly UITimer _debounce = new UITimer { Interval = 0.35 };

        byte[] _srcBytes;
        string _srcUrl = "";
        string _productName = "";
        List<PaletteColor> _palette = new List<PaletteColor>();
        readonly HashSet<int> _paletteOff = new HashSet<int>();
        int _seed = 1, _renderVersion;

        static readonly (string name, byte r, byte g, byte b)[] Joints =
        {
            ("Natural grey", 190, 184, 172), ("Light grey", 205, 205, 200), ("White", 235, 233, 226),
            ("Buff", 205, 180, 135), ("Dark grey", 95, 95, 92), ("Charcoal", 55, 55, 55), ("Black", 25, 25, 25),
        };

        public PatternPage(MaterialAgentPanel host)
        {
            _host = host;
            _fill.Items.Add("Product texture (planks, tiles, stone)", nameof(FillMode.Texture));
            _fill.Items.Add("Colours from the photo (bricks)", nameof(FillMode.Palette));
            _fill.SelectedKey = nameof(FillMode.Palette);
            foreach (PatternKind k in Enum.GetValues(typeof(PatternKind))) _pattern.Items.Add(Describe(k), k.ToString());
            _pattern.SelectedKey = nameof(PatternKind.Stretcher);
            foreach (var j in Joints) _jointPreset.Items.Add(j.name);
            _jointPreset.Items.Add("Custom");
            _jointPreset.SelectedIndex = 0;
            _jointDepth.Items.Add("Flush joints", "0");
            _jointDepth.Items.Add("Recessed joints (4 mm)", "4");
            _jointDepth.Items.Add("Deep joints (8 mm)", "8");
            _jointDepth.SelectedKey = "4";

            _paletteStrip.Paint += PaintPalette;
            _paletteStrip.MouseDown += (s, e) =>
            {
                int i = (int)(e.Location.X / 26);
                if (i < 0 || i >= _palette.Count) return;
                if (!_paletteOff.Remove(i) && _paletteOff.Count < _palette.Count - 1) _paletteOff.Add(i);
                _paletteStrip.Invalidate();
                Queue();
            };

            _useMaterial.Click += (s, e) => UseMaterialImage();
            _loadImage.Click += (s, e) => LoadImage();
            _shuffle.Click += (s, e) => { _seed++; Queue(); };
            _send.Click += async (s, e) => await SendAsync();
            _jointPreset.SelectedIndexChanged += (s, e) =>
            {
                int i = _jointPreset.SelectedIndex;
                if (i >= 0 && i < Joints.Length) _jointColor.Value = Color.FromArgb(Joints[i].r, Joints[i].g, Joints[i].b);
            };
            _jointColor.ValueChanged += (s, e) => Queue();
            foreach (var c in new Control[] { _fill, _pattern, _jointDepth })
                ((DropDown)c).SelectedIndexChanged += (s, e) => { UpdateEnabled(); Queue(); };
            foreach (var n in new[] { _length, _height, _depth, _joint, _srcWidth }) n.ValueChanged += (s, e) => Queue();
            _variation.ValueChanged += (s, e) => Queue();
            _grainVertical.CheckedChanged += (s, e) => Queue();
            _debounce.Elapsed += async (s, e) => { _debounce.Stop(); await RenderPreviewAsync(); };

            var small = SystemFonts.Default(SystemFonts.Default().Size - 1);
            _srcLabel.Font = small; _info.Font = small;

            var layout = new DynamicLayout { Padding = new Padding(6), DefaultSpacing = new Size(4, 4) };
            layout.AddRow(new TableLayout
            {
                Spacing = new Size(6, 0),
                Rows = { new TableRow(_srcThumb, new TableCell(new StackLayout { Spacing = 3, HorizontalContentAlignment = HorizontalAlignment.Stretch, Items = { _srcLabel, Row(_useMaterial, _loadImage) } }, true)) },
            });
            layout.AddRow(new TableLayout
            {
                Spacing = new Size(4, 3),
                Rows =
                {
                    new TableRow(Caption("Image width"), new TableCell(_srcWidth, true), Caption("mm")),
                },
            });
            layout.AddRow(new TableLayout { Spacing = new Size(4, 0), Rows = { new TableRow(Caption("Fill"), new TableCell(_fill, true)) } });
            layout.AddRow(_paletteStrip);
            layout.AddRow(new TableLayout { Spacing = new Size(4, 0), Rows = { new TableRow(Caption("Pattern"), new TableCell(_pattern, true)) } });
            layout.AddRow(new TableLayout
            {
                Spacing = new Size(4, 3),
                Rows =
                {
                    new TableRow(Caption("Unit L"), new TableCell(_length, true), Caption("H"), new TableCell(_height, true), Caption("D"), new TableCell(_depth, true)),
                },
            });
            layout.AddRow(new TableLayout
            {
                Spacing = new Size(4, 3),
                Rows =
                {
                    new TableRow(Caption("Joint"), new TableCell(_joint, true), Caption("mm"), new TableCell(_jointPreset, true), _jointColor),
                    new TableRow(Caption(""), new TableCell(_jointDepth, true), Caption(""), new TableCell(_grainVertical, true), Caption("")),
                },
            });
            layout.AddRow(new TableLayout { Spacing = new Size(4, 0), Rows = { new TableRow(Caption("Variation"), new TableCell(_variation, true), _shuffle) } });
            layout.AddRow(_preview);
            layout.AddRow(_info);
            layout.AddRow(_send);
            layout.Add(null);
            Content = layout;
            UpdateEnabled();
        }

        static NumericStepper Stepper(double v) => new NumericStepper { MinValue = 1, MaxValue = 10000, DecimalPlaces = 1, Value = v, Increment = 5 };
        static Label Caption(string t) => new Label { Text = t, VerticalAlignment = VerticalAlignment.Center };
        static TableLayout Row(params Control[] c) => new TableLayout { Spacing = new Size(4, 0), Rows = { new TableRow(c.Select(x => new TableCell(x)).Concat(new[] { new TableCell(null, true) }).ToArray()) } };

        static string Describe(PatternKind k) => k switch
        {
            PatternKind.Stretcher => "Stretcher bond (½ offset)",
            PatternKind.ThirdBond => "Third bond (⅓ offset)",
            PatternKind.QuarterBond => "Quarter bond (¼ offset)",
            PatternKind.Stack => "Stack bond / grid",
            PatternKind.Flemish => "Flemish bond",
            PatternKind.English => "English bond",
            PatternKind.Header => "Header bond",
            PatternKind.Basketweave => "Basketweave",
            PatternKind.Herringbone => "Herringbone",
            _ => k.ToString(),
        };

        bool UsesDepth => _pattern.SelectedKey == nameof(PatternKind.Flemish) || _pattern.SelectedKey == nameof(PatternKind.English) || _pattern.SelectedKey == nameof(PatternKind.Header);

        void UpdateEnabled()
        {
            _depth.Enabled = UsesDepth;
            _paletteStrip.Visible = _fill.SelectedKey == nameof(FillMode.Palette) && _palette.Count > 0;
        }

        /// <summary>Called when the Material tab gets a new search result: offer brick sizes and the texture.</summary>
        public void OnNewResult(ResolveResult r)
        {
            if (r?.Brick != null)
            {
                _length.Value = r.Brick.LengthMm;
                _height.Value = r.Brick.HeightMm;
                if (r.Brick.DepthMm > 0) _depth.Value = r.Brick.DepthMm;
                _fill.SelectedKey = nameof(FillMode.Palette);
            }
        }

        // ------------------------------------------------------------------ source

        void UseMaterialImage()
        {
            var c = _host.CurrentCandidate;
            if (c?.Image?.Bytes == null)
            {
                // No texture: room shots can still provide colours.
                c = _host.CurrentResult?.References.FirstOrDefault();
                if (c?.Image?.Bytes == null) { _info.Text = "The Material tab has no image yet. Search for a product there first, or load an image."; return; }
            }
            SetSource(c.Image.Bytes, c.Url, _host.CurrentProductName, c.Kind == "room" ? 0 : _host.CurrentWidthMm);
            OnNewResult(_host.CurrentResult);
        }

        void LoadImage()
        {
            var dlg = new OpenFileDialog { Title = "Choose a product image", MultiSelect = false };
            dlg.Filters.Add(new FileFilter("Images", ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff"));
            if (dlg.ShowDialog(this) != DialogResult.Ok) return;
            try
            {
                var bytes = File.ReadAllBytes(dlg.FileName);
                if (ImageFormat.Sniff(bytes) == ImageKind.Webp) bytes = WebpConverter.Convert(bytes).bytes;
                SetSource(bytes, dlg.FileName, Path.GetFileNameWithoutExtension(dlg.FileName), 0);
            }
            catch (Exception ex) { _info.Text = "Couldn't read that image: " + ex.Message; }
        }

        void SetSource(byte[] bytes, string url, string productName, double widthMm)
        {
            _srcBytes = bytes;
            _srcUrl = url ?? "";
            _productName = string.IsNullOrWhiteSpace(productName) ? "Pattern" : productName.Trim();
            using (var ms = new MemoryStream(bytes)) _srcThumb.Image = new Bitmap(ms);
            _srcLabel.Text = $"{_productName}" + (widthMm > 0 ? $" · image covers {widthMm:0} mm" : " · size unknown");
            _srcWidth.Value = Math.Round(widthMm);
            try { _palette = Palette.Extract(bytes); } catch { _palette = new List<PaletteColor>(); }
            _paletteOff.Clear();
            _paletteStrip.Invalidate();
            UpdateEnabled();
            Queue();
        }

        void PaintPalette(object sender, PaintEventArgs e)
        {
            for (int i = 0; i < _palette.Count; i++)
            {
                var c = _palette[i];
                float x = i * 26;
                e.Graphics.FillRectangle(Color.FromArgb(c.R, c.G, c.B), x, 1, 22, 20);
                if (_paletteOff.Contains(i))
                {
                    e.Graphics.DrawLine(Colors.White, x, 1, x + 22, 21);
                    e.Graphics.DrawLine(Colors.Black, x, 2, x + 21, 21);
                }
            }
        }

        // ------------------------------------------------------------------ render

        void Queue()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        (PatternLayout layout, PatternStyle style, PatternSource source) Inputs(int maxPixels)
        {
            var kind = Enum.TryParse(_pattern.SelectedKey, out PatternKind k) ? k : PatternKind.Stretcher;
            var layout = PatternLayout.Create(new PatternSpec
            {
                Kind = kind, UnitLengthMm = _length.Value, UnitHeightMm = _height.Value, UnitDepthMm = _depth.Value, JointMm = _joint.Value,
            });
            var jc = _jointColor.Value;
            var style = new PatternStyle
            {
                Fill = Enum.TryParse(_fill.SelectedKey, out FillMode f) ? f : FillMode.Palette,
                JointR = (byte)(jc.R * 255), JointG = (byte)(jc.G * 255), JointB = (byte)(jc.B * 255),
                JointDepthMm = double.TryParse(_jointDepth.SelectedKey, out var d) ? d : 4,
                Variation = _variation.Value / 100.0,
                Seed = _seed,
                SourceGrainVertical = _grainVertical.Checked == true,
                Roughness = 0.75,
                MaxPixels = maxPixels,
            };
            var source = new PatternSource
            {
                ImageBytes = _srcBytes,
                ImageWidthMm = _srcWidth.Value,
                Palette = _palette.Where((p, i) => !_paletteOff.Contains(i)).ToList(),
            };
            return (layout, style, source);
        }

        async Task RenderPreviewAsync()
        {
            if (_srcBytes == null) { _info.Text = "Choose a product image first: use the Material tab's result or load one."; return; }
            int version = ++_renderVersion;
            try
            {
                var (layout, style, source) = Inputs(640);
                var r = await Task.Run(() => PatternRenderer.Render(layout, style, source));
                OnUi(() =>
                {
                    if (version != _renderVersion) return; // superseded by a newer change
                    using (var ms = new MemoryStream(r.Albedo)) _preview.Image = new Bitmap(ms);
                    _info.Text = $"Tile {r.WidthMm:0} × {r.HeightMm:0} mm (exact) · {string.Join(" ", r.Notes)}".Trim();
                    _send.Enabled = true;
                });
            }
            catch (Exception ex)
            {
                OnUi(() => { if (version == _renderVersion) { _info.Text = ex.Message; _send.Enabled = false; } });
            }
        }

        async Task SendAsync()
        {
            if (_srcBytes == null) return;
            _send.Enabled = false;
            _info.Text = "Building the full-resolution texture…";
            try
            {
                var (layout, style, source) = Inputs(2048);
                var kindName = Describe(Enum.TryParse(_pattern.SelectedKey, out PatternKind pk) ? pk : PatternKind.Stretcher);
                var r = await Task.Run(() => PatternRenderer.Render(layout, style, source));
                OnUi(() => Deliver(r, kindName));
            }
            catch (Exception ex)
            {
                OnUi(() => { _info.Text = "Couldn't build the texture: " + ex.Message; _send.Enabled = true; });
            }
        }

        void Deliver(PatternResult r, string kindName)
        {
            try
            {
                var label = $"pattern:{_pattern.SelectedKey}:{_srcUrl}";
                var albedo = ImageFetcher.SaveGenerated(r.Albedo, label);
                var stem = Path.Combine(Path.GetDirectoryName(albedo.LocalPath), Path.GetFileNameWithoutExtension(albedo.LocalPath));
                File.WriteAllBytes(stem + "_normal.png", r.Normal);
                File.WriteAllBytes(stem + "_roughness.png", r.Roughness);

                var candidate = new CandidateImage
                {
                    Url = label,
                    Image = albedo,
                    Kind = "pattern",
                    LikelyTileable = true,
                    Note = $"{kindName}, {_length.Value:0.#} × {_height.Value:0.#} mm units, {_joint.Value:0.#} mm joints",
                    Maps = new SurfaceMapFiles { NormalPath = stem + "_normal.png", RoughnessPath = stem + "_roughness.png" },
                };
                var scale = new ScaleDecision
                {
                    WidthMm = Math.Round(r.WidthMm, 1),
                    HeightMm = Math.Round(r.HeightMm, 1),
                    Source = ScaleSource.User,
                    Confidence = ScaleConfidence.High,
                    Rationale = $"Built from unit sizes: {candidate.Note}. Tile {r.WidthMm:0} × {r.HeightMm:0} mm. {string.Join(" ", r.Notes)}".Trim(),
                };
                _host.UsePatternTexture(candidate, scale, $"{_productName}, {kindName.ToLowerInvariant()}");
                _info.Text = "Sent to the Material tab.";
            }
            catch (Exception ex)
            {
                _info.Text = "Couldn't build the texture: " + ex.Message;
            }
            finally
            {
                _send.Enabled = true;
            }
        }

        static void OnUi(Action action) => Application.Instance.Invoke(action);
    }
}
