#!/usr/bin/env python3
"""Pin every NuGet package the Linux release restores, as Flatpak sources.

    python3 build/flatpak/gen-nuget-sources.py [--in-sdk 25.08] [-o FILE]

Writes build/flatpak/nuget-sources.json: one `file` source per package, with
nuget.org's URL and its SHA-512, landing in nuget-sources/ inside the build.
The source-build manifest (io.github.hananel_hazan.VibeSuperTonic.source.yml.in)
lists it, and points a NuGet.Config at that folder, so Flathub's builder, which
has no network, restores from exactly these bytes.

WHY OUR OWN AND NOT flatpak-dotnet-generator.py. It is the same idea in fewer
lines, and it restores what pack-tar.sh PUBLISHES: the daemon and the window
self-contained, vst-ctl and vst-speechd NativeAOT, all for linux-x64. A generator
told about the solution instead would pin the Windows engine's packages and miss
the AOT compiler, and the offline build would fail on the first publish.

--in-sdk RUNS THE RESTORE INSIDE THE FLATPAK SDK, WITH ITS DOTNET EXTENSION. That
is the one that matters. The runtime packs (Microsoft.NETCore.App.Runtime.*, the
ILCompiler packs) are versioned by the SDK that restores them, not by anything in
this repository: SDK 10.0.112 asks for 10.0.12. That is exactly what happened.
The first file was generated on a workstation with 10.0.112, and Flathub's
dotnet10//25.08 asks for 10.0.8. It also brings its own apphost, so
Microsoft.NETCore.App.Host drops out: 33 packages, not 34. CI's flathub job
regenerates with --in-sdk and refuses a committed file that differs. The run
that caught this (2026-09-27) was also the first full build to pass.

Deterministic: sorted by name and version, and nothing about the machine in it.
"""
import argparse
import base64
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_OUT = ROOT / "build" / "flatpak" / "nuget-sources.json"

# Exactly pack-tar.sh's four publishes. Keep them in step with it.
PROJECTS = [
    ("src/VibeSuperTonic.Daemon/VibeSuperTonic.Daemon.csproj", ["-p:SelfContained=true"]),
    ("src/VibeSuperTonic.Ui/VibeSuperTonic.Ui.csproj", ["-p:SelfContained=true"]),
    ("src/VibeSuperTonic.Ctl/VibeSuperTonic.Ctl.csproj", []),
    ("src/VibeSuperTonic.SpeechD/VibeSuperTonic.SpeechD.csproj", []),
]


def dotnet_prefix(sdk, home):
    if not sdk:
        return ["dotnet"]
    ext = "org.freedesktop.Sdk.Extension.dotnet10"
    # The extension's dotnet, in the SDK the manifest builds with: run with the
    # SDK as its runtime, the extension is mounted where the manifest's
    # append-path finds it. --filesystem so it can read the repository and
    # write the packages folder. The same shape as flatpak-dotnet-generator's.
    #
    # HOME is a directory of our own, because the caller's may not be writable
    # from inside: dotnet's first run creates ~/.dotnet, and in CI's container
    # HOME is /github/home, owned by the runner's user while the job is root,
    # and bwrap drops the capability that lets root write there anyway. The
    # first flathub run died on exactly that (UnauthorizedAccessException on
    # '/github/home/.dotnet'). NuGet's own caches follow HOME too.
    home.mkdir(parents=True, exist_ok=True)
    return [
        "flatpak", "run",
        f"--env=HOME={home}",
        f"--env=DOTNET_CLI_HOME={home}",
        "--env=DOTNET_CLI_TELEMETRY_OPTOUT=1",
        "--env=DOTNET_NOLOGO=1",
        "--env=DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1",
        "--share=network", "--filesystem=host",
        "--command=sh",
        f"--runtime=org.freedesktop.Sdk//{sdk}",
        f"{ext}//{sdk}",
        "-c",
        'PATH="$PATH:/usr/lib/sdk/dotnet10/bin" '
        'LD_LIBRARY_PATH="${LD_LIBRARY_PATH:+$LD_LIBRARY_PATH:}/usr/lib/sdk/dotnet10/lib" '
        'exec dotnet "$@"',
        "dotnet",
    ]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in-sdk", metavar="BRANCH", help="restore inside org.freedesktop.Sdk//BRANCH")
    ap.add_argument("-o", "--out", default=str(DEFAULT_OUT))
    args = ap.parse_args()

    # Inside the home directory rather than /tmp: `flatpak run` gives the
    # sandbox its own /tmp, and --filesystem=host does not reach it.
    (ROOT / "dist").mkdir(exist_ok=True)
    tmp = Path(tempfile.mkdtemp(prefix=".vst-nuget-", dir=str(ROOT / "dist")))
    try:
        prefix = dotnet_prefix(args.in_sdk, tmp / "home")
        packages = tmp / "packages"
        for proj, extra in PROJECTS:
            cmd = prefix + [
                "restore", str(ROOT / proj), "-r", "linux-x64",
                "-p:_IsPublishing=true", *extra,
                "--packages", str(packages), "--nologo", "-v", "q",
            ]
            print("restore", proj, file=sys.stderr)
            subprocess.run(cmd, check=True)

        sources = []
        for sha_file in sorted(packages.glob("*/*/*.nupkg.sha512")):
            version_dir = sha_file.parent
            name = version_dir.parent.name
            version = version_dir.name
            sha512 = base64.b64decode(sha_file.read_text().strip()).hex()
            filename = f"{name}.{version}.nupkg"
            sources.append({
                "type": "file",
                "url": f"https://api.nuget.org/v3-flatcontainer/{name}/{version}/{filename}",
                "sha512": sha512,
                "dest": "nuget-sources",
                "dest-filename": filename,
            })
        if not sources:
            sys.exit("the restore produced no packages; nothing to pin")
        sources.sort(key=lambda s: s["dest-filename"])
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    Path(args.out).write_text(json.dumps(sources, indent=4) + "\n")
    print(f"{len(sources)} packages -> {args.out}", file=sys.stderr)


if __name__ == "__main__":
    main()
