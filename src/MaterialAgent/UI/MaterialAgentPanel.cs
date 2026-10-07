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
using MaterialAgent.Core.Agent;
using MaterialAgent.Core.Colors;
using MaterialAgent.RhinoSide;
using Rhino;
using Rhino.DocObjects;

namespace MaterialAgent.UI
{
    /// <summary>
    /// Dockable, modeless panel: the user keeps selecting objects in the viewport while it is open.
    /// Search → preview (candidates, scale with source/confidence, mapping) → correct → Import → adjust.
    /// </summary>
    [Guid("3c8e9a1d-6b7f-4f2e-8a05-91d4c7e2b6f3")]
    public sealed class MaterialAgentPanel : Panel
    {
        public static Guid PanelId => typeof(MaterialAgentPanel).GUID;

        const int ThumbSize = 56;

        readonly uint _docSerial;
        CancellationTokenSource _cts;
        ResolveResult _result;
        readonly List<CandidateImage> _candidates = new List<CandidateImage>();
        int _selected = -1;
        FetchedImage _image;
        ExistingMaterial _existing;
        ScaleSource _scaleSource = ScaleSource.User;
        ScaleConfidence _scaleConfidence = ScaleConfidence.High;
        bool _settingScale;
        readonly UITimer _liveTimer = new UITimer { Interval = 0.35 };

        // Search
        readonly TextBox _specBox = new TextBox { PlaceholderText = "Product name or code, e.g. Egger H1145 ST10" };
        readonly Button _resolveButton = new Button { Text = "Find" };
        readonly Button _cancelButton = new Button { Text = "Cancel", Visible = false };
        readonly ProgressBar _progress = new ProgressBar { Indeterminate = true, Visible = false };
        readonly Label _progressLabel = new Label { TextColor = Colors.Gray, Wrap = WrapMode.Word };

        // What was found
        readonly Label _productTitle = new Label { Font = SystemFonts.Bold(), Wrap = WrapMode.Word };
        readonly Label _productSub = new Label { TextColor = Colors.Gray, Wrap = WrapMode.Word };
        readonly LinkButton _pageLink = new LinkButton { Visible = false };

        // Candidates + preview
        readonly StackLayout _candidateStrip = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 4 };
        readonly Scrollable _candidateScroll;
        readonly ImageView _preview = new ImageView { Size = new Size(-1, 170) };
        readonly Label _imageInfo = new Label { Text = "No image yet.", TextColor = Colors.Gray, Wrap = WrapMode.Word };

        // Seamless tools
        readonly CheckBox _tiledCheck = new CheckBox { Text = "Preview tiled 2×2", ToolTip = "Shows the image repeated, so you can spot seams and obvious repetition." };
        readonly Button _blendButton = new Button { Text = "Blend edges", ToolTip = "Free: makes the selected image tile by blending its borders with itself. Good for swatches that almost tile." };
        readonly Button _generateButton = new Button { Text = "Generate seamless (AI)", ToolTip = "Uses Gemini's image model (Nano Banana) and your API key to paint a flat, tileable texture from the found images. Costs a few cents per image." };
        readonly Label _tileHint = new Label { TextColor = Colors.DarkOrange, Wrap = WrapMode.Word, Visible = false };

        // Manual image
        readonly TextBox _imageBox = new TextBox { PlaceholderText = "Image URL or file path" };
        readonly Button _browseButton = new Button { Text = "Browse…" };
        readonly Button _loadButton = new Button { Text = "Load" };

        // Details / provenance
        readonly TextBox _nameBox = new TextBox { PlaceholderText = "Product name" };
        readonly TextBox _codeBox = new TextBox { PlaceholderText = "Product code (used to find it again)" };
        readonly TextBox _manufacturerBox = new TextBox { PlaceholderText = "Manufacturer" };
        readonly TextBox _pageUrlBox = new TextBox { PlaceholderText = "Product page URL" };

        // Scale
        readonly NumericStepper _widthMm = new NumericStepper { MinValue = 0.1, MaxValue = 100000, DecimalPlaces = 1, Value = 600, Increment = 10 };
        readonly NumericStepper _heightMm = new NumericStepper { MinValue = 0.1, MaxValue = 100000, DecimalPlaces = 1, Value = 600, Increment = 10 };
        readonly Button _swapButton = new Button { Text = "⇄", ToolTip = "Swap width and height", Width = 32 };
        readonly CheckBox _lockAspect = new CheckBox { Text = "Keep image proportions", Checked = true };
        readonly Label _scaleSourceLabel = new Label { Wrap = WrapMode.Word };
        readonly Label _rationaleLabel = new Label { TextColor = Colors.Gray, Wrap = WrapMode.Word };

        // Mapping + surface
        readonly DropDown _mappingDrop = new DropDown();
        readonly DropDown _grainDrop = new DropDown();
        readonly CheckBox _rotateCheck = new CheckBox { Text = "Rotate 90°" };
        readonly DropDown _finishDrop = new DropDown();
        readonly CheckBox _enscapeCheck = new CheckBox { Text = "Create as Enscape material", ToolTip = "Enscape already renders the standard material. Tick this only if you want to edit it in the Enscape Material Editor: it converts the material to Enscape's type (experimental)." };
        readonly Label _enscapeNote = new Label { TextColor = Colors.Gray, Wrap = WrapMode.Word };
        readonly CheckBox _mapsCheck = new CheckBox { Text = "Generate normal + roughness maps", Checked = true, ToolTip = "Approximated from the image; product pages rarely provide real PBR maps." };

        // Import + adjust
        readonly CheckBox _reuseCheck = new CheckBox { Checked = true, Visible = false };
        readonly Button _importButton = new Button { Text = "Import to selection", Enabled = false };
        readonly Button _importLayerButton = new Button { Text = "Import to layer…", Enabled = false, ToolTip = "Sets the material on chosen layers, so objects drawn there later get it too (with real-world mapping)." };

        // RAL colours (built in)
        RalColor _ralColor;
        readonly DropDown _ralAlternatives = new DropDown { Visible = false, ToolTip = "Other colours matching your search" };
        Control _scaleSection, _mappingSection;
        readonly CheckBox _liveCheck = new CheckBox { Text = "Live: update selected objects while editing scale/mapping" };
        readonly Button _remapButton = new Button { Text = "Re-apply to selection", ToolTip = "Update the texture mapping of the selected objects without creating a new material." };
        readonly Button _measureButton = new Button { Text = "Measure in viewport…", ToolTip = "Pick two points on a feature of known size and type its real length (MatAgentRescale)." };
        readonly Label _status = new Label { Wrap = WrapMode.Word };

        // Tabs
        readonly TabControl _tabs = new TabControl();
        readonly TabPage _materialPage = new TabPage { Text = "Material" };
        readonly TabPage _settingsPage = new TabPage { Text = "Settings" };

