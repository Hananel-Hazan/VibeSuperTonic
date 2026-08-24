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

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
