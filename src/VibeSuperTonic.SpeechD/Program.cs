using System.Reflection;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.SpeechD;

// vst-speechd — the Speech Dispatcher output module.
//
// speech-dispatcher spawns this as `vst-speechd [configfile]` and talks the
// module protocol over stdin and stdout. It is not a program anybody runs by
// hand, with one exception: --version, because build/pack-tar.sh asks the
// SHIPPED binary its version and refuses an archive whose binaries disagree.
//
// EVERYTHING DIAGNOSTIC GOES TO STDERR. stdout is the protocol; one stray line
// on it desynchronises the server, and the server's response to a module it
// cannot parse is to refuse to start at all — taking every other module,
// including the user's working espeak-ng, down with it. Trap 1's shape, arriving
// from a different direction.

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

// Inside a snap: step aside when the user chooses to apply an update now. See
// SnapUpdateMarker. Speech Dispatcher then uses its fallback voice until it next
// starts this module, which the choice warned about.
if (SnapPeer.Current is not null
    && SnapUpdateMarker.PathIn(Environment.GetEnvironmentVariable("SNAP_USER_COMMON")) is { } marker)
{
    string? own = Path.GetFileName(Environment.GetEnvironmentVariable("SNAP")?.TrimEnd('/'));
    DateTime started = DateTime.UtcNow;
    var watch = new Thread(() =>
    {
        while (true)
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));
            try
            {
                if (File.Exists(marker)
                    && SnapUpdateMarker.ShouldExit(own, File.ReadAllText(marker), File.GetLastWriteTimeUtc(marker), started))
                {
                    Console.Error.WriteLine($"vst-speechd: revision {own} stepping aside for an update the user asked for");
                    Environment.Exit(0);
                }
            }
            catch (IOException) { /* written as we read; look again */ }
            catch (UnauthorizedAccessException) { return; }
        }
    }) { IsBackground = true, Name = "snap-update-marker" };
    watch.Start();
}

using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();

var module = new SpeechdModule(
    stdin,
    stdout,
    new Voices(),
    message => Console.Error.WriteLine("vst-speechd: " + message));

return module.Run();
