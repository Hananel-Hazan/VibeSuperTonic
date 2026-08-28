using System.Reflection;
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

using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();

var module = new SpeechdModule(
    stdin,
    stdout,
    new Voices(),
    message => Console.Error.WriteLine("vst-speechd: " + message));

return module.Run();
