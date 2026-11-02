using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Hubwright.Engine;
using Hubwright.Preview;
using Hubwright.Proposals;
using Hubwright.Storage;
using Hubwright.Theming;

namespace Hubwright.Desktop.Pages;

/// <summary>
/// Edit a proposal's theme and logo: brand and background colours, layout, every palette slot if
/// wanted, a logo with Microsoft's size checks, and theme ideas from the brand and the logo's own
/// colours. The home page mockup on the right follows every change. Saving updates the proposal and
/// the implementation plan; nothing is sent to the tenant until the theme steps run.
/// </summary>
public sealed class ThemeEditorPage : Grid
{
    private static readonly string[] Slots =
    [
        "themePrimary", "themeLighterAlt", "themeLighter", "themeLight", "themeTertiary", "themeSecondary", "themeDarkAlt", "themeDark", "themeDarker",
        "neutralLighterAlt", "neutralLighter", "neutralLight", "neutralQuaternaryAlt", "neutralQuaternary", "neutralTertiaryAlt", "neutralTertiary",
        "neutralSecondary", "neutralPrimaryAlt", "neutralPrimary", "neutralDark", "black", "white",
    ];

    private readonly HubwrightSession _session = App.Current.Session;
    private readonly string? _folder;
    private readonly IntranetProposal? _proposal;
    private readonly WebView2 _web = new();
    private readonly TextBox _primary = Ui.Field("Brand colour", "");
    private readonly TextBox _background = Ui.Field("Background", "#ffffff");
    private readonly TextBox _name = Ui.Field("Theme name", "");
    private readonly ComboBox _layout = Ui.Choice("Header layout", ["standard", "compact", "minimal", "extended"], "standard");
    private readonly ColorPicker _picker = new() { IsAlphaEnabled = false, IsMoreButtonVisible = false, ColorSpectrumShape = ColorSpectrumShape.Ring, IsColorChannelTextInputVisible = false, IsHexInputVisible = false };
    private readonly StackPanel _checks = Ui.Stack(3);
    private readonly StackPanel _suggestions = Ui.Stack(8);
    private readonly StackPanel _logoChecks = Ui.Stack(4);
    private readonly Image _logoImage = new() { MaxHeight = 64, MaxWidth = 192, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _slots = Ui.Stack(4);
    private readonly Dictionary<string, TextBox> _slotBoxes = [];
    private readonly TextBlock _status = Ui.Muted("");
    private readonly CheckBox _custom = new() { Content = "Edit palette slots by hand (otherwise generated from the brand colour)" };
    private byte[]? _logo;
    private string? _logoExtension;
    private bool _logoRemoved;
    private bool _logoSquare;
    private IReadOnlyList<Rgb> _logoColors = [];
    private bool _updating;

    public ThemeEditorPage(string? folder)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(470) });
        ColumnDefinitions.Add(new ColumnDefinition());
        _folder = folder ?? _session.Store.IntranetProposals().FirstOrDefault();
        _proposal = _folder is null ? null : TenantStore.LoadIntranetProposal(_folder);
        if (_proposal is null)
        {
            Children.Add(Ui.Page(Ui.Header(Glyphs.Palette, "Theme and logo"), Ui.InfoBar("No proposal yet", "Create an intranet proposal first; the theme and logo belong to it.", InfoBarSeverity.Informational)));
            return;
        }

        var b = _proposal.Blueprint;
        _primary.Text = b.BrandColor;
        _background.Text = b.BackgroundColor;
        _name.Text = b.Theme().Name;
        _layout.SelectedItem = b.HeaderLayout;
        _custom.IsChecked = b.Palette is { Count: > 0 };
        var palette = b.Theme().Palette;
        foreach (var slot in Slots)
        {
            var box = new TextBox { Text = palette.GetValueOrDefault(slot, ""), Width = 110 };
            var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Ui.Resource("CardStrokeColorDefaultBrush") };
            void Paint()
            {
                if (Rgb.TryParse(box.Text, out var c))
                {
                    swatch.Background = Ui.Brush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
                }
            }

            Paint();
            box.TextChanged += (_, _) =>
            {
                Paint();
                if (!_updating)
                {
                    _custom.IsChecked = true;
                    Update();
                }
            };
            _slotBoxes[slot] = box;
            _slots.Children.Add(Ui.Row(swatch, box, Ui.Muted(slot)));
        }

        if (b.Logo is not null && File.Exists(Path.Combine(_folder!, b.Logo)))
        {
            _logo = File.ReadAllBytes(Path.Combine(_folder!, b.Logo));
            _logoExtension = Path.GetExtension(b.Logo);
        }

        var left = Ui.Stack(12,
            Ui.Header(Glyphs.Palette, "Theme and logo", _proposal.Name),
            Ui.Muted("Changes show in the mockup on the right. Save updates the proposal and its implementation steps; nothing reaches the tenant until you run the theme steps."),
            Ui.Card(Ui.Stack(10, Ui.Strong("Colours"), _picker, Ui.Columns(2, [_primary, _background]), _name, _layout,
                Ui.Row(Ui.Button("Make accessible", MakeAccessible, glyph: Glyphs.Check), Ui.Button("Dark background", () => _background.Text = "#1b1a19"), Ui.Button("White background", () => _background.Text = "#ffffff")))),
            Ui.Card(Ui.Stack(10, Ui.Strong("Logo"),
                Ui.Muted("Microsoft: \"" + ThemeSuggestions.LogoSizeQuote + "\" The site logo thumbnail \"" + ThemeSuggestions.ThumbnailQuote + "\"."),
                _logoImage,
                Ui.Row(Ui.Button("Upload logo…", UploadAsync, accent: true, glyph: Glyphs.Upload), Ui.Button("Remove", RemoveLogo, glyph: Glyphs.Cancel)),
                _logoChecks)),
            Ui.Card(Ui.Stack(10, Ui.Strong("Suggestions"), Ui.Muted("Ideas from your brand colour and your logo's colours, each adjusted to pass WCAG 2.1 AA. Select one to try it."), Ui.Button("Suggest themes", ShowSuggestions, glyph: Glyphs.Sparkle), _suggestions)),
            Ui.Card(Ui.Stack(6, Ui.Strong("Contrast"), _checks)),
            new Expander { Header = "Palette slots (advanced)", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = Ui.Stack(8, _custom, _slots) },
            Ui.Row(
                Ui.Button("Save", SaveAsync, accent: true, glyph: Glyphs.Save),
                Ui.Button("Back to Build", () => App.Current.Window.Show("build", _folder), glyph: Glyphs.Rocket),
                Ui.Button("Back to proposal", () => App.Current.Window.Show("proposal"), glyph: Glyphs.Sparkle)),
            _status);
        Children.Add(new ScrollViewer { Content = left, Padding = new Thickness(24, 16, 16, 24) });
        SetColumn(_web, 1);
        _web.Margin = new Thickness(0, 12, 12, 12);
        Children.Add(_web);

        _primary.TextChanged += (_, _) => FromText();
        _background.TextChanged += (_, _) => Update();
        _name.TextChanged += (_, _) => Update();
        _layout.SelectionChanged += (_, _) => Update();
        _custom.Checked += (_, _) => Update();
        _custom.Unchecked += (_, _) => Update();
        _picker.ColorChanged += (_, _) =>
        {
            if (!_updating)
            {
                _primary.Text = new Rgb(_picker.Color.R, _picker.Color.G, _picker.Color.B).Hex;
            }
        };
        Loaded += async (_, _) =>
        {
            await _web.EnsureCoreWebView2Async();
            await AnalyseLogoAsync();
            FromText();
        };
    }

    private void FromText()
    {
        if (Rgb.TryParse(_primary.Text, out var c))
        {
            _updating = true;
            _picker.Color = Windows.UI.Color.FromArgb(255, c.R, c.G, c.B);
            _updating = false;
        }

        Update();
    }

    /// <summary>The proposal's blueprint with the editor's values, as it would be saved.</summary>
    private Blueprints.Blueprint Edited()
    {
        var b = Blueprints.Blueprint.FromJson(_proposal!.Blueprint.ToJson());
        if (Rgb.TryParse(_primary.Text, out var primary))
        {
            b.BrandColor = primary.Hex;
        }

        if (Rgb.TryParse(_background.Text, out var background))
        {
            b.BackgroundColor = background.Hex;
        }

        b.ThemeName = string.IsNullOrWhiteSpace(_name.Text) ? null : _name.Text.Trim();
        b.HeaderLayout = (string)_layout.SelectedItem;
        b.Palette = _custom.IsChecked == true
            ? _slotBoxes.Where(kv => Rgb.TryParse(kv.Value.Text, out _)).ToDictionary(kv => kv.Key, kv => Rgb.Parse(kv.Value.Text).Hex)
            : null;
        return b;
    }

    private void Update()
    {
        if (_updating || _proposal is null)
        {
            return;
        }

        var b = Edited();
        var theme = b.Theme();
        if (_custom.IsChecked != true)
        {
            _updating = true;
            foreach (var (slot, box) in _slotBoxes)
            {
                box.Text = theme.Palette.GetValueOrDefault(slot, box.Text);
            }

            _updating = false;
        }

        _checks.Children.Clear();
        foreach (var c in theme.Checks)
        {
            _checks.Children.Add(Ui.Row(Ui.Badge(c.Passes ? "pass" : "fail", c.Passes ? Ui.Good : Ui.Problem), Ui.Text($"{c.Usage}: {c.Ratio:0.00}:1 (needs {c.Required:0.0})", 12)));
        }

        if (_web.CoreWebView2 is not null)
        {
            var model = MockupModel.FromBlueprint(b).WithLogo(LogoDataUri());
            _web.NavigateToString(MockupRenderer.Render(model));
        }
    }

    private string? LogoDataUri()
    {
        if (_logo is null)
        {
            return null;
        }

        var mime = _logoExtension?.ToLowerInvariant() switch
        {
            ".svg" => "image/svg+xml",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            _ => "image/png",
        };
        return $"data:{mime};base64,{Convert.ToBase64String(_logo)}";
    }

    private void MakeAccessible()
    {
        if (Rgb.TryParse(_primary.Text, out var c) && ThemeGenerator.SuggestAccessiblePrimary(c, Rgb.TryParse(_background.Text, out var bg) ? bg : Rgb.White) is { } better)
        {
            _primary.Text = better.Hex;
        }
    }

    private async Task UploadAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.Window.Handle);
        foreach (var type in new[] { ".png", ".jpg", ".jpeg", ".svg", ".gif" })
        {
            picker.FileTypeFilter.Add(type);
        }

        if (await picker.PickSingleFileAsync() is not { } file)
        {
            return;
        }

        _logo = await File.ReadAllBytesAsync(file.Path);
        _logoExtension = Path.GetExtension(file.Path);
        _logoRemoved = false;
        await AnalyseLogoAsync();
        ShowSuggestions();
        Update();
    }

    private void RemoveLogo()
    {
        _logo = null;
        _logoRemoved = true;
        _logoColors = [];
        _logoImage.Source = null;
        _logoChecks.Children.Clear();
        _logoChecks.Children.Add(Ui.Muted("No logo. Sites show the organization's initials until you add one."));
        Update();
    }

    private async Task AnalyseLogoAsync()
    {
        _logoChecks.Children.Clear();
        if (_logo is null)
        {
            _logoChecks.Children.Add(Ui.Muted("No logo yet. Tip: prepare a wide version (up to 192 x 64) for the header and a square one for the thumbnail, both with a transparent background."));
            return;
        }

        int width = 0, height = 0;
        var transparent = false;
        if (!string.Equals(_logoExtension, ".svg", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = new MemoryStream(_logo);
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
                width = (int)decoder.PixelWidth;
                height = (int)decoder.PixelHeight;
                var pixels = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Straight, new Windows.Graphics.Imaging.BitmapTransform(), Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
                var bytes = pixels.DetachPixelData();
                for (var i = 3; i < bytes.Length; i += 4 * 7)
                {
                    if (bytes[i] < 250)
                    {
                        transparent = true;
                        break;
                    }
                }

                _logoColors = ThemeSuggestions.LogoColors(bytes, width, height);
                var image = new BitmapImage();
                stream.Position = 0;
                await image.SetSourceAsync(stream.AsRandomAccessStream());
                _logoImage.Source = image;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                _logoChecks.Children.Add(Ui.InfoBar("Could not read the image", ex.Message, InfoBarSeverity.Warning));
                return;
            }
        }
        else
        {
            transparent = true;
            _logoImage.Source = null;
        }

        _logoSquare = width > 0 && width == height;
        foreach (var check in ThemeSuggestions.CheckLogo(width, height, _logo.Length, transparent, _logoExtension ?? ".png"))
        {
            var row = Ui.Stack(2, Ui.Row(Ui.Badge(check.Passes ? "ok" : "tip", check.Passes ? Ui.Good : Ui.Watch), Ui.Text(check.Message, 12)));
            if (check.Quote is not null)
            {
                row.Children.Add(Ui.Muted($"Microsoft: “{check.Quote}”"));
            }

            _logoChecks.Children.Add(row);
        }

        if (_logoColors.Count > 0)
        {
            var swatches = Ui.Row([.. _logoColors.Select(c => (UIElement)new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(5), Background = Ui.Brush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B)) })]);
            _logoChecks.Children.Add(Ui.Stack(4, Ui.Muted("Colours in your logo:"), swatches));
        }
    }

    private void ShowSuggestions()
    {
        if (_proposal is null)
        {
            return;
        }

        _suggestions.Children.Clear();
        var brand = Rgb.TryParse(_primary.Text, out var c) ? c : Rgb.Parse(_proposal.Blueprint.BrandColor);
        foreach (var s in ThemeSuggestions.Suggest(_proposal.Blueprint.Organization, brand, _logoColors))
        {
            var p = s.Theme.Palette;
            var swatches = Ui.Row([.. new[] { "themePrimary", "themeDark", "themeLight", "themeLighterAlt", "white", "neutralPrimary" }
                .Where(p.ContainsKey)
                .Select(k => (UIElement)new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Background = Ui.Brush(ToColor(p[k])), BorderThickness = new Thickness(1), BorderBrush = Ui.Resource("CardStrokeColorDefaultBrush") })]);
            var passed = s.Theme.Checks.Count(x => x.Passes);
            var card = Ui.Card(Ui.Stack(4,
                Ui.Row(Ui.Strong(s.Label), Ui.Badge($"{passed}/{s.Theme.Checks.Count} pass", passed == s.Theme.Checks.Count ? Ui.Good : Ui.Watch)),
                swatches,
                Ui.Muted(s.Why),
                Ui.Button("Use this", () =>
                {
                    _custom.IsChecked = false;
                    _background.Text = s.Background.Hex;
                    _primary.Text = s.Primary.Hex;
                    _name.Text = s.Theme.Name;
                }, glyph: Glyphs.Check)));
            _suggestions.Children.Add(card);
        }
    }

    private static Windows.UI.Color ToColor(string hex)
    {
        var c = Rgb.Parse(hex);
        return Windows.UI.Color.FromArgb(255, c.R, c.G, c.B);
    }

    private async Task SaveAsync()
    {
        if (_proposal is null || _folder is null)
        {
            return;
        }

        var edited = Edited();
        if (_logo is not null)
        {
            var assets = Directory.CreateDirectory(Path.Combine(_folder, "assets")).FullName;
            foreach (var old in Directory.GetFiles(assets, "logo.*"))
            {
                File.Delete(old);
            }

            var name = "logo" + (_logoExtension ?? ".png").ToLowerInvariant();
            await File.WriteAllBytesAsync(Path.Combine(assets, name), _logo);
            edited.Logo = "assets/" + name;
            edited.LogoIsSquare = _logoSquare;
        }
        else if (_logoRemoved)
        {
            edited.Logo = null;
        }

        _proposal.Blueprint = edited;
        var notes = _proposal.ThemeNotes.Where(n => !n.StartsWith("Edited in the theme editor", StringComparison.Ordinal)).ToList();
        var theme = edited.Theme();
        notes.Insert(0, $"Edited in the theme editor: \"{theme.Name}\", {theme.Checks.Count(c => c.Passes)} of {theme.Checks.Count} text pairs pass{(edited.Logo is null ? "" : ", with the organization's logo")}.");
        _proposal.ThemeNotes = notes;
        IntranetProposals.Update(_session, _folder, _proposal);
        Implementations.RefreshTheme(_session, _folder, _proposal);
        if (_session.Workspace.Blueprint?.Name == edited.Name)
        {
            _session.Store.SaveBlueprint(edited);
        }

        _status.Text = $"Saved at {DateTime.Now:HH:mm}. The proposal and its implementation steps use \"{theme.Name}\".";
    }
}
