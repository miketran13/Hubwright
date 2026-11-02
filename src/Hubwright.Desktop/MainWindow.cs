using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Hubwright.Desktop.Pages;
using Hubwright.Storage;

namespace Hubwright.Desktop;

/// <summary>The window: a tenant switcher, navigation grouped by what the admin is doing, and a page.</summary>
public sealed class MainWindow : Window
{
    private static readonly (string Tag, string Title, string Glyph, string? Group)[] Items =
    [
        ("home", "Home", Glyphs.Home, null),
        ("setup", "Setup", Glyphs.Shield, "1 · Connect"),
        ("dashboard", "Score", Glyphs.Dashboard, "2 · Assess"),
        ("findings", "Findings", Glyphs.Warning, "2 · Assess"),
        ("structure", "Structure", Glyphs.Tree, "2 · Assess"),
        ("reports", "Reports", Glyphs.Page, "2 · Assess"),
        ("proposal", "Intranet plan", Glyphs.Sparkle, "3 · Plan"),
        ("designs", "Site designs", Glyphs.Blueprint, "3 · Plan"),
        ("brand", "Brand and look", Glyphs.Palette, "3 · Plan"),
        ("templates", "Site templates", Glyphs.Page, "3 · Plan"),
        ("build", "Build", Glyphs.Rocket, "4 · Build"),
        ("secure", "Security", Glyphs.Shield, "5 · Secure"),
        ("baseline", "Baseline", Glyphs.Check, "5 · Secure"),
        ("monitor", "Drift and trend", Glyphs.Dashboard, "6 · Run"),
        ("owners", "Owners", Glyphs.People, "6 · Run"),
        ("history", "History", Glyphs.History, "6 · Run"),
        ("proposals", "AI suggestions", Glyphs.Inbox, "Library"),
        ("skills", "Best practices", Glyphs.Library, "Library"),
    ];

    /// <summary>Each page's colour icon (Assets/Icons); pages that share a glyph get different icons here.</summary>
    private static readonly Dictionary<string, string> NavIcons = new(StringComparer.Ordinal)
    {
        ["home"] = "home", ["setup"] = "lock_shield", ["dashboard"] = "data_pie", ["findings"] = "alert_badge",
        ["structure"] = "apps_list", ["reports"] = "document", ["proposal"] = "lightbulb_filament", ["designs"] = "board",
        ["brand"] = "image", ["templates"] = "notebook", ["build"] = "clipboard_task", ["secure"] = "shield_checkmark",
        ["baseline"] = "checkmark_circle", ["monitor"] = "data_trending", ["owners"] = "people", ["history"] = "history",
        ["proposals"] = "mail", ["skills"] = "library", ["ask"] = "chat", ["audit"] = "receipt", ["settings"] = "settings",
    };

    private static IconElement Icon(string tag, string glyph) => Ui.NavIcon(NavIcons.GetValueOrDefault(tag, ""), glyph);

    // Not a page: one click switches between light and dark (the same setting as Settings → Appearance).
    private readonly NavigationViewItem _themeItem = new() { Tag = "theme", SelectsOnInvoked = false };
    private Grid _root = new();
    private Border? _busy;

    private readonly NavigationView _nav;
    private readonly ContentControl _host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly ComboBox _tenants = new() { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
    private string _current = "home";

    // Ask: a side pane that stays open while you move between pages.
    private readonly Border _askPane = new() { Width = 460, Visibility = Visibility.Collapsed, BorderThickness = new Thickness(1, 0, 0, 0) };
    private bool _switching;

    public MainWindow()
    {
        Title = "Hubwright";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Hubwright.ico"));
        SystemBackdrop = new MicaBackdrop();
        _nav = new NavigationView
        {
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            OpenPaneLength = 220,
        };
        var body = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() },
        };
        _busy = Busy();
        body.Children.Add(_busy);
        Grid.SetRow(_host, 1);
        body.Children.Add(_host);
        Grid.SetRowSpan(_askPane, 2);
        _askPane.BorderBrush = Pages.Ui.Resource("CardStrokeColorDefaultBrush");
        Grid.SetColumn(_askPane, 1);
        body.Children.Add(_askPane);
        _nav.Content = body;

        string? group = null;
        foreach (var (tag, title, glyph, itemGroup) in Items)
        {
            if (itemGroup is not null && itemGroup != group)
            {
                _nav.MenuItems.Add(new NavigationViewItemHeader { Content = itemGroup });
            }

            group = itemGroup;
            _nav.MenuItems.Add(new NavigationViewItem { Content = title, Tag = tag, Icon = Icon(tag, glyph) });
        }

