using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using MaterialAgent.Core;
using MaterialAgent.RhinoSide;
using Rhino;
using Rhino.DocObjects;

namespace MaterialAgent.UI
{
    /// <summary>
    /// Dockable, modeless panel: the user keeps selecting objects in the viewport while it is open.
    /// MVP step 1: manual image URL/path + tile size, thumbnail preview, Import.
    /// </summary>
    [Guid("3c8e9a1d-6b7f-4f2e-8a05-91d4c7e2b6f3")]
    public sealed class MaterialAgentPanel : Panel
    {
        public static Guid PanelId => typeof(MaterialAgentPanel).GUID;

        readonly uint _docSerial;
        CancellationTokenSource _cts;
        FetchedImage _image;
        ExistingMaterial _existing;
        ScaleSource _scaleSource = ScaleSource.User;
        ScaleConfidence _scaleConfidence = ScaleConfidence.High;
        bool _settingScale;

        // Search (agent, later)
        readonly TextBox _specBox = new TextBox { PlaceholderText = "e.g. Egger H1145 ST10" };
        readonly Button _resolveButton = new Button { Text = "Resolve", Enabled = false, ToolTip = "AI agent lookup is not wired up yet. Use a manual image below." };

        // Image source
        readonly TextBox _imageBox = new TextBox { PlaceholderText = "Image URL or file path" };
        readonly Button _browseButton = new Button { Text = "Browse…" };
        readonly Button _loadButton = new Button { Text = "Load preview" };
        readonly Button _cancelButton = new Button { Text = "Cancel", Visible = false };
        readonly ProgressBar _progress = new ProgressBar { Indeterminate = true, Visible = false };

        // Preview
        readonly ImageView _thumbnail = new ImageView { Size = new Size(-1, 220) };
        readonly Label _imageInfo = new Label { Text = "No image loaded.", TextColor = Colors.Gray };

        // Product / provenance
        readonly TextBox _nameBox = new TextBox { PlaceholderText = "Product name" };
        readonly TextBox _codeBox = new TextBox { PlaceholderText = "Product code (used to find it again)" };
        readonly TextBox _manufacturerBox = new TextBox { PlaceholderText = "Manufacturer" };
        readonly TextBox _pageUrlBox = new TextBox { PlaceholderText = "Product page URL" };
        readonly LinkButton _pageLink = new LinkButton { Text = "Open page", Enabled = false };

        // Scale and mapping
        readonly NumericStepper _widthMm = new NumericStepper { MinValue = 0.1, MaxValue = 100000, DecimalPlaces = 1, Value = 600, Increment = 10 };
        readonly NumericStepper _heightMm = new NumericStepper { MinValue = 0.1, MaxValue = 100000, DecimalPlaces = 1, Value = 600, Increment = 10 };
        readonly Button _swapButton = new Button { Text = "⇄", ToolTip = "Swap width and height", Width = 32 };
        readonly Label _scaleSourceLabel = new Label();
        readonly DropDown _mappingDrop = new DropDown();
        readonly DropDown _grainDrop = new DropDown();
        readonly CheckBox _rotateCheck = new CheckBox { Text = "Rotate 90°" };

        // Reuse + actions
        readonly CheckBox _reuseCheck = new CheckBox { Checked = true, Visible = false };
        readonly Button _importButton = new Button { Text = "Import to selection", Enabled = false };
        readonly Button _remapButton = new Button { Text = "Re-apply scale to selection", ToolTip = "Update the texture mapping of the selected objects without creating a new material." };
        readonly Label _status = new Label { Wrap = WrapMode.Word };

