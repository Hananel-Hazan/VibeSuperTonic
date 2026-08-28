#!/usr/bin/env python3
"""Break each rule S3 added, one at a time, and check a test notices.

A mutation that leaves the suite GREEN is a rule nothing defends."""
import subprocess, sys, os

ROOT = "/home/hananel/#GitRepos/VibeSuperTonic"
VL = "src/VibeSuperTonic.SpeechD/VoiceList.cs"
SM = "src/VibeSuperTonic.SpeechD/SpeechdModule.cs"
VC = "src/VibeSuperTonic.SpeechD/Voices.cs"
IV = "src/VibeSuperTonic.SpeechD/InstalledVoices.cs"

# (label, [(file, find, replace), ...])
MUTATIONS = [
 ("supertonic listed once instead of per language",
  [(VL, "foreach (string language in supertonicLanguages)",
       "foreach (string language in supertonicLanguages.Take(1))")]),
 ("row name drops the language",
  [(VL, 'Name: SupertonicNamePrefix + style + "-" + lang,',
       'Name: SupertonicNamePrefix + style,')]),
 ("style name no longer implies gender",
  [(VL, "'M' => VoiceGender.Male,", "'M' => VoiceGender.Unknown,")]),
 ("rank reads the digit instead of counting what is installed",
  [(VL, "VoiceGender.Male => ++males,", "VoiceGender.Male => style[1] - '0',")]),
 ("piper language hardcoded",
  [(VL, "return NormaliseLanguage(dash <= 0 ? id : id[..dash]);", 'return "en";')]),
 ("piper quality dropped from the variant",
  [(VL, 'return dash < 0 || dash == id.Length - 1 ? "piper" : id[(dash + 1)..];',
       'return "piper";')]),
 ("an exact name no longer wins",
  [(VL, "if (named is not null) return named;", "if (named is not null) { }")]),
 ("a language on its own selects a voice again",
  [(VL, "if (ParseSymbolic(symbolic) is not { } request) return null;",
       "if (ParseSymbolic(symbolic) is not { } request) return ByLanguage(voices, NormaliseLanguage(language)).FirstOrDefault();")]),
 ("a rank above what is installed refuses",
  [(VL, "?? pool.OrderByDescending(v => v.Rank).First();", "?? null;")]),
 ("a known gender is no longer preferred",
  [(VL, "var pool = gendered.Count > 0 ? gendered : candidates;", "var pool = candidates;")]),
 ("child_female is refused",
  [(VL, '"child_female" => (VoiceGender.Female, 1),', '"child_female" => null,')]),
 ("an exact region no longer beats a related one",
  [(VL, "if (exact.Count > 0) return exact;", "if (exact.Count > 99999) return exact;")]),
 ("language normalisation dropped",
  [(VL, """(language ?? "").Trim().ToLowerInvariant().Replace('_', '-')""",
       """(language ?? "").Trim()""")]),
 ("the echo row is no longer listed",
  [(SM, 'Reply($"200-{EchoVoiceName}\\ten\\techo");', "")]),
 ("voice rows separated by spaces instead of tabs",
  [(SM, 'Reply($"200-{v.Name}\\t{v.Language}\\t{v.Variant}");',
       'Reply($"200-{v.Name} {v.Language} {v.Variant}");')]),
 ("choosing the echo row no longer routes to espeak",
  [(SM, "bool echoChosen = string.Equals(\n            _synthesisVoice, EchoVoiceName, StringComparison.OrdinalIgnoreCase);",
       "bool echoChosen = false;")]),
 ("the resolved language never reaches the renderer",
  [(SM, "() => _voices.StartNeural(text, pick?.RenderVoice, pick?.RenderLanguage), \"neural\");",
       "() => _voices.StartNeural(text, pick?.RenderVoice, null), \"neural\");")]),
 ("the resolved voice never reaches the renderer",
  [(SM, "() => _voices.StartNeural(text, pick?.RenderVoice, pick?.RenderLanguage), \"neural\");",
       "() => _voices.StartNeural(text, null, pick?.RenderLanguage), \"neural\");")]),
 ("the voice list is cached at first use",
  [(SM, "private string? _symbolicVoice;",
       "private string? _symbolicVoice;\n    private IReadOnlyList<SpeechdVoice>? _cache;"),
   (SM, "foreach (var v in _installed.Read())",
       "foreach (var v in _cache ??= _installed.Read())")]),
 ("--language is no longer passed to the renderer",
  [(VC, "if (!string.IsNullOrWhiteSpace(language))\n        {\n            psi.ArgumentList.Add(\"--language\");",
       "if (false)\n        {\n            psi.ArgumentList.Add(\"--language\");")]),
 ("the models root is the store root itself",
  [(VC, 'Path.Combine(store.TrimEnd(\'/\'), "models")', "store.TrimEnd('/')")]),
 ("supertonic styles read from the wrong directory",
  [(IV, 'Path.Combine(_modelsRoot, "voice_styles")', 'Path.Combine(_modelsRoot, "styles")')]),
]

def run_tests():
    r = subprocess.run(
        ["dotnet", "test", "src/VibeSuperTonic.SpeechD.Tests/VibeSuperTonic.SpeechD.Tests.csproj",
         "-c", "Release", "--nologo"],
        cwd=ROOT, capture_output=True, text=True)
    out = r.stdout + r.stderr
    if "error CS" in out:
        return "BUILD-ERROR"
    return "caught" if r.returncode != 0 else "NOT CAUGHT"

results = []
for label, edits in MUTATIONS:
    originals = {}
    ok = True
    for path, find, repl in edits:
        full = os.path.join(ROOT, path)
        if full not in originals:
            originals[full] = open(full).read()
        s = open(full).read()
        if find not in s:
            ok = False
            break
        open(full, "w").write(s.replace(find, repl, 1))
    verdict = run_tests() if ok else "PATCH-MISS"
    for full, text in originals.items():
        open(full, "w").write(text)
    results.append((label, verdict))
    print(f"{verdict:12}  {label}", flush=True)

print()
bad = [l for l, v in results if v != "caught"]
print(f"{len(results) - len(bad)}/{len(results)} mutations caught")
for l in bad:
    print("  UNDEFENDED:", l)
