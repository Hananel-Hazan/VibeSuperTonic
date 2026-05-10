namespace VibeSuperTonic.Launcher;

internal sealed record Voice(string Id, string DisplayName, string Gender);

internal static class Voices
{
    public const string EngineClsid = "{F2A8C7B1-1234-5678-9ABC-DEF012345678}";
    public const string LanguageHexLcid = "409"; // en-US
    public const string TokenSchemaVersion = "4";

    public static readonly Voice[] All =
    {
        new("M1", "VibeSuperTonic M1", "Male"),
        new("M2", "VibeSuperTonic M2", "Male"),
        new("M3", "VibeSuperTonic M3", "Male"),
        new("M4", "VibeSuperTonic M4", "Male"),
        new("M5", "VibeSuperTonic M5", "Male"),
        new("F1", "VibeSuperTonic F1", "Female"),
        new("F2", "VibeSuperTonic F2", "Female"),
        new("F3", "VibeSuperTonic F3", "Female"),
        new("F4", "VibeSuperTonic F4", "Female"),
        new("F5", "VibeSuperTonic F5", "Female"),
    };

    public static readonly string[] LegacyTokenNames = { "VibeSuperTonic_Spike" };

    public static readonly string[] VoiceTokenRoots =
    {
        @"SOFTWARE\Microsoft\Speech\Voices\Tokens",
        @"SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens",
    };

    public static IEnumerable<string> AllTokenNames()
    {
        foreach (var v in All) yield return $"VibeSuperTonic_{v.Id}";
        foreach (var t in LegacyTokenNames) yield return t;
    }
}