        public MaterialAgentPanel(uint documentSerialNumber)
        {
            _docSerial = documentSerialNumber;

            _mappingDrop.Items.Add("Box", nameof(MappingKind.Box));
            _mappingDrop.Items.Add("Planar", nameof(MappingKind.Planar));
            _mappingDrop.Items.Add("Per-face (box for now)", nameof(MappingKind.PerFace));
            _mappingDrop.SelectedKey = nameof(MappingKind.Box);

            _grainDrop.Items.Add("No grain", nameof(GrainAxis.None));
            _grainDrop.Items.Add("Horizontal in image", nameof(GrainAxis.Horizontal));
            _grainDrop.Items.Add("Vertical in image", nameof(GrainAxis.Vertical));
            _grainDrop.SelectedKey = nameof(GrainAxis.None);

            _browseButton.Click += (s, e) => Browse();
            _loadButton.Click += async (s, e) => await LoadPreviewAsync();
            _imageBox.KeyDown += async (s, e) => { if (e.Key == Keys.Enter) { e.Handled = true; await LoadPreviewAsync(); } };
            _cancelButton.Click += (s, e) => _cts?.Cancel();
            _pageUrlBox.TextChanged += (s, e) => _pageLink.Enabled = MaterialResolution.IsHttpUrl(_pageUrlBox.Text?.Trim());
            _pageLink.Click += (s, e) => OpenUrl(_pageUrlBox.Text?.Trim());
            _codeBox.TextChanged += (s, e) => RefreshExisting();
            _widthMm.ValueChanged += (s, e) => OnScaleEdited();
            _heightMm.ValueChanged += (s, e) => OnScaleEdited();
            _swapButton.Click += (s, e) => SwapScale();
            _reuseCheck.CheckedChanged += (s, e) => UpdateButtons();
            _importButton.Click += (s, e) => Import();
            _remapButton.Click += (s, e) => Remap();

            Content = new Scrollable { Border = BorderType.None, Content = BuildLayout() };
            UpdateScaleSourceLabel();
        }

        Control BuildLayout()
        {
            var layout = new DynamicLayout { Padding = new Padding(8), DefaultSpacing = new Size(6, 6) };

            layout.AddRow(Header("Product"));
            layout.AddRow(TableLayout.HorizontalScaled(4, new TableCell(_specBox, true), _resolveButton));

            layout.AddRow(Header("Texture image"));
            layout.AddRow(new TableLayout { Spacing = new Size(6, 0), Rows = { new TableRow(new TableCell(_imageBox, true), _browseButton) } });
            layout.AddRow(new TableLayout { Spacing = new Size(6, 0), Rows = { new TableRow(_loadButton, _cancelButton, new TableCell(_progress, true)) } });
            layout.AddRow(_thumbnail);
            layout.AddRow(_imageInfo);

            layout.AddRow(Header("Details"));
            layout.AddRow(_nameBox);
            layout.AddRow(_codeBox);
            layout.AddRow(_manufacturerBox);
            layout.AddRow(new TableLayout { Spacing = new Size(6, 0), Rows = { new TableRow(new TableCell(_pageUrlBox, true), _pageLink) } });

            layout.AddRow(Header("Real-world repeat size"));
            layout.AddRow(new TableLayout
            {
                Spacing = new Size(6, 4),
                Rows =
                {
                    new TableRow(new Label { Text = "Width (mm)", VerticalAlignment = VerticalAlignment.Center }, new TableCell(_widthMm, true), null),
                    new TableRow(new Label { Text = "Height (mm)", VerticalAlignment = VerticalAlignment.Center }, new TableCell(_heightMm, true), _swapButton),
                },
            });
            layout.AddRow(_scaleSourceLabel);

            layout.AddRow(Header("Mapping"));
            layout.AddRow(new TableLayout
            {
                Spacing = new Size(6, 4),
                Rows =
                {
                    new TableRow(new Label { Text = "Type", VerticalAlignment = VerticalAlignment.Center }, new TableCell(_mappingDrop, true)),
                    new TableRow(new Label { Text = "Grain", VerticalAlignment = VerticalAlignment.Center }, new TableCell(_grainDrop, true)),
                    new TableRow(null, _rotateCheck),
                },
            });

            layout.AddRow(_reuseCheck);
            layout.AddRow(_importButton);
            layout.AddRow(_remapButton);
            layout.AddRow(_status);
            layout.AddRow(new Label
            {
                Text = "Images come from third-party sites. Check the site's terms before use. The source URL is stored with the material.",
                TextColor = Colors.Gray,
                Wrap = WrapMode.Word,
                Font = SystemFonts.Default(SystemFonts.Default().Size - 1),
            });
            layout.Add(null);
            return layout;
        }

        static Label Header(string text) => new Label { Text = text, Font = SystemFonts.Bold() };

        RhinoDoc Doc => RhinoDoc.FromRuntimeSerialNumber(_docSerial) ?? RhinoDoc.ActiveDoc;

        // ---------------------------------------------------------------- image loading