        // Settings tab
        readonly PasswordBox _apiKeyBox = new PasswordBox();
        readonly TextBox _apiKeyPlain = new TextBox { Visible = false };
        readonly CheckBox _showKeyCheck = new CheckBox { Text = "Show key" };
        readonly TextBox _modelBox = new TextBox { PlaceholderText = AgentSettings.DefaultModel };
        readonly Button _saveSettingsButton = new Button { Text = "Save" };
        readonly Button _testKeyButton = new Button { Text = "Test key" };
        readonly Button _clearKeyButton = new Button { Text = "Remove key" };
        readonly Label _keySourceLabel = new Label { TextColor = Colors.Gray, Wrap = WrapMode.Word };
        readonly Label _settingsStatus = new Label { Wrap = WrapMode.Word };
        readonly Label _enscapeStatus = new Label { Wrap = WrapMode.Word };
        readonly TextBox _enscapeTypeBox = new TextBox { PlaceholderText = "Auto-detect (leave empty)" };
        readonly Button _enscapeDetectButton = new Button { Text = "Detect again" };
        readonly TextBox _imageModelBox = new TextBox { PlaceholderText = AgentSettings.DefaultImageModel };
        readonly DropDown _imageSizeDrop = new DropDown();
        readonly CheckBox _autoGenerateCheck = new CheckBox { Text = "Automatically generate a seamless texture when none is found (costs per image)" };
        readonly Button _enscapeListButton = new Button { Text = "List material types", ToolTip = "Prints every material type and its ID to the Rhino command line." };
        readonly Expander _manualExpander = new Expander { Header = new Label { Text = "Use your own image" } };
        readonly Expander _detailsExpander = new Expander { Header = new Label { Text = "Details (name, code, product page)" } };
        readonly Expander _adjustExpander = new Expander { Header = new Label { Text = "Adjust after import" } };
        readonly UITimer _busyTimer = new UITimer { Interval = 1 };
        string _busyMessage = "";
        DateTime _busyStarted;

        public MaterialAgentPanel(uint documentSerialNumber)
        {
            _docSerial = documentSerialNumber;
            _candidateScroll = new Scrollable { Content = _candidateStrip, Height = ThumbSize + 14, Border = BorderType.None, ExpandContentHeight = false, Visible = false };

            _mappingDrop.Items.Add("Box", nameof(MappingKind.Box));
            _mappingDrop.Items.Add("Planar", nameof(MappingKind.Planar));
            _mappingDrop.Items.Add("Per-face (box for now)", nameof(MappingKind.PerFace));
            _mappingDrop.SelectedKey = nameof(MappingKind.Box);

            _grainDrop.Items.Add("No grain", nameof(GrainAxis.None));
            _grainDrop.Items.Add("Horizontal in image", nameof(GrainAxis.Horizontal));
            _grainDrop.Items.Add("Vertical in image", nameof(GrainAxis.Vertical));
            _grainDrop.SelectedKey = nameof(GrainAxis.None);

            foreach (Finish f in Enum.GetValues(typeof(Finish)))
                _finishDrop.Items.Add($"{f} (roughness {EnumText.Roughness(f):0.##})", f.ToString());
            _finishDrop.SelectedKey = nameof(Finish.Matt);

            _resolveButton.Click += async (s, e) => await ResolveAsync();
            _specBox.KeyDown += async (s, e) => { if (e.Key == Keys.Enter) { e.Handled = true; await ResolveAsync(); } };
            _cancelButton.Click += (s, e) => _cts?.Cancel();
            _browseButton.Click += (s, e) => Browse();
            _loadButton.Click += async (s, e) => await LoadManualAsync();
            _imageBox.KeyDown += async (s, e) => { if (e.Key == Keys.Enter) { e.Handled = true; await LoadManualAsync(); } };
            _pageUrlBox.TextChanged += (s, e) => UpdatePageLink();
            _pageLink.Click += (s, e) => OpenUrl(_pageUrlBox.Text?.Trim());
            _codeBox.TextChanged += (s, e) => RefreshExisting();
            _widthMm.ValueChanged += (s, e) => OnScaleEdited(widthChanged: true);
            _heightMm.ValueChanged += (s, e) => OnScaleEdited(widthChanged: false);
            _swapButton.Click += (s, e) => SwapScale();
            _mappingDrop.SelectedIndexChanged += (s, e) => QueueLive();
            _grainDrop.SelectedIndexChanged += (s, e) => QueueLive();
            _rotateCheck.CheckedChanged += (s, e) => QueueLive();
            _reuseCheck.CheckedChanged += (s, e) => UpdateButtons();
            _importButton.Click += (s, e) => Import(toLayers: false);
            _importLayerButton.Click += (s, e) => Import(toLayers: true);
            _ralAlternatives.SelectedIndexChanged += (s, e) =>
            {
                if (_ralAlternatives.SelectedIndex > 0 && _ralAlternatives.SelectedValue is ListItem li && li.Tag is RalColor c)
                    ShowRal(new RalMatch { Color = c }, keepAlternatives: true);
            };
            _remapButton.Click += (s, e) => Remap(quiet: false);
            _measureButton.Click += (s, e) => RhinoApp.RunScript("_MatAgentRescale", false);
            _saveSettingsButton.Click += (s, e) => SaveSettings();
            _busyTimer.Elapsed += (s, e) => { if (_cts != null) ShowBusyText(); };
            _testKeyButton.Click += async (s, e) => await TestKeyAsync();
            _clearKeyButton.Click += (s, e) => ClearKey();
            _showKeyCheck.CheckedChanged += (s, e) => ToggleShowKey();
            _tiledCheck.CheckedChanged += (s, e) => { if (_selected >= 0 && _selected < _candidates.Count) _preview.Image = PreviewImage(_candidates[_selected].Image); };
            _blendButton.Click += async (s, e) => await BlendEdgesAsync();
            _generateButton.Click += async (s, e) => await GenerateSeamlessAsync(automatic: false);
            _imageSizeDrop.Items.Add("1K (cheaper)", "1K");
            _imageSizeDrop.Items.Add("2K (sharper, costs more)", "2K");
            _enscapeCheck.CheckedChanged += (s, e) => { if (_enscapeCheck.Enabled) AgentSettingsStore.CreateEnscape = _enscapeCheck.Checked == true; };
            _enscapeDetectButton.Click += (s, e) => { AgentSettingsStore.EnscapeTypeId = _enscapeTypeBox.Text; EnscapeSupport.Redetect(); UpdateEnscapeUi(); };
            _enscapeListButton.Click += (s, e) =>
            {
                int n = EnscapeSupport.ListMaterialTypes();
                _enscapeStatus.Text = $"Listed {n} material type(s) on the Rhino command line. Copy Enscape's ID into the box above and press Detect again.";
            };
            _apiKeyBox.TextChanged += (s, e) => { if (!_apiKeyPlain.Visible) _apiKeyPlain.Text = _apiKeyBox.Text; };
            _apiKeyPlain.TextChanged += (s, e) => { if (_apiKeyPlain.Visible) _apiKeyBox.Text = _apiKeyPlain.Text; };
            _liveTimer.Elapsed += (s, e) => { _liveTimer.Stop(); Remap(quiet: true); };
            MaterialAgentEvents.ScaleChanged += OnExternalScaleChanged;

            _materialPage.Content = VerticalScroller(BuildLayout());
            _settingsPage.Content = VerticalScroller(BuildSettingsPage());
            _tabs.Pages.Add(_materialPage);
            _tabs.Pages.Add(_settingsPage);
            Content = _tabs;
            LoadSettingsIntoUi();
            UpdateEnscapeUi();
            UpdateScaleSourceLabel();
        }

