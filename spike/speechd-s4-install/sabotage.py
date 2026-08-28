#!/usr/bin/env python3
"""Break each rule S4's installer enforces, one at a time, and check the
restore test notices — and notices for the RIGHT reason.

The house rule: a check that has never been observed failing is not evidence.
Every assertion in restore-test.sh is here as a mutation of the installer, and
a mutation that leaves the suite green is a rule nothing defends.

    python3 spike/speechd-s4-install/sabotage.py [--only <substring>]

Each case runs the whole restore test (~50 s), because the checks are
behavioural: they need a real speech-dispatcher to answer, and there is no unit
level at which "espeak-ng still speaks" is a meaningful question.

The mutations are applied to a COPY at /tmp/vst-s4-sabotage.sh, never to the
tree — a sabotage run interrupted halfway must not leave a booby-trapped
installer in the repository, and this one edits the file a release ships.
"""
import subprocess, sys, pathlib, os

ROOT = pathlib.Path(__file__).resolve().parents[2]
SRC = ROOT / "build/speechd-install.sh"
COPY = pathlib.Path("/tmp/vst-s4-sabotage.sh")
TEST = ROOT / "spike/speechd-s4-install/restore-test.sh"

# (label, [(find, replace), ...], the check that must fail)
MUTATIONS = [
 ("the other modules are not re-declared (trap 1)",
  [('    [[ "$name" == "$MODULE_NAME" ]] && continue\n'
    '    grep -qx "$name" <<< "$already" && continue\n', '    continue\n')],
  "espeak-ng was not re-declared"),

 ("the system config is not copied first (trap 2)",
  [('        cp "$system_conf_dir/speechd.conf" "$conf"',
    '        : > "$conf"')],
  "does not look like a copy"),

 ("an existing config is edited with no backup",
  [('    cp "$conf" "$backup"\n', '    :\n')],
  "no backup was taken"),

 ("--remove keeps the config it created",
  [('        rm -f "$conf"\n        say "  removed $conf',
    '        say "  removed $conf')],
  "was left behind"),

 ("--remove strips instead of restoring the backup",
  [('    if [[ -n "$backup" && -f "$backup" ]]; then', '    if false; then')],
  "differs from the original"),

 ("a re-install stacks another block",
  [('    tmp="$(mktemp)"\n'
    '    awk -v b="$MARK_BEGIN" -v e="$MARK_END" \'\n'
    "        $0 == b { skip = 1 } skip == 0 { print } $0 == e { skip = 0 }' \"$conf\" > \"$tmp\"\n"
    '    grep -v "AddModule \\"$MODULE_NAME\\"" "$tmp" > "$conf"\n'
    '    rm -f "$tmp"\n', '    :\n')],
  "the block was stacked"),

 ("a machine with no voices is registered anyway (trap 14)",
  [('if (( voices == 0 && force == 0 )); then', 'if false; then')],
  "registered a module with no voices"),

 ("--force stops working",
  [('if (( voices == 0 && force == 0 )); then', 'if true; then')],
  "--force did not override"),

 ("the INIT gate accepts any reply (trap 14)",
  [('[[ "$reply" == 299\\ * ]] || die "the module does not answer INIT',
    '[[ -n "$reply" || 1 ]] || die "the module does not answer INIT')],
  "registered a module that cannot start"),

 ("--check does not ask the module whether it works",
  [('    reply="$(module_init_reply $module_cmd)"\n'
    '    if [[ "$reply" == 299\\ * ]]; then',
    '    reply="$(module_init_reply $module_cmd)"\n'
    '    if true; then')],
  "--check passed a broken install"),

 ("--check does not verify the configured path (trap 13)",
  [('            if [[ -e "${declared%% *}" ]]; then', '            if true; then')],
  "naming a path that does not exist"),

 ("an empty enumeration is treated as a real answer (trap 1)",
  [('if (( ${#offered[@]} == 0 )); then', 'if false; then')],
  "wrote a config it could not verify"),

 # BOTH layers, because neither is separately observable: `printf` on an empty
 # array emits one blank line, and either the guard in the function or the
 # awk filter in the caller is enough to stop that counting as a module. That
 # redundancy is deliberate for a failure whose consequence is a screen reader
 # left with one synthesizer — but it means the honest mutation removes both.
 ("the empty-array defences are removed (trap 1)",
  [('    (( ${#names[@]} )) || return 0\n', ''),
   ("mapfile -t offered < <(enumerate_offered | awk 'NF')",
    'mapfile -t offered < <(enumerate_offered)')],
  "wrote a config it could not verify"),
]

only = None
if "--only" in sys.argv:
    only = sys.argv[sys.argv.index("--only") + 1]

original = SRC.read_text()
env = dict(os.environ, VST_INSTALLER_SRC=str(COPY))

def run_case(label, edits, expect):
    text = original
    for find, repl in edits:
        if find not in text:
            return "PATCH-MISS", ""
        text = text.replace(find, repl, 1)
    COPY.write_text(text)
    COPY.chmod(0o755)
    r = subprocess.run(["bash", str(TEST)], cwd=ROOT, capture_output=True, text=True, env=env)
    out = r.stdout + r.stderr
    fails = [l for l in out.splitlines() if "FAIL" in l]
    if r.returncode == 0:
        return "NOT CAUGHT", ""
    if any(expect in l for l in fails):
        return "caught", fails[0].strip()
    # It broke something — but not the thing this mutation was aimed at, which
    # means the check named above is still undefended.
    return "MISATTRIBUTED", "; ".join(f.strip() for f in fails[:2])

results = []
for label, edits, expect in MUTATIONS:
    if only and only not in label:
        continue
    verdict, detail = run_case(label, edits, expect)
    results.append((label, verdict))
    print(f"{verdict:14} {label}", flush=True)
    if verdict in ("MISATTRIBUTED", "NOT CAUGHT") and detail:
        print(f"               ({detail})", flush=True)

COPY.unlink(missing_ok=True)
assert SRC.read_text() == original, "the installer in the tree was modified — it must not be"

bad = [l for l, v in results if v != "caught"]
print(f"\n{len(results) - len(bad)}/{len(results)} mutations caught")
for l in bad:
    print("  UNDEFENDED:", l)
sys.exit(1 if bad else 0)
