using Microsoft.UI.Xaml;
using Hubwright.Engine;
using Hubwright.Storage;

namespace Hubwright.Desktop;

/// <summary>The app: one window, one session per selected tenant.</summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        // The chosen theme app-wide, before any window, so resources resolve to it (only settable here).
        // System leaves Windows' choice; the window follows a later change at once (MainWindow.ApplyTheme).
        StartupTheme = HubwrightConfig.Load().Desktop.Theme;
        if (StartupTheme is "Light" or "Dark")
        {
            RequestedTheme = StartupTheme == "Dark" ? ApplicationTheme.Dark : ApplicationTheme.Light;
        }

        UnhandledException += (_, e) =>
        {
            Log("unhandled: " + e.Exception);
            e.Handled = true;
        };
    }

    public static new App Current => (App)Application.Current;

    /// <summary>The theme setting the app started with. Menus, drop-downs and tooltips keep it until a restart.</summary>
    public string StartupTheme { get; } = "System";

    /// <summary>Starts Hubwright again on the same tenant and page, then closes this one.</summary>
    public void Restart(string page)
    {
        if (Environment.ProcessPath is { } exe)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { ArgumentList = { "--tenant", Session.Store.Tenant, "--page", page }, UseShellExecute = false });
            Exit();
        }
    }

    /// <summary>The tenant being looked at; pages read and write through <see cref="Session"/>.</summary>
    public HubwrightSession Session { get; private set; } = null!;

    public event Action? SessionChanged;

    public MainWindow Window => _window!;

    /// <summary>The CLI next to the app, so claude/copilot backends can start <c>hubwright mcp</c>.</summary>
    public static string? CliPath
    {
        get
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "hubwright.exe");
            if (File.Exists(beside))
            {
                return beside;
            }

            // A development build: the CLI's own bin folder in the repository.
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var cli = Path.Combine(dir.FullName, "src", "Hubwright.Cli", "bin");
                if (Directory.Exists(cli))
                {
                    return Directory.GetFiles(cli, "hubwright.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                }
            }

            return null;
        }
    }

    /// <summary>A script from tools/: next to the app, or in the repository for a development build.</summary>
    public static string? ToolPath(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tools", name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public static string? FindOnPath(string exe)
        => (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), exe))
            .FirstOrDefault(File.Exists);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var tenants = TenantStore.Tenants();
        var config = HubwrightConfig.Load();
        var start = Environment.GetCommandLineArgs().SkipWhile(a => a != "--tenant").Skip(1).FirstOrDefault()
            ?? config.Tenant.Name ?? tenants.FirstOrDefault() ?? "sample";
        try
        {
            Session = HubwrightSession.Open(start, CliPath);
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            // Nothing else records a failure this early; the app closes right after.
            Log("launch: " + ex);
            throw;
        }
    }

    public void Switch(string tenant)
    {
        Session = HubwrightSession.Open(tenant, CliPath);
        SessionChanged?.Invoke();
    }

    /// <summary>Re-reads config and skills (after Settings or a skills change).</summary>
    public void Reload() => Switch(Session.Store.Tenant);

    public static void Log(string line)
    {
        try
        {
            File.AppendAllText(Runtime.HubwrightHome.PathFor("desktop.log"), $"{DateTimeOffset.Now:O} {line}\n");
        }
        catch (IOException)
        {
        }
    }
}