        void Browse()
        {
            var dlg = new OpenFileDialog { Title = "Choose a texture image", MultiSelect = false };
            dlg.Filters.Add(new FileFilter("Images", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif"));
            dlg.Filters.Add(new FileFilter("All files", ".*"));
            if (dlg.ShowDialog(this) == DialogResult.Ok)
            {
                _imageBox.Text = dlg.FileName;
                _ = LoadPreviewAsync();
            }
        }

        async Task LoadPreviewAsync()
        {
            var source = _imageBox.Text?.Trim();
            if (string.IsNullOrEmpty(source)) { SetStatus("Enter an image URL or choose a file.", true); return; }

            _cts?.Cancel();
            var cts = new CancellationTokenSource();
            _cts = cts;
            SetBusy(true);
            SetStatus("Loading image…");

            try
            {
                var image = await Task.Run(() => ImageFetcher.FetchAsync(source, cts.Token), cts.Token);

                OnUi(() =>
                {
                    if (cts != _cts) return; // superseded by a newer load
                    Bitmap bitmap;
                    using (var ms = new MemoryStream(image.Bytes))
                        bitmap = new Bitmap(ms);
                    _image = image;
                    _thumbnail.Image = bitmap;
                    _imageInfo.Text = $"{bitmap.Width} × {bitmap.Height} px · {image.Kind.ToString().ToUpperInvariant()} · {(image.IsRemote ? "downloaded" : "local file")}";
                    _imageInfo.TextColor = SystemColors.ControlText;
                    if (string.IsNullOrWhiteSpace(_nameBox.Text))
                        _nameBox.Text = Path.GetFileNameWithoutExtension(image.IsRemote ? new Uri(image.Source).AbsolutePath : image.Source);
                    RefreshExisting();
                    SetStatus("Image loaded. Check the repeat size, then select objects and Import.");
                });
            }
            catch (OperationCanceledException)
            {
                OnUi(() => SetStatus("Cancelled."));
            }
            catch (Exception ex)
            {
                OnUi(() => SetStatus(ex.Message, true));
            }
            finally
            {
                OnUi(() =>
                {
                    if (cts == _cts) { SetBusy(false); _cts = null; }
                    UpdateButtons();
                });
                cts.Dispose();
            }
        }

        void SetBusy(bool busy)
        {
            _progress.Visible = busy;
            _cancelButton.Visible = busy;
            _loadButton.Enabled = !busy;
            _browseButton.Enabled = !busy;
            UpdateButtons();
        }

        // ---------------------------------------------------------------- scale

        void OnScaleEdited()
        {
            if (_settingScale) return;
            _scaleSource = ScaleSource.User;
            _scaleConfidence = ScaleConfidence.High;
            UpdateScaleSourceLabel();
        }

        void SwapScale()
        {
            _settingScale = true;
            (_widthMm.Value, _heightMm.Value) = (_heightMm.Value, _widthMm.Value);
            _settingScale = false;
        }

        void UpdateScaleSourceLabel()
        {
            string source = _scaleSource switch
            {
                ScaleSource.PageText => "stated on product page",
                ScaleSource.ImageFeature => "measured from a feature in the image",
                ScaleSource.CategoryPrior => "typical size for this kind of material",
                _ => "entered by you",
            };
            _scaleSourceLabel.Text = $"Source: {source} · confidence {EnumText.ToWire(_scaleConfidence)}";
            _scaleSourceLabel.TextColor = _scaleConfidence == ScaleConfidence.Low ? Colors.DarkOrange : Colors.Gray;
        }

        MappingSettings CurrentMapping() => new MappingSettings
        {
            WidthMm = _widthMm.Value,
            HeightMm = _heightMm.Value,
            Kind = Enum.TryParse(_mappingDrop.SelectedKey, out MappingKind m) ? m : MappingKind.Box,
            Grain = Enum.TryParse(_grainDrop.SelectedKey, out GrainAxis g) ? g : GrainAxis.None,
            Rotate90 = _rotateCheck.Checked == true,
        };

        // ---------------------------------------------------------------- reuse

        void RefreshExisting()
        {
            var imageKey = _image?.Source ?? _imageBox.Text?.Trim();
            _existing = MaterialReuse.Find(Doc, _codeBox.Text?.Trim(), imageKey);
            if (_existing != null)
            {
                _reuseCheck.Text = $"Reuse '{_existing.Material.Name}' already in this document";
                _reuseCheck.Visible = true;
            }
            else
            {
                _reuseCheck.Visible = false;
            }
            UpdateButtons();
        }

        // ---------------------------------------------------------------- import

        void UpdateButtons()
        {
            bool busy = _cts != null;
            bool reuse = _existing != null && _reuseCheck.Visible && _reuseCheck.Checked == true;
            _importButton.Enabled = !busy && (_image != null || reuse);
        }

        static List<RhinoObject> SelectedTargets(RhinoDoc doc)
        {
            const ObjectType renderable = ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion | ObjectType.Mesh | ObjectType.SubD;
            return doc.Objects.GetSelectedObjects(false, false)
                .Where(o => (o.ObjectType & renderable) != 0)
                .ToList();
        }

        void Import()
        {
            var doc = Doc;
            if (doc == null) { SetStatus("No active document.", true); return; }

            RefreshExisting();
            bool reuse = _existing != null && _reuseCheck.Checked == true;
            if (_image == null && !reuse) { SetStatus("Load an image first.", true); return; }

            var mapping = CurrentMapping();
            var settings = new ImportSettings
            {
                MaterialName = MaterialName(),
                Image = _image,
                Mapping = mapping,
                Provenance = _image == null ? null : new Provenance
                {
                    ProductCode = NullIfBlank(_codeBox.Text),
                    ProductName = NullIfBlank(_nameBox.Text),
                    Manufacturer = NullIfBlank(_manufacturerBox.Text),
                    PageUrl = NullIfBlank(_pageUrlBox.Text),
                    ImageUrl = _image.Source,
                    FetchDateUtc = _image.FetchedUtc,
                    ScaleSource = _scaleSource,
                    ScaleConfidence = _scaleConfidence,
                    WidthMm = mapping.WidthMm,
                    HeightMm = mapping.HeightMm,
                    Mapping = mapping.Kind,
                    Grain = mapping.Grain,
                },
            };

            try
            {
                var targets = SelectedTargets(doc);
                var result = MaterialFactory.Import(doc, settings, targets, reuse ? _existing.Material : null);
                var verb = result.Reused ? "Reused" : "Created";
                SetStatus(targets.Count == 0
                    ? $"{verb} material '{result.Material.Name}'. No surfaces, meshes or SubDs were selected, so nothing was assigned."
                    : $"{verb} material '{result.Material.Name}' and applied it to {result.AssignedCount} object(s) at {mapping.WidthMm:0.#} × {mapping.HeightMm:0.#} mm.");
                RefreshExisting();
            }
            catch (Exception ex)
            {
                SetStatus("Import failed: " + ex.Message, true);
            }
        }

        void Remap()
        {
            var doc = Doc;
            if (doc == null) return;
            var targets = SelectedTargets(doc);
            if (targets.Count == 0) { SetStatus("Select the objects to re-scale first.", true); return; }
            try
            {
                var mapping = CurrentMapping();
                uint undo = doc.BeginUndoRecord("Material Agent re-scale");
                int n;
                try { n = MappingApplier.Apply(doc, targets, mapping); }
                finally { doc.EndUndoRecord(undo); }
                doc.Views.Redraw();
                SetStatus($"Updated mapping on {n} object(s) to {mapping.WidthMm:0.#} × {mapping.HeightMm:0.#} mm.");
            }
            catch (Exception ex)
            {
                SetStatus("Re-scale failed: " + ex.Message, true);
            }
        }

        string MaterialName()
        {
            var name = NullIfBlank(_nameBox.Text);
            var code = NullIfBlank(_codeBox.Text);
            if (name != null && code != null && name.IndexOf(code, StringComparison.OrdinalIgnoreCase) < 0) return $"{name} ({code})";
            return name ?? code ?? "Material Agent texture";
        }

        // ---------------------------------------------------------------- helpers

        void SetStatus(string text, bool error = false)
        {
            _status.Text = text;
            _status.TextColor = error ? Colors.Red : SystemColors.ControlText;
        }

        static void OnUi(Action action) => Application.Instance.Invoke(action);

        static void OpenUrl(string url)
        {
            if (!MaterialResolution.IsHttpUrl(url)) return;
            try { Application.Instance.Open(url); } catch { /* no browser available */ }
        }

        static string NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _cts?.Cancel();
            base.Dispose(disposing);
        }
    }
}
