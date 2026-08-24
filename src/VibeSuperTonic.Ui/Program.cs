using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace VibeSuperTonic.Ui;

// vibesupertonic-ui — the Reader window.
//
//   vibesupertonic-ui
//
// Nothing autostarts this. It runs when the user opens it and not before; the
// daemon comes up on the first hotkey press, or on this window connecting, and
// the tray icon belongs to the daemon because it has to exist while this is
// closed.
//
// Killing this leaves speech running, which is now a tautology rather than a
// design goal: it is a different process.
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Answered before Avalonia is touched, so it works with no display —
        // build/pack-tar.sh asks all three binaries their version and refuses to
        // archive a tree whose parts disagree, and a packer that needed an X
        // server to check that would be useless on a build machine.
        if (args.Contains("--version"))
        {
            Console.WriteLine(
                Assembly.GetEntryAssembly()
                    ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
                    ?.Split('+')[0]
                    ?? "unknown");
            return 0;
        }

        EnsureHotkeyClient();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Under an AppImage, make sure the copy of <c>vst-ctl</c> the hotkeys point
    /// at is this build's.
    ///
    /// <para><b>Why the window does this at all.</b> Upgrading an AppImage is
    /// replacing one file, and nothing in that gesture touches
    /// <c>~/.local/bin</c> — so the client can be a release behind while every
    /// press still appears to work. The daemon repairs it on its own start, but
    /// the daemon starts *from a press*, and the case that needs repairing most
    /// is a press that does nothing. Opening the window is what a person does
    /// next, so opening the window has to be a second way in.</para>
    ///
    /// <para>Delegated to the daemon binary rather than reimplemented here: one
    /// copy rule, one version comparison, one place to be wrong. It costs a
    /// process start of a few milliseconds, before any window exists, and it
    /// cannot fail in a way that matters — the flag exits 0 having done nothing
    /// on every install that is not an AppImage.</para>
    /// </summary>
    private static void EnsureHotkeyClient()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPIMAGE"))) return;

        try
        {
            string daemon = Path.Combine(AppContext.BaseDirectory, "vibesupertonicd");
            if (!File.Exists(daemon)) return;

            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(daemon)
            {
                ArgumentList = { "--ensure-client" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return;

            string said = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return; }
            if (said.Length > 0) Console.WriteLine(said);
        }
        catch
        {
            // A convenience copy is never a reason to fail to open a window.
        }
    }

    // Named and public-shaped because Avalonia's tooling looks for it.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}

internal sealed class App : Application
{
    private readonly DaemonClient _client = new();
    private readonly CancellationTokenSource _stopping = new();

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(_client);

            // The subscription outlives no window here — closing the window ends
            // the process, and the daemon keeps speaking. Cancel on exit so the
            // reconnect loop does not hold the process open after the window has
            // gone.
            desktop.ShutdownRequested += (_, _) => _stopping.Cancel();

            _ = Task.Run(() => _client.RunAsync(_stopping.Token));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
