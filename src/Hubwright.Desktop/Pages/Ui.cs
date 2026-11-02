using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Hubwright.Rules;
using Windows.UI;

namespace Hubwright.Desktop.Pages;

/// <summary>Small builders: pages are composed in code with one look (Fluent cards, glyphs, badges).</summary>
public static class Ui
{
    public static readonly Color Brand = Color.FromArgb(255, 107, 79, 216);
    public static readonly Color Good = Color.FromArgb(255, 34, 165, 90);
    public static readonly Color Watch = Color.FromArgb(255, 224, 138, 0);
    public static readonly Color Problem = Color.FromArgb(255, 229, 72, 77);
    public static readonly Color Info = Color.FromArgb(255, 15, 108, 189);
    public static readonly Color Neutral = Color.FromArgb(255, 138, 136, 134);

    /// <summary>
    /// A theme brush (TextFillColorSecondaryBrush, CardStrokeColorDefaultBrush…). The plain indexer can
    /// throw during launch in a Release build before the Fluent theme dictionaries answer, so this walks
    /// them itself and falls back to a neutral colour rather than crash.
    /// </summary>
    public static Brush Resource(string key)
    {
        if (Brushes.TryGetValue((key, ActualTheme), out var cached))
        {
            return cached;
        }

        var brush = Lookup(key) as Brush ?? Brush(Neutral);
        Brushes[(key, ActualTheme)] = brush;
        return brush;
    }

    /// <summary>A style from the Fluent resources, or null (the control keeps its default style).</summary>
    public static Style? Style(string key) => Lookup(key) as Style;

    /// <summary>The chosen theme (Settings → Appearance, or the switch in the navigation); Default follows Windows. Dialogs take it too.</summary>
    public static ElementTheme Theme { get; set; } = ElementTheme.Default;

    /// <summary>The theme the window shows right now, Light or Dark (set by the window when it changes).</summary>
    public static ElementTheme ActualTheme { get; set; } = ElementTheme.Light;

    /// <summary>What a card's fill resolves to in each theme, for desktop.log: proof the dark lookup isn't the light colour.</summary>
    public static string ThemeProbe()
    {
        static string Hex(object? brush) => brush is SolidColorBrush b ? $"#{b.Color.A:X2}{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : "none";
        return $"card fill light {Hex(Themed(Application.Current.Resources, "CardBackgroundFillColorDefaultBrush", "Light"))}, dark {Hex(Themed(Application.Current.Resources, "CardBackgroundFillColorDefaultBrush", "Dark"))}";
    }

    public static ElementTheme ParseTheme(string? theme) => theme?.ToLowerInvariant() switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    // UI-thread only, like every element.
    private static readonly Dictionary<(string, ElementTheme), Brush> Brushes = [];

    /// <summary>
    /// Pages are built in code, so a plain lookup answers for the theme the app started with. After a
    /// live switch the window shows the other theme: then read that theme's dictionaries first.
    /// </summary>
    private static object? Lookup(string key)
    {
        var started = Application.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
        var theme = ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        if (ActualTheme != started && Themed(Application.Current.Resources, key, theme) is { } switched)
        {
            return switched;
        }

        return Find(Application.Current.Resources, key, theme);
    }