        // Layout note: never put null in a TableRow unless a stretchy spacer is wanted. Eto treats null cells as
        // scaled, which splits rows into equal columns and wastes most of the panel's width.
        Control BuildLayout()
        {
            var small = SystemFonts.Default(SystemFonts.Default().Size - 1);
            foreach (var l in new[] { _productSub, _imageInfo, _tileHint, _scaleSourceLabel, _rationaleLabel, _status, _progressLabel, _enscapeNote })
                l.Font = small;

            var layout = new DynamicLayout { Padding = new Padding(6), DefaultSpacing = new Size(4, 4) };

            // Search
            layout.AddRow(Row(Stretch(_specBox), _resolveButton, _cancelButton));
            layout.AddRow(_progress);
            layout.AddRow(_progressLabel);

            // What was found
            layout.AddRow(_productTitle);
            layout.AddRow(Row(Stretch(_productSub), _pageLink));
            layout.AddRow(_preview);
            layout.AddRow(_candidateScroll);
            layout.AddRow(_imageInfo);
            layout.AddRow(_ralAlternatives);
            layout.AddRow(_tileHint);
            layout.AddRow(Row(_tiledCheck, Spacer(), _blendButton, _generateButton));

            // Size, mapping, surface: label | control pairs, labels sized to their text.
            var sizeRow = new TableLayout
            {
                Spacing = new Size(4, 3),
                Rows =
                {
                    new TableRow(Caption("Size"), Stretch(_widthMm), Caption("×"), Stretch(_heightMm), Caption("mm"), _swapButton),
                },
            };
            _scaleSection = new StackLayout
            {
                Spacing = 2,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items = { sizeRow, Row(_lockAspect, Stretch(_scaleSourceLabel)), _rationaleLabel },
            };
            layout.AddRow(_scaleSection);

            _mappingSection = new TableLayout
            {
                Spacing = new Size(4, 3),
                Rows =
                {
                    new TableRow(Caption("Mapping"), Stretch(_mappingDrop), Caption("Grain"), Stretch(_grainDrop)),
                    new TableRow(Caption(""), _rotateCheck, Caption(""), _mapsCheck),
                },
            };
            layout.AddRow(_mappingSection);
            layout.AddRow(new TableLayout
            {
                Spacing = new Size(4, 3),
                Rows = { new TableRow(Caption("Finish"), Stretch(_finishDrop), _enscapeCheck) },
            });
            layout.AddRow(_enscapeNote);

            // Import
            layout.AddRow(_reuseCheck);
            layout.AddRow(Row(Stretch(_importButton), Stretch(_importLayerButton)));
            layout.AddRow(_status);

            // Less used: collapsed by default.
            _detailsExpander.Content = new StackLayout
            {
                Spacing = 3,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    Row(Stretch(_nameBox), Stretch(_codeBox)),
                    Row(_manufacturerBox, Stretch(_pageUrlBox)),
                },
            };
            _manufacturerBox.Width = 120;
            layout.AddRow(_detailsExpander);

            _manualExpander.Content = new TableLayout
            {
                Spacing = new Size(4, 0),
                Padding = new Padding(0, 3),
                Rows = { new TableRow(Stretch(_imageBox), _browseButton, _loadButton) },
            };
            layout.AddRow(_manualExpander);

            _adjustExpander.Content = new StackLayout
            {
                Spacing = 3,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items = { _liveCheck, Row(Stretch(_remapButton), Stretch(_measureButton)) },
            };
            layout.AddRow(_adjustExpander);

