using System.Runtime.Versioning;

// This spike drives PulseAudio, so it is Linux-only by construction. Saying so
// at assembly level is what makes CA1416 agree: without it every call into
// PulseAudioSink is "reachable on all platforms" and warns, and 14 warnings
// nobody can act on is how a real one gets missed.
[assembly: SupportedOSPlatform("linux")]
