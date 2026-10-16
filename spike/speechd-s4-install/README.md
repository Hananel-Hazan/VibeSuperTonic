# S4: the installer, and the machine it must not break

Written 2026-08-28. [S4](../../docs/SPEECHD-PLAN.md#s4) registers the module with
Speech Dispatcher, and the thing being defended is not our module working — it is
**espeak-ng still answering afterwards**, on the desktop of somebody who cannot
see an error message.

Same house rule as [the 705 gate](../speechd-705-gate/README.md) and
[S3](../speechd-s3-voices/README.md): **probe it, don't read about it**, and keep
the distro's own module in the config beside ours so "ours did X" means something.

## Run

```bash
bash spike/speechd-s4-install/restore-test.sh --compose   # the test that matters
bash spike/speechd-s4-install/home-rule-test.sh           # appimage-home.sh alone
python3 spike/speechd-s4-install/sabotage.py              # break each rule, check a test notices
bash spike/speechd-s4-install/payload-sabotage.sh         # the same for the packer assertion
bash spike/speechd-s4-install/appimage-sabotage.sh        # and for the AppImage's
```

`restore-test.sh` needs the scratch install `--compose` builds (it drives the S3
composer at `/tmp/vst-s4-install`). `payload-sabotage.sh` needs a composed tree,
so run `build/pack-tar.sh` first.

## Nothing here touches your speech-dispatcher

**One variable is redirected — `XDG_CONFIG_HOME` — and both halves follow it.**
speech-dispatcher reads its user config from `$XDG_CONFIG_HOME/speech-dispatcher`
(measured on 0.12.1) and the installer writes there by default, so the installer
under test resolves the module, the store and the config exactly as it will on a
user's machine rather than being handed the answers. `/etc/speech-dispatcher` is
read and never written: it is the source of trap 2's copy, and using the real one
is the point.

A private `speech-dispatcher` runs with its own socket, pidfile and log
directory, and `XDG_RUNTIME_DIR` moves with it — which also keeps the daemon's
control socket out of the way of the one you are using.

`-C` was the first attempt and is wrong: it replaces the system directory
wholesale, so the scratch directory plays both roles and trap 2's copy has
nowhere to copy from, and it refuses to start when that directory has no
`speechd.conf` — which makes the auto-detection baseline untestable.

## What was found

| Question | Answer |
| --- | --- |
| Can a module's binary field carry an argument? | **No.** `Exec of module ... failed with error 2` — *and speechd still logs "Module loaded"* |
| So how does an AppImage name itself? | A symlink `~/.local/bin/vst-speechd` → the image, resolved by AppRun's `$ARGV0` case |
| Does a user config have to carry module configs? | No — `espeak-ng.conf` still resolves from `/etc` |
| `pgrep -x speech-dispatcher`? | **Never matches.** 17 characters against pgrep's 15-character `comm` limit |
| espeak-ng's voice list | 14,805 rows, before and after, every time |

## The bug the harness found

`printf '%s\n' "${names[@]}"` on an **empty** array prints one blank line, not
nothing. So the enumeration of "what does speechd offer now" counted one module,
the refusal that guards [trap 1](../../docs/SPEECHD-PLAN.md#t1) never fired, and
the config written declared **ours and nothing else** — which is precisely the
catastrophe the installer exists to prevent, arriving through the code meant to
prevent it. It survived being written and being read. What caught it was scenario
F: asking speechd through a socket that is not there.

## The sabotages found bugs in the tests

Three of the first thirteen mutations came back `MISATTRIBUTED` or `NOT CAUGHT`,
and every one was a defect in a check rather than in the installer:

- `grep 'AddModule "espeak-ng"'` also matches the **commented** line the copied
  system config brings with it. The re-declaration check was passing on the
  distro's own comment while the module list was in fact destroyed.
- the moved-install scenario proved nothing, because with speechd not restarted
  `--check` fails anyway — ours is configured but not offered, whatever the path
  says. It now restarts and confirms ours is offered *before* moving anything.
- the packer's trap-13 probe had the same shape, asserting only a non-zero exit.
  It now requires the words.

A fourth is worth keeping in mind rather than fixing: the two guards against the
empty-array bug — one in the function, one in the caller — are individually
invisible to any test, because either alone is sufficient. The mutation removes
both, and the redundancy stays.