            layout.Add(null);
            return layout;
        }

        /// <summary>Room left for the vertical scrollbar, which appears without resizing the scroll area.</summary>
        const int ScrollbarAllowance = 20;

        /// <summary>
        /// A vertically scrolling container whose content is pinned to the visible width. Without this, long
        /// wrapped labels report their unwrapped width, the content grows sideways and buttons end up off-screen.
        /// </summary>
        static Scrollable VerticalScroller(Control content)
        {
            var scroll = new Scrollable { Border = BorderType.None, ExpandContentWidth = true, Content = content };
            scroll.SizeChanged += (s, e) =>
            {
                int w = scroll.ClientSize.Width - ScrollbarAllowance;
                if (w > 100 && content.Width != w) content.Width = w;
            };
            return scroll;
        }

        static TableLayout Row(params TableCell[] cells)
        {
            var row = new TableRow();
            foreach (var c in cells) row.Cells.Add(c);
            return new TableLayout { Spacing = new Size(4, 0), Rows = { row } };
        }

        static TableCell Stretch(Control c) => new TableCell(c, true);
        static TableCell Spacer() => new TableCell(null, true);

        Control BuildSettingsPage()
        {
            var getKey = new LinkButton { Text = "Get a free Gemini API key (Google AI Studio)" };
            getKey.Click += (s, e) => OpenUrl("https://aistudio.google.com/apikey");

            var layout = new DynamicLayout { Padding = new Padding(8), DefaultSpacing = new Size(6, 6) };
            layout.AddRow(Header("Gemini API key"));
            layout.AddRow(new Label
            {
                Text = "The agent uses Google's Gemini to find products. Paste your key here and press Save; it is remembered for next time.",
                Wrap = WrapMode.Word,
            });
            layout.AddRow(getKey);
            layout.AddRow(new TableLayout
            {
                Spacing = new Size(6, 4),
                Rows =
                {
                    new TableRow(Caption("Key"), new TableCell(new StackLayout { Items = { new StackLayoutItem(_apiKeyBox, true), new StackLayoutItem(_apiKeyPlain, true) }, HorizontalContentAlignment = HorizontalAlignment.Stretch }, true)),
                    new TableRow(Caption(""), _showKeyCheck),
                },
            });
            layout.AddRow(Header("Model"));
            layout.AddRow(new TableLayout { Spacing = new Size(6, 4), Rows = { new TableRow(Caption("Model"), new TableCell(_modelBox, true)) } });
            layout.AddRow(new Label
            {
                Text = $"Default: {AgentSettings.DefaultModel}. For lower cost try gemini-flash-lite-latest (less careful).",
                TextColor = Colors.Gray,
                Wrap = WrapMode.Word,
            });
            layout.AddRow(new TableLayout { Spacing = new Size(6, 0), Rows = { new TableRow(_saveSettingsButton, _testKeyButton, _clearKeyButton, null) } });
            layout.AddRow(_settingsStatus);
            layout.AddRow(_keySourceLabel);

            layout.AddRow(Header("Seamless texture generation"));
            layout.AddRow(new TableLayout
            {
                Spacing = new Size(6, 4),
                Rows =
                {
                    new TableRow(Caption("Image model"), new TableCell(_imageModelBox, true)),
                    new TableRow(Caption("Size"), new TableCell(_imageSizeDrop, true)),
                },
            });
            layout.AddRow(new Label
            {
                Text = $"Default {AgentSettings.DefaultImageModel} (\"Nano Banana 2\"). Uses the same API key; saved with Save above.",
                TextColor = Colors.Gray,
                Wrap = WrapMode.Word,
            });
            layout.AddRow(_autoGenerateCheck);

            layout.AddRow(Header("Enscape"));
            layout.AddRow(_enscapeStatus);
            layout.AddRow(new TableLayout { Spacing = new Size(6, 4), Rows = { new TableRow(Caption("Material type ID"), new TableCell(_enscapeTypeBox, true)) } });
            layout.AddRow(new TableLayout { Spacing = new Size(6, 0), Rows = { new TableRow(_enscapeDetectButton, _enscapeListButton, null) } });

            layout.AddRow(Header("About"));
            layout.AddRow(new Label
            {
                Text = "Images come from third-party sites. Check each site's terms before use. The source URL is stored with every material.",
                TextColor = Colors.Gray,
                Wrap = WrapMode.Word,
            });
            layout.Add(null);
            return layout;
        }

        static Label Header(string text) => new Label { Text = text, Font = SystemFonts.Bold() };
        static Label Caption(string text) => new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };

        RhinoDoc Doc => RhinoDoc.FromRuntimeSerialNumber(_docSerial) ?? RhinoDoc.ActiveDoc;

        // ================================================================ agent search

        async Task ResolveAsync()
        {
            var query = _specBox.Text?.Trim();
            if (string.IsNullOrEmpty(query)) { SetStatus("Type a product name or code.", true); return; }

            // RAL colours are built in: no web search, no API key, no cost.
            if (RalCatalog.TryMatch(query, out var ral))
            {
                var inDoc = MaterialReuse.Find(Doc, ral.Color.Code, null);
                if (inDoc != null && MessageBox.Show(this, $"'{inDoc.Material.Name}' is already in this document.\n\nUse it instead of creating another?",
                        "Material Agent", MessageBoxButtons.YesNo, MessageBoxType.Question) == DialogResult.Yes)
                {
                    ShowExisting(inDoc);
                    return;
                }
                ShowRal(ral, keepAlternatives: false);
                return;
            }

            // Reuse before fetching: the document may already have this product.
            var existing = MaterialReuse.FindByQuery(Doc, query);
            if (existing != null)
            {
                var answer = MessageBox.Show(this,
                    $"'{existing.Material.Name}' is already in this document.\n\nUse it instead of searching the web?",
                    "Material Agent", MessageBoxButtons.YesNo, MessageBoxType.Question);
                if (answer == DialogResult.Yes) { ShowExisting(existing); return; }
            }

            var settings = AgentSettingsStore.Load();
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                _tabs.SelectedPage = _settingsPage;
                SetSettingsStatus("Paste your Gemini API key here and press Save, then search again.", true);
                return;
            }

            var cts = BeginBusy("Asking the agent…");
            try
            {
                var resolver = new GeminiMaterialResolver(settings);
                var progress = new UiProgress(text => { if (cts == _cts) { _busyMessage = text; ShowBusyText(); } });
                var result = await Task.Run(() => resolver.ResolveAsync(query, progress, cts.Token), cts.Token);
                OnUi(() => { if (cts == _cts) ShowResult(result); });
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
                OnUi(() => EndBusy(cts));
            }
        }

        void ShowResult(ResolveResult r)
        {
            LeaveRalMode();
            _result = r;
            var p = r.Resolution.Product;
            _nameBox.Text = p.Name ?? "";
            _codeBox.Text = p.Code ?? "";
            _manufacturerBox.Text = p.Manufacturer ?? "";
            _pageUrlBox.Text = p.PageUrl ?? "";
            ShowProductHeader(p.Name, p.Manufacturer, p.Code, r.Category);

            _mappingDrop.SelectedKey = r.Mapping.ToString();
            _grainDrop.SelectedKey = r.Grain.ToString();
            _rotateCheck.Checked = false;
            _finishDrop.SelectedKey = r.Finish.ToString();

            SetCandidates(r.Candidates);
            ApplyScaleDecision(r.Scale);
            if (r.Candidates.Count == 0)
            {
                _image = null;
                _preview.Image = null;
                _imageInfo.Text = $"No flat texture found, only {r.References.Count} room/perspective photo(s). Generate seamless (AI) can paint a texture from them, or paste an image URL under Use your own image.";
                _imageInfo.TextColor = Colors.DarkOrange;
                _generateButton.Enabled = r.References.Count > 0;
                UpdateButtons();
            }

            var tokens = r.Usage.Total;
            var timing = string.Join(" · ", r.Timings.Select(t => $"{t.Key} {t.Value.TotalSeconds:0}s"));
            var msg = r.Candidates.Count == 0
                ? $"Found the product but no usable texture. ({timing}, ~{tokens:N0} tokens)"
                : $"Found {r.Candidates.Count} texture(s). Check the picture and size, select objects, then Import. ({timing}, ~{tokens:N0} tokens)";
            if (r.Warnings.Count > 0) msg += "\n" + string.Join("\n", r.Warnings.Take(3));
            SetStatus(msg);
            _progressLabel.Text = r.Sources.Count > 0 ? "Sources: " + string.Join(", ", r.Sources.Take(4).Select(s => s.Key)) : "";
            RefreshExisting();

            bool anyTileable = r.Candidates.Any(c => c.LikelyTileable && c.MatchesProduct);
            if (r.Candidates.Count == 0 && r.References.Count == 0) return;
            if (!anyTileable && AgentSettingsStore.AutoGenerateSeamless)
                Application.Instance.AsyncInvoke(async () => await GenerateSeamlessAsync(automatic: true));
        }

        void ShowExisting(ExistingMaterial existing)
        {
            var p = existing.Provenance;
            _result = null;
            SetCandidates(new List<CandidateImage>());
            _image = null;
            _preview.Image = null;
            _imageInfo.Text = "Using the material already in this document.";
            _nameBox.Text = p.ProductName ?? existing.Material.Name;
            _codeBox.Text = p.ProductCode ?? "";
            _manufacturerBox.Text = p.Manufacturer ?? "";
            _pageUrlBox.Text = p.PageUrl ?? "";
            ShowProductHeader(_nameBox.Text, p.Manufacturer, p.ProductCode, p.Category);
            _mappingDrop.SelectedKey = p.Mapping.ToString();
            _grainDrop.SelectedKey = p.Grain.ToString();
            _rotateCheck.Checked = p.Rotate90;
            _finishDrop.SelectedKey = p.Finish.ToString();
            SetScale(p.WidthMm > 0 ? p.WidthMm : _widthMm.Value, p.HeightMm > 0 ? p.HeightMm : _heightMm.Value, p.ScaleSource, p.ScaleConfidence, "");
            RefreshExisting();
            if (_existing == null)
            {
                _existing = existing;
                _reuseCheck.Text = $"Reuse '{existing.Material.Name}' already in this document";
                _reuseCheck.Visible = true;
            }
            _reuseCheck.Checked = true;
            UpdateButtons();
            SetStatus($"Reusing '{existing.Material.Name}'. Select objects and Import.");
        }

        void ShowProductHeader(string name, string manufacturer, string code, string category)
        {
            _productTitle.Text = name ?? "";
            _productSub.Text = string.Join(" · ", new[] { manufacturer, code, category }.Where(x => !string.IsNullOrWhiteSpace(x)));
            UpdatePageLink();
        }

        void UpdatePageLink()
        {
            var url = _pageUrlBox.Text?.Trim();
            bool ok = MaterialResolution.IsHttpUrl(url);
            _pageLink.Visible = ok;
            if (ok) { _pageLink.Text = "Product page: " + new Uri(url).Host; _pageLink.ToolTip = url; }
        }

        // ================================================================ candidates

        void SetCandidates(IList<CandidateImage> candidates)
        {
            _candidates.Clear();
            _candidates.AddRange(candidates);
            _candidateStrip.Items.Clear();
            for (int i = 0; i < _candidates.Count; i++)
            {
                int index = i;
                var c = _candidates[i];
                var thumb = new ImageView { Size = new Size(ThumbSize, ThumbSize), Image = Decode(c.Image) };
                var frame = new Panel { Padding = new Padding(2), Content = thumb, ToolTip = Describe(c) + "\n" + c.Url };
                thumb.MouseDown += (s, e) => SelectCandidate(index);
                frame.MouseDown += (s, e) => SelectCandidate(index);
                _candidateStrip.Items.Add(frame);
            }
            _candidateScroll.Visible = _candidates.Count > 1;
            _selected = -1;
            if (_candidates.Count > 0) SelectCandidate(0);
        }

        void SelectCandidate(int index)
        {
            if (index < 0 || index >= _candidates.Count) return;
            LeaveRalMode();
            _selected = index;
            var c = _candidates[index];
            _image = c.Image;
            _preview.Image = PreviewImage(c.Image);
            bool tiles = c.LikelyTileable || c.Kind == "generated" || c.Kind == "blended";
            _tileHint.Visible = !tiles;
            _tileHint.Text = "This image may not tile cleanly (check with Preview tiled). Try Blend edges, or Generate seamless (AI).";
            for (int i = 0; i < _candidateStrip.Items.Count; i++)
                if (_candidateStrip.Items[i].Control is Panel p)
                    p.BackgroundColor = i == index ? SystemColors.Highlight : Colors.Transparent;

            var dims = c.Image.PixelWidth > 0 ? $"{c.Image.PixelWidth} × {c.Image.PixelHeight} px" : c.Image.Kind.ToString().ToUpperInvariant();
            _imageInfo.Text = $"{dims}{(c.Image.ConvertedFromWebp ? " (converted from WebP)" : "")} · {Describe(c)}";
            _imageInfo.TextColor = c.Kind == "room" || !c.MatchesProduct ? Colors.DarkOrange : SystemColors.ControlText;

            // A different picture covers a different area: keep the width, follow its proportions.
            if (_lockAspect.Checked == true && c.Image.Aspect > 0)
            {
                _settingScale = true;
                _heightMm.Value = ScaleLadder.HeightForWidth(_widthMm.Value, c.Image.Aspect);
                _settingScale = false;
            }
            RefreshExisting();
            UpdateButtons();
        }

        static string Describe(CandidateImage c)
        {
            var bits = new List<string>();
            if (!string.IsNullOrEmpty(c.Kind)) bits.Add(c.Kind);
            bits.Add(c.LikelyTileable ? "tileable" : "may not tile");
            if (!c.MatchesProduct) bits.Add("may be a different product");
            bits.Add(c.FromPage ? "from product page"
                : c.Kind == "manual" ? "your image"
                : c.Kind == "generated" ? "AI-generated"
                : c.Kind == "blended" ? "edges blended in code"
                : "suggested by agent");
            if (!string.IsNullOrWhiteSpace(c.Note)) bits.Add(c.Note);
            return string.Join(" · ", bits);
        }

        static Bitmap Decode(FetchedImage image)
        {
            if (image?.Bytes == null) return null;
            try { using (var ms = new MemoryStream(image.Bytes)) return new Bitmap(ms); }
            catch { return null; }
        }

        // ================================================================ RAL colours

        void ShowRal(RalMatch match, bool keepAlternatives)
        {
            var c = match.Color;
            _ralColor = c;
            _result = null;
            _image = null;
            _candidates.Clear();
            _candidateStrip.Items.Clear();
            _candidateScroll.Visible = false;
            _selected = -1;
            _tileHint.Visible = false;

            _preview.Image = Swatch(c);
            var system = c.System == RalSystem.Classic ? "RAL Classic" : "RAL Design System";
            var info = $"{c.Code} {c.Name} · {c.Hex} · built-in {system} colour (screen approximation; check against a physical RAL fan for colour-critical work)";
            if (c.Special == RalSpecial.Metallic) info += " · pearl/metallic: rendered with some metalness";
            if (c.Special == RalSpecial.Luminous) info += " · fluorescent: real samples are brighter than any screen can show";
            _imageInfo.Text = info;
            _imageInfo.TextColor = c.Special == RalSpecial.Luminous ? Colors.DarkOrange : SystemColors.ControlText;

            _nameBox.Text = c.Name;
            _codeBox.Text = c.Code;
            _manufacturerBox.Text = "RAL";
            _pageUrlBox.Text = "";
            ShowProductHeader(c.Name, "RAL", c.Code, system + " colour");
            _finishDrop.SelectedKey = c.System == RalSystem.Classic ? nameof(Finish.Satin) : nameof(Finish.Matt);
            _scaleSection.Enabled = false;
            _mappingSection.Enabled = false;

            if (!keepAlternatives)
            {
                _ralAlternatives.Items.Clear();
                if (match.Alternatives.Count > 0)
                {
                    _ralAlternatives.Items.Add(new ListItem { Text = $"Other matches ({match.Alternatives.Count})…" });
                    _ralAlternatives.Items.Add(new ListItem { Text = c.Display, Tag = c });
                    foreach (var alt in match.Alternatives)
                        _ralAlternatives.Items.Add(new ListItem { Text = alt.Display, Tag = alt });
                    _ralAlternatives.SelectedIndex = 0;
                }
                _ralAlternatives.Visible = match.Alternatives.Count > 0;
            }

            RefreshExisting();
            UpdateButtons();
            _progressLabel.Text = "";
            SetStatus($"{c.Display}: choose the finish, then Import to selection or to a layer. No web search needed.");
        }

        void LeaveRalMode()
        {
            if (_ralColor == null) return;
            _ralColor = null;
            _ralAlternatives.Visible = false;
            _scaleSection.Enabled = true;
            _mappingSection.Enabled = true;
        }

        static Bitmap Swatch(RalColor c)
        {
            var color = Color.FromArgb(c.R, c.G, c.B);
            const int w = 320, h = 200;
            return new Bitmap(w, h, PixelFormat.Format32bppRgb, Enumerable.Repeat(color, w * h));
        }

        // ================================================================ seamless tools

        /// <summary>The selected image, or a 2×2 tiling of it (downscaled) so seams show.</summary>
        Image PreviewImage(FetchedImage image)
        {
            var bmp = Decode(image);
            if (bmp == null || _tiledCheck.Checked != true) return bmp;
            const int maxSide = 400;
            double s = Math.Min(1.0, (double)maxSide / Math.Max(bmp.Width, bmp.Height));
            int w = Math.Max(1, (int)(bmp.Width * s)), h = Math.Max(1, (int)(bmp.Height * s));
            var tiled = new Bitmap(w * 2, h * 2, PixelFormat.Format32bppRgb);
            using (var g = new Graphics(tiled))
            {
                g.ImageInterpolation = ImageInterpolation.High;
                for (int ty = 0; ty < 2; ty++)
                    for (int tx = 0; tx < 2; tx++)
                        g.DrawImage(bmp, tx * w, ty * h, w, h);
            }
            bmp.Dispose();
            return tiled;
        }

        CandidateImage SelectedCandidate => _selected >= 0 && _selected < _candidates.Count ? _candidates[_selected] : null;

        void AddAndSelect(CandidateImage c)
        {
            var list = new List<CandidateImage> { c };
            list.AddRange(_candidates);
            SetCandidates(list);
        }

        async Task BlendEdgesAsync()
        {
            var source = SelectedCandidate;
            if (source == null) { SetStatus("Find a product or load an image first.", true); return; }
            var cts = BeginBusy("Blending edges…");
            try
            {
                var blended = await Task.Run(() =>
                    ImageFetcher.SaveGenerated(SeamlessTile.MakeSeamless(source.Image.Bytes), "seamless-blend:" + source.Url), cts.Token);
                OnUi(() =>
                {
                    if (cts != _cts) return;
                    // Blending keeps the pixel size, so the real-world scale stays as it is.
                    AddAndSelect(new CandidateImage { Url = blended.Source, Image = blended, Kind = "blended", LikelyTileable = true, Note = "from the selected image" });
                    _tiledCheck.Checked = true;
                    SetStatus("Made a tileable version by blending the edges. Check the tiled preview for ghosting or obvious repeats.");
                });
            }
            catch (OperationCanceledException) { OnUi(() => SetStatus("Cancelled.")); }
            catch (Exception ex) { OnUi(() => SetStatus("Blend failed: " + ex.Message, true)); }
            finally { OnUi(() => EndBusy(cts)); }
        }

        async Task GenerateSeamlessAsync(bool automatic)
        {
            var selected = SelectedCandidate;
            var roomShots = _result?.References ?? new List<CandidateImage>();
            if (selected == null && roomShots.Count == 0) { SetStatus("Find a product or load an image first.", true); return; }
            var settings = AgentSettingsStore.Load();
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                _tabs.SelectedPage = _settingsPage;
                SetSettingsStatus("Generating a texture needs your Gemini API key. Paste it here and press Save.", true);
                return;
            }

            // References: the selected texture first, then other textures, then room shots (the generator
            // extracts the surface from them).
            var references = new List<CandidateImage>();
            if (selected != null) references.Add(selected);
            references.AddRange(_candidates.Where(c => c != selected && c.MatchesProduct && c.Kind != "generated" && c.Kind != "blended"));
            references.AddRange(roomShots.Where(c => c.MatchesProduct));
            var product = new ProductInfo { Name = NullIfBlank(_nameBox.Text), Code = NullIfBlank(_codeBox.Text), Manufacturer = NullIfBlank(_manufacturerBox.Text) };
            var basis = new ScaleDecision { WidthMm = _widthMm.Value, HeightMm = _heightMm.Value, Source = _scaleSource, Confidence = _scaleConfidence, Rationale = _rationaleLabel.Text };
            double w = _widthMm.Value, h = _heightMm.Value;
            var category = _result?.Category;
            var finish = CurrentFinish();

            var cts = BeginBusy(automatic ? "No seamless image found: generating one…" : "Generating a seamless texture…");
            try
            {
                var generator = new SeamlessTextureGenerator(settings);
                var generated = await Task.Run(() => generator.GenerateAsync(product, category, finish, references, w, h, basis, cts.Token), cts.Token);
                OnUi(() =>
                {
                    if (cts != _cts) return;
                    AddAndSelect(generated.Candidate);
                    ApplyScaleDecision(generated.Scale);
                    _tiledCheck.Checked = true;
                    SetStatus($"Generated a seamless texture with {settings.ImageModel}. Compare it with the product photos before importing; AI images can drift in colour and pattern. (~{generated.Usage.Total:N0} tokens)");
                });
            }
            catch (OperationCanceledException) { OnUi(() => SetStatus("Cancelled.")); }
            catch (Exception ex) { OnUi(() => SetStatus("Generation failed: " + ex.Message, true)); }
            finally { OnUi(() => EndBusy(cts)); }
        }

        // ================================================================ manual image

        void Browse()
        {
            var dlg = new OpenFileDialog { Title = "Choose a texture image", MultiSelect = false };
            dlg.Filters.Add(new FileFilter("Images", ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".gif"));
            dlg.Filters.Add(new FileFilter("All files", ".*"));
            if (dlg.ShowDialog(this) == DialogResult.Ok)
            {
                _imageBox.Text = dlg.FileName;
                _ = LoadManualAsync();
            }
        }

        async Task LoadManualAsync()
        {
            var source = _imageBox.Text?.Trim();
            if (string.IsNullOrEmpty(source)) { SetStatus("Enter an image URL or choose a file.", true); return; }

            var cts = BeginBusy("Loading image…");
            try
            {
                var image = await Task.Run(() => ImageFetcher.FetchAsync(source, cts.Token), cts.Token);
                OnUi(() =>
                {
                    if (cts != _cts) return;
                    var manual = new CandidateImage { Url = image.Source, Image = image, Kind = "manual", LikelyTileable = true };
                    // Keep the agent's candidates (if any) and put the user's image first.
                    var list = new List<CandidateImage> { manual };
                    list.AddRange(_candidates.Where(c => c.Kind != "manual"));
                    SetCandidates(list);
                    if (string.IsNullOrWhiteSpace(_nameBox.Text))
                        _nameBox.Text = Path.GetFileNameWithoutExtension(image.IsRemote ? new Uri(image.Source).AbsolutePath : image.Source);
                    MarkScaleAsUser();
                    SetStatus("Image loaded. Set the real-world size, select objects, then Import.");
                });
            }
            catch (OperationCanceledException) { OnUi(() => SetStatus("Cancelled.")); }
            catch (Exception ex) { OnUi(() => SetStatus(ex.Message, true)); }
            finally { OnUi(() => EndBusy(cts)); }
        }

        // ================================================================ busy state

        CancellationTokenSource BeginBusy(string message)
        {
            _cts?.Cancel();
            var cts = new CancellationTokenSource();
            _cts = cts;
            _progress.Visible = true;
            _cancelButton.Visible = true;
            _resolveButton.Enabled = false;
            _loadButton.Enabled = false;
            _browseButton.Enabled = false;
            _blendButton.Enabled = false;
            _generateButton.Enabled = false;
            _busyMessage = message;
            _busyStarted = DateTime.Now;
            _progressLabel.Text = message;
            _busyTimer.Start();
            SetStatus("");
            UpdateButtons();
            return cts;
        }

        void ShowBusyText()
        {
            var secs = (int)(DateTime.Now - _busyStarted).TotalSeconds;
            _progressLabel.Text = secs > 0 ? $"{_busyMessage} {secs}s" : _busyMessage;
        }

        void EndBusy(CancellationTokenSource cts)
        {
            if (cts == _cts)
            {
                _cts = null;
                _busyTimer.Stop();
                _progress.Visible = false;
                _cancelButton.Visible = false;
                _resolveButton.Enabled = true;
                _loadButton.Enabled = true;
                _browseButton.Enabled = true;
                _blendButton.Enabled = true;
                _generateButton.Enabled = true;
                if (_result == null || _progressLabel.Text.Contains("…")) _progressLabel.Text = "";
            }
            cts.Dispose();
            UpdateButtons();
        }

        // ================================================================ scale

        void ApplyScaleDecision(ScaleDecision d)
        {
            if (d == null) return;
            SetScale(d.WidthMm, d.HeightMm, d.Source, d.Confidence, d.Rationale);
        }

        void SetScale(double w, double h, ScaleSource source, ScaleConfidence confidence, string rationale)
        {
            _settingScale = true;
            _widthMm.Value = Math.Max(_widthMm.MinValue, w);
            _heightMm.Value = Math.Max(_heightMm.MinValue, h);
            _settingScale = false;
            _scaleSource = source;
            _scaleConfidence = confidence;
            _rationaleLabel.Text = rationale ?? "";
            UpdateScaleSourceLabel();
            QueueLive();
        }

        void OnScaleEdited(bool widthChanged)
        {
            if (_settingScale) return;
            var aspect = _image?.Aspect ?? 0;
            if (_lockAspect.Checked == true && aspect > 0)
            {
                _settingScale = true;
                if (widthChanged) _heightMm.Value = ScaleLadder.HeightForWidth(_widthMm.Value, aspect);
                else _widthMm.Value = ScaleLadder.WidthForHeight(_heightMm.Value, aspect);
                _settingScale = false;
            }
            MarkScaleAsUser();
            QueueLive();
        }

        void MarkScaleAsUser()
        {
            _scaleSource = ScaleSource.User;
            _scaleConfidence = ScaleConfidence.High;
            UpdateScaleSourceLabel();
        }

        void SwapScale()
        {
            _settingScale = true;
            (_widthMm.Value, _heightMm.Value) = (_heightMm.Value, _widthMm.Value);
            _settingScale = false;
            MarkScaleAsUser();
            QueueLive();
        }

        void UpdateScaleSourceLabel()
        {
            string source = _scaleSource switch
            {
                ScaleSource.PageText => "stated on the product page",
                ScaleSource.ImageFeature => "counted from features in the image",
                ScaleSource.CategoryPrior => "typical size for this kind of material, please check",
                _ => "entered by you",
            };
            _scaleSourceLabel.Text = $"Source: {source} · confidence {EnumText.ToWire(_scaleConfidence)}";
            _scaleSourceLabel.TextColor = _scaleConfidence switch
            {
                ScaleConfidence.Low => Colors.OrangeRed,
                ScaleConfidence.Medium => Colors.DarkGoldenrod,
                _ => Colors.Green,
            };
        }

        void OnExternalScaleChanged(object sender, Provenance p)
        {
            OnUi(() =>
            {
                if (IsDisposed) return;
                SetScale(p.WidthMm, p.HeightMm, p.ScaleSource, p.ScaleConfidence, "Measured in the viewport.");
            });
        }

        MappingSettings CurrentMapping() => new MappingSettings
        {
            WidthMm = _widthMm.Value,
            HeightMm = _heightMm.Value,
            Kind = Enum.TryParse(_mappingDrop.SelectedKey, out MappingKind m) ? m : MappingKind.Box,
            Grain = Enum.TryParse(_grainDrop.SelectedKey, out GrainAxis g) ? g : GrainAxis.None,
            Rotate90 = _rotateCheck.Checked == true,
        };

        Finish CurrentFinish() => Enum.TryParse(_finishDrop.SelectedKey, out Finish f) ? f : Finish.Matt;

        // ================================================================ reuse

        void RefreshExisting()
        {
            var imageKey = _image?.Source;
            _existing = MaterialReuse.Find(Doc, _codeBox.Text?.Trim(), imageKey);
            _reuseCheck.Visible = _existing != null;
            if (_existing != null) _reuseCheck.Text = $"Reuse '{_existing.Material.Name}' already in this document";
            UpdateButtons();
        }

        // ================================================================ import

        void UpdateButtons()
        {
            bool busy = _cts != null;
            bool reuse = _existing != null && _reuseCheck.Checked == true;
            bool ready = !busy && (_image != null || _ralColor != null || reuse);
            _importButton.Enabled = ready;
            _importLayerButton.Enabled = ready;
        }

        static List<RhinoObject> SelectedTargets(RhinoDoc doc)
        {
            const ObjectType renderable = ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion | ObjectType.Mesh | ObjectType.SubD;
            return doc.Objects.GetSelectedObjects(false, false)
                .Where(o => (o.ObjectType & renderable) != 0)
                .ToList();
        }

        void Import(bool toLayers)
        {
            var doc = Doc;
            if (doc == null) { SetStatus("No active document.", true); return; }

            RefreshExisting();
            bool reuse = _existing != null && _reuseCheck.Checked == true;
            var ral = _image == null ? _ralColor : null;
            if (_image == null && ral == null && !reuse) { SetStatus("Find a product or colour, or load an image first.", true); return; }

            int[] layers = null;
            if (toLayers)
            {
                if (!Rhino.UI.Dialogs.ShowSelectMultipleLayersDialog(new[] { doc.Layers.CurrentLayerIndex }, "Apply material to layers", false, out layers)
                    || layers == null || layers.Length == 0)
                    return;
            }

            var mapping = CurrentMapping();
            var settings = new ImportSettings
            {
                MaterialName = MaterialName(),
                Image = _image,
                SolidColor = ral,
                Mapping = mapping,
                Finish = CurrentFinish(),
                GenerateMaps = _mapsCheck.Checked == true,
                AsEnscape = _enscapeCheck.Enabled && _enscapeCheck.Checked == true,
                Provenance = _image != null || ral != null ? BuildProvenance(mapping, ral) : null,
            };

            try
            {
                var targets = toLayers ? new List<RhinoObject>() : SelectedTargets(doc);
                var result = MaterialFactory.Import(doc, settings, targets, reuse ? _existing.Material : null, layers);
                MaterialAgentEvents.LastMapping = mapping;
                if (result.Reused) UpdateProvenanceOf(doc, new[] { result.Material }, mapping);

                var verb = result.Reused ? "Reused" : "Created";
                bool solid = ral != null || ProvenanceStore.Read(doc, result.Material)?.IsSolidColor == true;
                var scaleText = solid ? "" : $" at {mapping.WidthMm:0.#} × {mapping.HeightMm:0.#} mm";
                string msg;
                if (toLayers)
                {
                    msg = $"{verb} material '{result.Material.Name}' and set it on {result.LayerCount} layer(s); {result.AssignedCount} object(s) on them now use it{scaleText}.";
                    if (result.KeptOwnMaterialCount > 0)
                        msg += $" {result.KeptOwnMaterialCount} object(s) keep their own material (set their material to 'Use layer' to change that).";
                    if (!solid) msg += " Objects added to these layers later get the same real-world mapping.";
                }
                else
                {
                    msg = targets.Count == 0
                        ? $"{verb} material '{result.Material.Name}'. No surfaces, meshes or SubDs were selected, so nothing was assigned."
                        : $"{verb} material '{result.Material.Name}' and applied it to {result.AssignedCount} object(s){scaleText}.";
                }
                if (result.MapError != null) msg += "\nMaps were not generated: " + result.MapError;
                if (result.IsEnscape) msg += "\nCreated as an Enscape material: open the Enscape Material Editor to fine-tune it.";
                if (result.EnscapeWarning != null) msg += "\n" + result.EnscapeWarning;
                SetStatus(msg);
                RefreshExisting();
            }
            catch (Exception ex)
            {
                SetStatus("Import failed: " + ex.Message, true);
            }
        }

        Provenance BuildProvenance(MappingSettings mapping, RalColor ral) => new Provenance
        {
            ProductCode = NullIfBlank(_codeBox.Text),
            ProductName = NullIfBlank(_nameBox.Text),
            Manufacturer = NullIfBlank(_manufacturerBox.Text),
            PageUrl = NullIfBlank(_pageUrlBox.Text),
            ImageUrl = _image?.Source,
            FetchDateUtc = _image?.FetchedUtc ?? DateTime.UtcNow,
            ColorHex = ral?.Hex,
            ScaleSource = ral != null ? ScaleSource.User : _scaleSource,
            ScaleConfidence = ral != null ? ScaleConfidence.High : _scaleConfidence,
            WidthMm = ral != null ? 0 : mapping.WidthMm,
            HeightMm = ral != null ? 0 : mapping.HeightMm,
            Mapping = mapping.Kind,
            Grain = mapping.Grain,
            Rotate90 = mapping.Rotate90,
            Finish = CurrentFinish(),
            Category = ral != null ? (ral.System == RalSystem.Classic ? "RAL Classic colour" : "RAL Design colour") : _result?.Category,
        };

        void QueueLive()
        {
            if (_liveCheck.Checked != true || _settingScale) return;
            _liveTimer.Stop();
            _liveTimer.Start();
        }

        void Remap(bool quiet)
        {
            var doc = Doc;
            if (doc == null) return;
            var targets = SelectedTargets(doc);
            if (targets.Count == 0) { if (!quiet) SetStatus("Select the objects to re-scale first.", true); return; }
            try
            {
                var mapping = CurrentMapping();
                uint undo = doc.BeginUndoRecord("Material Agent re-scale");
                int n;
                try
                {
                    n = MappingApplier.Apply(doc, targets, mapping);
                    UpdateProvenanceOf(doc, targets.Select(t => t.RenderMaterial), mapping);
                }
                finally { doc.EndUndoRecord(undo); }
                doc.Views.Redraw();
                MaterialAgentEvents.LastMapping = mapping;
                SetStatus($"Updated mapping on {n} object(s) to {mapping.WidthMm:0.#} × {mapping.HeightMm:0.#} mm.");
            }
            catch (Exception ex)
            {
                SetStatus("Re-scale failed: " + ex.Message, true);
            }
        }

        /// <summary>Keeps each material's stored scale in step with how it is mapped, for later re-scales.</summary>
        void UpdateProvenanceOf(RhinoDoc doc, IEnumerable<Rhino.Render.RenderMaterial> materials, MappingSettings mapping)
        {
            foreach (var rm in materials.Where(m => m != null).GroupBy(m => m.Id).Select(g => g.First()))
            {
                var p = ProvenanceStore.Read(doc, rm);
                if (p == null || p.IsSolidColor) continue;
                p.WidthMm = mapping.WidthMm;
                p.HeightMm = mapping.HeightMm;
                p.Mapping = mapping.Kind;
                p.Grain = mapping.Grain;
                p.Rotate90 = mapping.Rotate90;
                p.ScaleSource = _scaleSource;
                p.ScaleConfidence = _scaleConfidence;
                ProvenanceStore.Write(doc, rm, p);
            }
        }

        string MaterialName()
        {
            var name = NullIfBlank(_nameBox.Text);
            var code = NullIfBlank(_codeBox.Text);
            if (name != null && code != null && name.IndexOf(code, StringComparison.OrdinalIgnoreCase) < 0) return $"{name} ({code})";
            return name ?? code ?? "Material Agent texture";
        }

        // ================================================================ settings

        void LoadSettingsIntoUi()
        {
            _apiKeyBox.Text = AgentSettingsStore.SavedApiKey;
            _apiKeyPlain.Text = _apiKeyBox.Text;
            _modelBox.Text = AgentSettingsStore.SavedModel;
            _imageModelBox.Text = AgentSettingsStore.ImageModel;
            _imageSizeDrop.SelectedKey = AgentSettingsStore.ImageSize == "2K" ? "2K" : "1K";
            _autoGenerateCheck.Checked = AgentSettingsStore.AutoGenerateSeamless;
            UpdateKeySourceLabel();
            if (string.IsNullOrWhiteSpace(AgentSettings.ResolveApiKey(AgentSettingsStore.SavedApiKey)))
            {
                _tabs.SelectedPage = _settingsPage;
                SetSettingsStatus("No API key yet. Paste one above and press Save.", true);
            }
        }

        void UpdateKeySourceLabel()
        {
            var saved = AgentSettingsStore.SavedApiKey;
            if (!string.IsNullOrWhiteSpace(saved))
                _keySourceLabel.Text = $"Using the saved key {AgentSettings.Mask(saved)}. It is stored in Rhino's plug-in settings on this computer (plain text, like other Rhino settings).";
            else if (!string.IsNullOrWhiteSpace(AgentSettings.EnvironmentKey))
                _keySourceLabel.Text = $"No saved key; using the {AgentSettings.EnvApiKey} environment variable.";
            else
                _keySourceLabel.Text = "";
        }

        string TypedKey => (_apiKeyPlain.Visible ? _apiKeyPlain.Text : _apiKeyBox.Text)?.Trim() ?? "";

        void ToggleShowKey()
        {
            bool show = _showKeyCheck.Checked == true;
            if (show) _apiKeyPlain.Text = _apiKeyBox.Text; else _apiKeyBox.Text = _apiKeyPlain.Text;
            _apiKeyPlain.Visible = show;
            _apiKeyBox.Visible = !show;
        }

        void SaveSettings()
        {
            AgentSettingsStore.Save(TypedKey, _modelBox.Text);
            AgentSettingsStore.ImageModel = _imageModelBox.Text;
            AgentSettingsStore.ImageSize = _imageSizeDrop.SelectedKey ?? "1K";
            AgentSettingsStore.AutoGenerateSeamless = _autoGenerateCheck.Checked == true;
            UpdateKeySourceLabel();
            SetSettingsStatus(string.IsNullOrEmpty(TypedKey) ? "Saved (no key)." : "Saved. Press Test key to check it.");
        }

        void ClearKey()
        {
            _apiKeyBox.Text = "";
            _apiKeyPlain.Text = "";
            AgentSettingsStore.Save("", _modelBox.Text);
            UpdateKeySourceLabel();
            SetSettingsStatus("Key removed.");
        }

        async Task TestKeyAsync()
        {
            var key = AgentSettings.ResolveApiKey(TypedKey);
            if (string.IsNullOrWhiteSpace(key)) { SetSettingsStatus("Paste a key first.", true); return; }
            var model = string.IsNullOrWhiteSpace(_modelBox.Text) ? AgentSettings.DefaultModel : _modelBox.Text.Trim();
            _testKeyButton.Enabled = false;
            SetSettingsStatus("Checking…");
            try
            {
                var client = new GeminiClient(GeminiMaterialResolver.SharedHttp, key, model);
                var name = await Task.Run(() => client.CheckAsync(CancellationToken.None));
                OnUi(() => SetSettingsStatus($"Key works. Model: {name}." + (TypedKey != AgentSettingsStore.SavedApiKey ? " Press Save to keep it." : "")));
            }
            catch (Exception ex)
            {
                OnUi(() => SetSettingsStatus(ex.Message, true));
            }
            finally
            {
                OnUi(() => _testKeyButton.Enabled = true);
            }
        }

        void UpdateEnscapeUi()
        {
            AgentSettingsStore.ApplyEnscapeOverride();
            _enscapeTypeBox.Text = AgentSettingsStore.EnscapeTypeId;
            var type = EnscapeSupport.MaterialType;
            _enscapeCheck.Enabled = type != null;
            _enscapeCheck.Checked = type != null && AgentSettingsStore.CreateEnscape;
            _enscapeNote.Text = type == null ? "Enscape not detected (install or load Enscape, then Settings → Detect again)." : "";
            _enscapeNote.Visible = type == null;
            _enscapeStatus.Text = type == null
                ? "Enscape material type not found. If Enscape is installed, start it once, then press Detect again. If that fails, press List material types and paste Enscape's ID above."
                : $"Enscape material type: {type.InternalName} ({type.Id}).";
            _enscapeStatus.TextColor = type == null ? Colors.DarkOrange : Colors.Green;
        }

        void SetSettingsStatus(string text, bool error = false)
        {
            _settingsStatus.Text = text;
            _settingsStatus.TextColor = error ? Colors.Red : Colors.Green;
        }

        // ================================================================ helpers

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

        /// <summary>Progress reporter that always lands on the UI thread.</summary>
        sealed class UiProgress : IProgress<string>
        {
            readonly Action<string> _report;
            public UiProgress(Action<string> report) { _report = report; }
            public void Report(string value) => Application.Instance.AsyncInvoke(() => _report(value));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cts?.Cancel();
                _liveTimer.Stop();
                MaterialAgentEvents.ScaleChanged -= OnExternalScaleChanged;
            }
            base.Dispose(disposing);
        }
    }
}