        _nav.FooterMenuItems.Add(_themeItem);
        _nav.FooterMenuItems.Add(new NavigationViewItem { Content = "Ask AI", Tag = "ask", Icon = Icon("ask", Glyphs.Chat), SelectsOnInvoked = false });
        _nav.FooterMenuItems.Add(new NavigationViewItem { Content = "Audit log", Tag = "audit", Icon = Icon("audit", Glyphs.History) });
        _nav.ItemInvoked += (_, e) =>
        {
            if (e.InvokedItemContainer?.Tag is "ask")
            {
                ToggleAsk();
            }
            else if (ReferenceEquals(e.InvokedItemContainer, _themeItem))
            {
                ToggleTheme();
            }
        };
        _nav.FooterMenuItems.Add(new NavigationViewItem { Content = "Settings", Tag = "settings", Icon = Icon("settings", Glyphs.Settings) });
        _nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItemContainer?.Tag is string tag && tag != _current && tag != MenuTag(_current))
            {
                Show(tag);
            }
        };

        var bar = new Grid { Padding = new Thickness(16, 8, 140, 8), ColumnSpacing = 12, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        bar.Children.Add(Ui.Row(new TextBlock { Text = "🏗️", FontSize = 18 }, new TextBlock { Text = "Hubwright", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }));
        var right = Ui.Row(new TextBlock { Text = "Tenant", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 }, _tenants);
        Grid.SetColumn(right, 2);
        bar.Children.Add(right);
        _tenants.SelectionChanged += (_, _) =>
        {
            if (!_switching && _tenants.SelectedItem is string tenant && tenant != App.Current.Session.Store.Tenant)
            {
                App.Current.Switch(tenant);
            }
        };

        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetTitleBar(bar);

        _root = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
        _root.Children.Add(bar);
        Grid.SetRow(_nav, 1);
        _root.Children.Add(_nav);
        Content = _root;
        _root.ActualThemeChanged += (_, _) => ThemeChanged();
        Ui.Theme = Ui.ParseTheme(App.Current.Session.Config.Desktop.Theme);
        _root.RequestedTheme = Ui.Theme;
        Ui.ActualTheme = _root.ActualTheme == ElementTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
        CaptionColors();
        ThemeItem();
        App.Log($"theme: {App.Current.Session.Config.Desktop.Theme}, showing {Ui.ActualTheme}, started {Application.Current.RequestedTheme} · {Ui.ThemeProbe()}");

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 920));
        App.Current.SessionChanged += () =>
        {
            // The pane works on the open tenant; a new one starts fresh.
            _askPane.Child = null;
            _askPane.Visibility = Visibility.Collapsed;
            FillTenants();
            Show(_current);
        };
        FillTenants();
        var page = Environment.GetCommandLineArgs().SkipWhile(a => a != "--page").Skip(1).FirstOrDefault();
        Show(page ?? "home");
    }

    public IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>Light, dark or Windows' choice (Settings → Appearance), applied to the window at once.</summary>
    public void ApplyTheme(string theme)
    {
        Ui.Theme = Ui.ParseTheme(theme);
        _root.RequestedTheme = Ui.Theme;
        ThemeChanged();
    }

    /// <summary>The navigation switch: to the other theme from the one showing, saved as the Theme setting.</summary>
    private void ToggleTheme()
    {
        var config = HubwrightConfig.Load();
        config.Desktop.Theme = _root.ActualTheme == ElementTheme.Dark ? "Light" : "Dark";
        config.Save();
        App.Log($"theme: {config.Desktop.Theme} (navigation switch)");
        App.Current.Session.Config.Desktop.Theme = config.Desktop.Theme;
        ApplyTheme(config.Desktop.Theme);
    }

    /// <summary>
    /// The theme in effect changed (a setting, the switch, or Windows): the caption buttons, the switch
    /// and the page, which was drawn with the old theme's brushes, follow it.
    /// </summary>
    private void ThemeChanged()
    {
        var actual = _root.ActualTheme == ElementTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
        CaptionColors();
        ThemeItem();
        if (actual == Ui.ActualTheme)
        {
            return;
        }

        Ui.ActualTheme = actual;
        App.Log($"theme: now showing {actual} · {Ui.ThemeProbe()}");
        _askPane.BorderBrush = Ui.Resource("CardStrokeColorDefaultBrush");
        if (_busy is not null)
        {
            _busy.Background = Ui.Resource("LayerFillColorDefaultBrush");
            _busy.BorderBrush = Ui.Resource("CardStrokeColorDefaultBrush");
        }

        Show(_current);
    }

    /// <summary>The switch offers the other theme: the sun while dark, the moon while light.</summary>
    private void ThemeItem()
    {
        var dark = _root.ActualTheme == ElementTheme.Dark;
        _themeItem.Content = dark ? "Light mode" : "Dark mode";
        _themeItem.Icon = Ui.NavIcon(dark ? "weather_sunny" : "weather_moon", dark ? "" : "");
        ToolTipService.SetToolTip(_themeItem, dark ? "Switch to the light theme" : "Switch to the dark theme");
    }

    /// <summary>Minimise, maximise and close follow the theme; on a dark title bar the default black ones vanish.</summary>
    private void CaptionColors()
    {
        var dark = _root.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(255, 150, 150, 150) : Windows.UI.Color.FromArgb(255, 120, 120, 120);
        bar.ButtonHoverForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(40, 255, 255, 255) : Windows.UI.Color.FromArgb(25, 0, 0, 0);
        bar.ButtonPressedForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(60, 255, 255, 255) : Windows.UI.Color.FromArgb(45, 0, 0, 0);
    }

    /// <summary>
    /// The strip above the page while something runs: a moving bar, the action, its latest progress
    /// line and how long it has taken. Hidden when nothing runs; it stays across page changes.
    /// </summary>
    private static Border Busy()
    {
        var label = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var detail = new TextBlock { FontSize = 12, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var elapsed = new TextBlock { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        var line = new Grid { ColumnSpacing = 10, Padding = new Thickness(16, 6, 16, 6), ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        line.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16, MinWidth = 16, MinHeight = 16 });
        Grid.SetColumn(label, 1);
        line.Children.Add(label);
        Grid.SetColumn(detail, 2);
        line.Children.Add(detail);
        Grid.SetColumn(elapsed, 3);
        line.Children.Add(elapsed);
        var strip = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = Ui.Resource("LayerFillColorDefaultBrush"),
            BorderBrush = Ui.Resource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel { Children = { new ProgressBar { IsIndeterminate = true }, line } },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(label, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Update()
        {
            if (Activity.Current is not { } now)
            {
                strip.Visibility = Visibility.Collapsed;
                timer.Stop();
                return;
            }

            label.Text = now.Label + "…";
            detail.Text = now.Others > 0 ? $"{now.Detail}{(now.Detail is null ? "" : " · ")}and {now.Others} more running" : now.Detail ?? "";
            var seconds = (int)(DateTimeOffset.Now - now.Started).TotalSeconds;
            elapsed.Text = seconds < 2 ? "" : seconds < 60 ? $"{seconds} s" : $"{seconds / 60} min {seconds % 60:00} s";
            strip.Visibility = Visibility.Visible;
            if (!timer.IsEnabled)
            {
                timer.Start();
            }
        }

        timer.Tick += (_, _) => Update();
        Activity.Changed += Update;
        return strip;
    }

    public void FillTenants()
    {
        _switching = true;
        _tenants.Items.Clear();
        var current = App.Current.Session.Store.Tenant;
        foreach (var tenant in TenantStore.Tenants().Append(current).Distinct(StringComparer.Ordinal))
        {
            _tenants.Items.Add(tenant);
        }

        _tenants.SelectedItem = current;
        _switching = false;
    }

    public void Show(string tag, string? parameter = null)
    {
        _current = tag;
        UIElement page;
        try
        {
            page = tag switch
            {
                "setup" => new SetupPage(),
                "secure" => SecurePage.Render(),
                "monitor" => MonitorPage.Render(),
                "owners" => OwnersPage.Render(),
                "history" => HistoryPage.Render(),
                "reports" => ReportsPage.Render(),
                "facts" => FactsPage.Render(),
                "proposal" => new ProposalPage(),
                "build" => new ImplementPage(parameter),
                "baseline" => new ImplementPage(null, security: true),
                "brand" or "theme-editor" => new BrandPage("theme-editor", parameter),
                "templates" => new TemplatesPage(),
                "designs" => new SiteDesignsPage(parameter),
                "audit" => AuditPage.Render(),
                "dashboard" => DashboardPage.Render(),
                "findings" => FindingsPage.Render(parameter),
                "structure" => StructurePage.Render(),
                "blueprint" => BlueprintPage.Render(),
                "preview" => new BrandPage("preview", parameter),
                "ask" => new AskPage(),
                "start" => HomePage.Render(),
                "proposals" => ProposalsPage.Render(),
                "skills" => SkillsPage.Render(),
                "settings" => SettingsPage.Render(),
                _ => HomePage.Render(),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            App.Log($"page {tag}: {ex}");
            page = Ui.Page(Ui.InfoBar("This page could not load", ex.Message, InfoBarSeverity.Error));
        }

        _host.Content = page;
        if (_nav.IsLoaded)
        {
            Select(tag);
        }
        else
        {
            // Selecting before the NavigationView is loaded throws ELEMENT_NOT_FOUND (seen in Release builds).
            _nav.Loaded += OnLoaded;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            _nav.Loaded -= OnLoaded;
            Select(_current);
        }
    }

    /// <summary>The menu item a page lives under (pages reached from other pages have no item of their own).</summary>
    private static string MenuTag(string tag) => tag switch
    {
        "theme-editor" or "preview" => "brand",
        "blueprint" => "proposal",
        "facts" => "settings",
        "start" => "home",
        _ => tag,
    };

    public void ToggleAsk()
    {
        if (_askPane.Visibility == Visibility.Visible)
        {
            _askPane.Visibility = Visibility.Collapsed;
            return;
        }

        _askPane.Child ??= new Pages.AskPage();
        _askPane.Visibility = Visibility.Visible;
    }

    private void Select(string tag)
    {
        tag = MenuTag(tag);
        var item = _nav.MenuItems.Concat(_nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null && !ReferenceEquals(_nav.SelectedItem, item))
        {
            _nav.SelectedItem = item;
        }
    }
}