    /// <summary>
    /// A key from the theme's own dictionary only (no plain lookup, which would use the startup theme).
    /// WinUI keys its dark dictionary "Default", not "Dark" (Themes/generic.xaml): without it a dark
    /// window got the light theme's 70% white card fill, grey on the dark backdrop.
    /// </summary>
    private static object? Themed(ResourceDictionary dictionary, string key, string theme)
    {
        foreach (var name in theme == "Dark" ? new[] { "Dark", "Default" } : ["Light"])
        {
            if (!dictionary.ThemeDictionaries.TryGetValue(name, out var themed) || themed is not ResourceDictionary t)
            {
                continue;
            }

            // A theme brush takes its colour from the startup theme whichever dictionary holds it, but
            // the colours themselves are plain values per theme (CardBackgroundFillColorDefault for
            // CardBackgroundFillColorDefaultBrush): build the brush from this theme's colour.
            if (key.EndsWith("Brush", StringComparison.Ordinal) && t.TryGetValue(key[..^"Brush".Length], out var color) && color is Color c)
            {
                return new SolidColorBrush(c);
            }

            if (t.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (Themed(merged, key, theme) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static object? Find(ResourceDictionary dictionary, string key, string theme)
    {
        if (dictionary.TryGetValue(key, out var value))
        {
            return value;
        }

        foreach (var name in new[] { theme, "Default" })
        {
            if (dictionary.ThemeDictionaries.TryGetValue(name, out var themed) && themed is ResourceDictionary t && Find(t, key, theme) is { } found)
            {
                return found;
            }
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (Find(merged, key, theme) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    public static SolidColorBrush Brush(Color color) => new(color);

    /// <summary>A Segoe Fluent glyph in the colour of what it does (<see cref="GlyphColor"/>), or in <paramref name="color"/>.</summary>
    public static FontIcon Glyph(string glyph, double size = 16, Color? color = null)
        => new() { Glyph = glyph, FontSize = size, Foreground = (color ?? GlyphColor(glyph)) is { } c ? Brush(c) : null };

    /// <summary>
    /// Button and inline glyphs are coloured by what they do, to match the colour icons: confirm, add
    /// and download green; cancel red; open, save, refresh and upload blue; the AI violet; warnings
    /// amber. Null (the text colour) for the rest. The colours read on light and dark backgrounds.
    /// </summary>
    public static Color? GlyphColor(string glyph) => glyph switch
    {
        Glyphs.Check or Glyphs.Download => Good,
        Glyphs.Cancel => Problem,
        Glyphs.Open or Glyphs.Save or Glyphs.Refresh or Glyphs.Upload or Glyphs.Page or Glyphs.History => Info,
        Glyphs.Sparkle or Glyphs.Chat or Glyphs.Blueprint or Glyphs.Tree => Brand,
        Glyphs.Warning or Glyphs.Palette or Glyphs.Rocket => Watch,
        Glyphs.Shield or Glyphs.People => Good,
        _ => null,
    };

    /// <summary>The colour icon (Assets/Icons) for a glyph, for page headers and the navigation; null when there is none.</summary>
    public static string? IconFor(string glyph) => glyph switch
    {
        Glyphs.Home => "home",
        Glyphs.Shield => "shield_checkmark",
        Glyphs.Dashboard => "data_pie",
        Glyphs.Warning => "alert_badge",
        Glyphs.Tree => "apps_list",
        Glyphs.Page => "document",
        Glyphs.Sparkle => "lightbulb_filament",
        Glyphs.Blueprint => "board",
        Glyphs.Palette => "image",
        Glyphs.Rocket => "clipboard_task",
        Glyphs.Check => "checkmark_circle",
        Glyphs.People => "people",
        Glyphs.History => "history",
        Glyphs.Inbox => "mail",
        Glyphs.Library => "library",
        Glyphs.Chat => "chat",
        Glyphs.Settings => "settings",
        _ => null,
    };

    /// <summary>A colour icon from Assets/Icons (Fluent UI System Icons, colour style, MIT).</summary>
    public static Image Icon(string name, double size = 20)
        => new() { Source = Svg(name), Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>The same icon for a navigation item or a menu entry; the glyph when the icon file is missing.</summary>
    public static IconElement NavIcon(string name, string fallbackGlyph)
        => File.Exists(IconPath(name)) ? new ImageIcon { Source = Svg(name) } : new FontIcon { Glyph = fallbackGlyph };

    private static readonly Dictionary<string, Microsoft.UI.Xaml.Media.Imaging.SvgImageSource> Sources = new(StringComparer.OrdinalIgnoreCase);

    private static string IconPath(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", name + ".svg");

    /// <summary>
    /// One decoded source per icon for the life of the app, rasterized small: a source per image, made
    /// on every render, keeps large native SVG documents alive. Loaded from a stream (the app is
    /// unpackaged, so ms-appx URIs don't resolve); a missing or broken file is an empty image.
    /// </summary>
    private static Microsoft.UI.Xaml.Media.Imaging.SvgImageSource Svg(string name)
    {
        if (Sources.TryGetValue(name, out var shared))
        {
            return shared;
        }

        var source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource { RasterizePixelWidth = 64, RasterizePixelHeight = 64 };
        if (File.Exists(IconPath(name)))
        {
            _ = LoadAsync(source, File.ReadAllBytes(IconPath(name)));
        }

        Sources[name] = source;
        return source;
    }

    private static async Task LoadAsync(Microsoft.UI.Xaml.Media.Imaging.SvgImageSource source, byte[] bytes)
    {
        try
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            await source.SetSourceAsync(stream);
        }
        catch (Exception ex)
        {
            // Decoration only: a broken icon never breaks a page.
            App.Log("icon: " + ex.Message);
        }
    }

    public static ScrollViewer Page(params UIElement[] children)
        => new() { Content = Stack(14, children).With(s => s.Padding = new Thickness(32, 24, 32, 40)), HorizontalScrollMode = ScrollMode.Disabled };

    public static StackPanel Stack(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    /// <summary>Children side by side, wrapping to the next line when the column is too narrow.</summary>
    public static WrapRow Row(params UIElement[] children)
    {
        var panel = new WrapRow { VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children)
        {
            if (child is FrameworkElement { VerticalAlignment: VerticalAlignment.Stretch } element)
            {
                element.VerticalAlignment = VerticalAlignment.Center;
            }

            panel.Children.Add(child);
        }

        return panel;
    }

    /// <summary>
    /// Lines a row up on its bottom edge: a labelled field (Header above the box) next to buttons
    /// otherwise centres the buttons on the label instead of the box.
    /// </summary>
    public static T BottomAligned<T>(this T row) where T : Panel
    {
        ArgumentNullException.ThrowIfNull(row);
        row.VerticalAlignment = VerticalAlignment.Bottom;
        foreach (var child in row.Children.OfType<FrameworkElement>())
        {
            child.VerticalAlignment = VerticalAlignment.Bottom;
        }

        return row;
    }

    public static Grid Header(string glyph, string title, string? subtitle = null)
    {
        var text = Stack(2, Title(title));
        if (subtitle is not null)
        {
            text.Children.Add(Muted(subtitle));
        }

        var grid = new Grid { ColumnSpacing = 14, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
        // The page's colour icon, as in the navigation; a white glyph on the brand tile when there is none.
        UIElement icon = IconFor(glyph) is { } name && File.Exists(IconPath(name))
            ? Icon(name, 40).With(i => i.VerticalAlignment = VerticalAlignment.Top)
            : new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(10), Background = Brush(Brand), Child = Glyph(glyph, 22, Colors.White).With(g => g.HorizontalAlignment = HorizontalAlignment.Center) };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    public static TextBlock Title(string text) => new() { Text = text, Style = Style("TitleTextBlockStyle") };

    public static TextBlock Heading(string text) => new() { Text = text, Style = Style("SubtitleTextBlockStyle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

    public static TextBlock Strong(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };

    public static TextBlock Text(string text, double size = 14) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, IsTextSelectionEnabled = true };

    public static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Resource("TextFillColorSecondaryBrush"), IsTextSelectionEnabled = true };

    public static Border Card(UIElement content, Color? edge = null) => new()
    {
        Child = content,
        Padding = new Thickness(16),
        CornerRadius = new CornerRadius(8),
        Background = Resource("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = edge is null ? Resource("CardStrokeColorDefaultBrush") : Brush(edge.Value),
        BorderThickness = new Thickness(edge is null ? 1 : 2),
    };

    public static Border Badge(string text, Color color) => new()
    {
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush(Colors.White) },
        Background = Brush(color),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(8, 2, 8, 2),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Color SeverityColor(Severity severity) => severity switch
    {
        Severity.Critical or Severity.High => Problem,
        Severity.Medium => Watch,
        Severity.Low => Info,
        _ => Neutral,
    };

    public static Color ScoreColor(double? score) => score switch
    {
        null => Neutral,
        >= 80 => Good,
        >= 60 => Watch,
        _ => Problem,
    };

    public static Button Button(string text, Action onClick, bool accent = false, string? glyph = null)
    {
        // A coloured glyph, except on an accent button (it would clash with the accent fill).
        var icon = glyph is null ? null : accent ? new FontIcon { Glyph = glyph, FontSize = 14 } : Glyph(glyph, 14);
        var button = new Button { Content = icon is null ? text : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, new TextBlock { Text = text } } } };
        if (accent)
        {
            button.Style = Style("AccentButtonStyle");
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// A button for async work: while it runs, a spinner replaces its icon, it is disabled, and the
    /// strip at the top of the page names the action (<see cref="Activity"/>). Errors show in a dialog.
    /// </summary>
    public static Button Button(string text, Func<Task> onClick, bool accent = false, string? glyph = null)
    {
        Button? button = null;

        // The cast picks the Action overload; an async lambda alone would bind to this method again and recurse forever.
        button = Button(text, (Action)(async () =>
        {
            var content = button!.Content;
            button.IsEnabled = false;
            button.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new ProgressRing { IsActive = true, Width = 14, Height = 14, MinWidth = 14, MinHeight = 14 }, new TextBlock { Text = text } },
            };
            using var running = Activity.Begin(text);
            try
            {
                await onClick();
            }
            catch (Exception ex)
            {
                App.Log(ex.ToString());
                running.Dispose();
                button.Content = content;
                await Message(button.XamlRoot, "Something went wrong", ex.Message);
            }
            finally
            {
                button.Content = content;
                button.IsEnabled = true;
            }
        }), accent, glyph);
        return button;
    }

    public static InfoBar InfoBar(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
        => new() { Title = title, Message = message, Severity = severity, IsOpen = true, IsClosable = false };

    public static Grid Columns(int count, IEnumerable<UIElement> items, double gap = 12)
    {
        var grid = new Grid { ColumnSpacing = gap, RowSpacing = gap };
        for (var i = 0; i < count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        var index = 0;
        foreach (var item in items)
        {
            if (index % count == 0)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            Grid.SetRow((FrameworkElement)item, index / count);
            Grid.SetColumn((FrameworkElement)item, index % count);
            grid.Children.Add(item);
            index++;
        }

        return grid;
    }

    public static Border Tile(string label, string value, Color color, string? hint = null)
    {
        var stack = Stack(2, Muted(label), new TextBlock { Text = value, FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Brush(color) });
        if (hint is not null)
        {
            stack.Children.Add(Muted(hint));
        }

        return Card(stack);
    }

    public static HyperlinkButton Link(string text, string url)
    {
        var link = new HyperlinkButton { Content = text, Padding = new Thickness(0) };
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            link.NavigateUri = uri;
        }

        return link;
    }

    public static TextBox Field(string header, string text = "", string? placeholder = null)
        => new() { Header = header, Text = text, PlaceholderText = placeholder ?? "" };

    /// <summary>A multi-line box: AcceptsReturn before Text, or WinUI keeps only the first line.</summary>
    public static TextBox Area(string header, string text, double height = 160)
    {
        var box = new TextBox { Header = header, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = height, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        box.Text = text;
        return box;
    }

    public static ComboBox Choice(string header, IEnumerable<string> items, string? selected)
    {
        var box = new ComboBox { Header = header, MinWidth = 220 };
        foreach (var item in items)
        {
            box.Items.Add(item);
        }

        box.SelectedItem = selected is not null && box.Items.Contains(selected) ? selected : box.Items.FirstOrDefault();
        return box;
    }

    public static async Task Message(XamlRoot? root, string title, string text)
    {
        if (root is null)
        {
            return;
        }

        await new ContentDialog { RequestedTheme = Theme, Title = title, Content = new ScrollViewer { Content = Text(text), MaxHeight = 480 }, CloseButtonText = "OK", XamlRoot = root }.ShowAsync();
    }

    public static async Task<bool> Confirm(XamlRoot root, string title, string text, string action)
        => await new ContentDialog { RequestedTheme = Theme, Title = title, Content = Text(text), PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = root }.ShowAsync() == ContentDialogResult.Primary;

    public static string Score(double? score) => score is null ? "–" : Math.Floor(score.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static T With<T>(this T element, Action<T> configure)
    {
        configure(element);
        return element;
    }

    public static void Open(string pathOrUrl)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pathOrUrl) { UseShellExecute = true })?.Dispose();
}

/// <summary>Segoe Fluent Icons glyphs used by the pages.</summary>
public static class Glyphs
{
    public const string History = "";
    public const string Page = "";

    public const string Home = "";
    public const string Rocket = "";
    public const string Dashboard = "";
    public const string Warning = "";
    public const string Tree = "";
    public const string Blueprint = "";
    public const string Palette = "";
    public const string Library = "";
    public const string Chat = "";
    public const string Inbox = "";
    public const string Settings = "";
    public const string Refresh = "";
    public const string Save = "";
    public const string Open = "";
    public const string Check = "";
    public const string Cancel = "";
    public const string Sparkle = "";
    public const string Download = "";
    public const string Upload = "";
    public const string People = "";
    public const string Shield = "";
}
