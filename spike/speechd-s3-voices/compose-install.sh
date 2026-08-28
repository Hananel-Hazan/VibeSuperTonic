#!/bin/bash
# Compose the install run-module.sh drives, at /tmp/vst-s3-install.
#
# NOT A RELEASE ARTIFACT and never to be confused with one — build/pack-tar.sh is
# the only thing that composes a shippable tree, and this skips every assertion it
# makes. This is a scratch install for measuring the module against the machine's
# real speech-dispatcher.
#
# The models are FAKE — empty .json style files and a two-byte .onnx — because
# what is being measured is the voice LIST and the arguments that reach the
# renderer, not synthesis. The neural render therefore fails at the daemon with
# "ONNX model directory not found", which is the correct end of this test: it
# proves the resolved voice reached the daemon, and it proves trap 16 caught the
# failure rather than going silent.
set -eu
cd "$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"

OUT=/tmp/vst-s3-install
rm -rf "$OUT" /tmp/vst-s3-pub-{sd,ctl,d}; mkdir -p "$OUT"

[ -x build/espeak-out/espeak/espeak-ng ] || {
    echo "no espeak payload — run: bash build/build-espeak.sh" >&2; exit 1; }

dotnet publish src/VibeSuperTonic.SpeechD/VibeSuperTonic.SpeechD.csproj -c Release -r linux-x64 -o /tmp/vst-s3-pub-sd  --nologo
dotnet publish src/VibeSuperTonic.Ctl/VibeSuperTonic.Ctl.csproj         -c Release -r linux-x64 -o /tmp/vst-s3-pub-ctl --nologo
dotnet publish src/VibeSuperTonic.Daemon/VibeSuperTonic.Daemon.csproj   -c Release -r linux-x64 -o /tmp/vst-s3-pub-d   --nologo

cp /tmp/vst-s3-pub-sd/vst-speechd "$OUT/"
cp /tmp/vst-s3-pub-ctl/vst-ctl    "$OUT/"

# THE WHOLE PUBLISH OUTPUT, not just the apphost. Copying only `vibesupertonicd`
# gets you "The application to execute does not exist: vibesupertonicd.dll", the
# module falling back to espeak, and a test that looks like a product bug.
cp -r /tmp/vst-s3-pub-d/. "$OUT/"
cp -r build/espeak-out/espeak "$OUT/"

mkdir -p "$OUT/models/voice_styles"
for s in F1 F2 F3 M1 M2 M3; do echo '{}' > "$OUT/models/voice_styles/$s.json"; done

PIPER="$OUT/models/piper/de_DE-thorsten-low"
mkdir -p "$PIPER"
# Both files or neither — the store's own rule for what counts as installed.
echo w   > "$PIPER/de_DE-thorsten-low.onnx"
echo '{}' > "$PIPER/de_DE-thorsten-low.onnx.json"

echo
echo "composed $OUT  ($("$OUT/vst-speechd" --version))"
echo "now: bash spike/speechd-s3-voices/run-module.sh"
