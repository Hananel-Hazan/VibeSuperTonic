#!/usr/bin/env python3
"""Stress what Phase 9 added: the image lifecycle and the client copied out of it.

    python3 spike/appimage-stress/appimage-stress.py dist/VibeSuperTonic-*.AppImage

Everything here is something a real machine does — logging in and out, upgrading,
two things starting at once, a half-written file — against the paths that no unit
test can reach: a squashfs mount, a re-exec, a copy into ~/.local/bin, and a
sidecar that has to survive all of it.

It runs in a temporary HOME and a temporary XDG_RUNTIME_DIR, so the daily install
is never touched. PULSE_SERVER is passed through explicitly because overriding
XDG_RUNTIME_DIR otherwise hides the audio socket, and a daemon that cannot open
audio takes different paths — the first run of this file passed a whole suite
that way and proved less than it looked.
"""
import concurrent.futures as futures
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time

SOURCE = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else "dist/VibeSuperTonic.AppImage")
ROOT = tempfile.mkdtemp(prefix="/tmp/vsa-")          # short: the socket has 108 bytes

# The image is COPIED here first. The store goes beside the file it was started
# from, so running this against dist/ directly creates dist/VibeSuperTonic and
# leaves it next to the artifact — which the first run of this file did.
IMAGE = os.path.join(ROOT, "VibeSuperTonic.AppImage")

# Rendering needs a model set, and there is deliberately none in the archive.
# Point at an existing install's; without one the audio scenarios would pass
# against a daemon that cannot speak, which is worth less than skipping them.
MODELS = os.environ.get("VST_MODELS") or os.path.expanduser("~/Apps/VibeSuperTonic/models")
HOME = os.path.join(ROOT, "home")
RUN = os.path.join(ROOT, "run")
SOCK = os.path.join(RUN, "vibesupertonic", "ctl.sock")
CLIENT = os.path.join(HOME, ".local", "bin", "vst-ctl")
SIDECAR = CLIENT + ".appimage"

failures = []


def env(**extra):
    e = dict(os.environ, HOME=HOME, XDG_RUNTIME_DIR=RUN)
    e.pop("APPIMAGE", None)
    pulse = f"/run/user/{os.getuid()}/pulse/native"
    if os.path.exists(pulse):
        e["PULSE_SERVER"] = "unix:" + pulse
    e.update(extra)
    return e


def check(label, ok, detail=""):
    print(f"  {'ok  ' if ok else 'FAIL'} <- {label}{'' if ok else '   ' + detail}")
    if not ok:
        failures.append(label)
    return ok


def ask(verb="Status", timeout=15.0):
    """One request on the control socket, or None."""
    try:
        s = socket.socket(socket.AF_UNIX)
        s.settimeout(timeout)
        s.connect(SOCK)
        s.sendall(json.dumps({"Verb": verb}).encode() + b"\n")
        buf = b""
        while not buf.endswith(b"\n"):
            chunk = s.recv(65536)
            if not chunk:
                break
            buf += chunk
        s.close()
        return json.loads(buf.decode())
    except Exception:
        return None


def start_daemon(extra=()):
    args = ["--models", MODELS] if os.path.isdir(MODELS) else []
    return subprocess.Popen(
        [IMAGE, "daemon", *args, *extra], env=env(),
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def wait_up(seconds=25.0):
    deadline = time.time() + seconds
    while time.time() < deadline:
        r = ask()
        if r and r.get("Ok"):
            return r
        time.sleep(0.25)
    return None


def shutdown():
    subprocess.run([IMAGE, "ctl", "shutdown"], env=env(),
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)
    for _ in range(40):
        if ask(timeout=1.0) is None:
            return True
        time.sleep(0.25)
    return False


def mounts():
    return len([d for d in os.listdir("/tmp") if d.startswith(".mount_VibeSu")])


def mounts_settled(target, seconds=10.0):
    """Wait for the runtime's own teardown before counting.

    The image is unmounted by the runtime AFTER its child exits, and `shutdown`
    returns as soon as the socket stops answering — so counting immediately reads
    one mount that is on its way out and calls it a leak. The first version of
    this file did exactly that and reported a defect that was its own impatience.
    """
    deadline = time.time() + seconds
    while time.time() < deadline:
        if mounts() <= target:
            return True
        time.sleep(0.25)
    return False


# --------------------------------------------------------------- scenarios

def lifecycle(cycles=10):
    """Login, logout, login… The mount is what makes this different from a tarball."""
    print(f"\n=== {cycles} start/shutdown cycles through the image ===")
    before = mounts()
    versions, clean = set(), True
    for _ in range(cycles):
        p = start_daemon()
        up = wait_up()
        if not up:
            clean = False
            p.kill()
            break
        versions.add(up["Status"]["Version"])
        if not shutdown():
            clean = False
            break
        if os.path.exists(SOCK):
            clean = False
            break

    check(f"{cycles} cycles, one version {versions or '—'}", clean and len(versions) == 1)
    check("no leaked squashfs mounts", mounts_settled(before),
          f"{mounts() - before} still there after 10s")


def client_race(workers=8):
    """Everything that wakes the product repairs the client. They can race."""
    print(f"\n=== {workers} concurrent client installs ===")
    shutil.rmtree(os.path.join(HOME, ".local"), ignore_errors=True)

    def once(_):
        return subprocess.run([IMAGE, "daemon", "--ensure-client"], env=env(),
                              capture_output=True, timeout=60).returncode

    with futures.ThreadPoolExecutor(workers) as pool:
        codes = list(pool.map(once, range(workers)))

    check("every install exited 0", all(c == 0 for c in codes), str(codes))
    ok = os.path.exists(CLIENT) and os.access(CLIENT, os.X_OK)
    check("exactly one client, executable", ok)
    if ok:
        v = subprocess.run([CLIENT, "--version"], capture_output=True, text=True, timeout=30)
        check("the client that survived the race runs", v.returncode == 0, v.stderr.strip()[:120])
        check("no torn temporaries left behind",
              not [f for f in os.listdir(os.path.dirname(CLIENT)) if f.startswith(".vst-ctl.")])


def client_repair():
    """A half-written or stale client is a hotkey that does nothing."""
    print("\n=== the client is repaired, however it is broken ===")
    for label, break_it in [
        ("deleted", lambda: os.remove(CLIENT)),
        ("truncated", lambda: open(CLIENT, "wb").close()),
        ("not executable", lambda: os.chmod(CLIENT, 0o644)),
        ("sidecar pointing elsewhere", lambda: open(SIDECAR, "w").write("/nonexistent.AppImage\n")),
    ]:
        break_it()
        subprocess.run([IMAGE, "daemon", "--ensure-client"], env=env(),
                       capture_output=True, timeout=60)
        v = subprocess.run([CLIENT, "--version"], capture_output=True, text=True, timeout=30)
        sidecar = open(SIDECAR).read().strip() if os.path.exists(SIDECAR) else ""
        check(f"repaired after: {label}", v.returncode == 0 and sidecar == IMAGE,
              f"exit {v.returncode}, sidecar {sidecar!r}")


def second_daemon():
    """Two daemons must never both hold the socket — one owns the speakers."""
    print("\n=== a second daemon while one is running ===")
    first = start_daemon()
    if not check("first daemon is up", wait_up() is not None):
        first.kill()
        return

    second = subprocess.run([IMAGE, "daemon"], env=env(), capture_output=True,
                            text=True, timeout=60)
    check("the second refuses rather than taking the socket", second.returncode != 0,
          f"exit {second.returncode}")
    said = (second.stdout + second.stderr).lower()
    check("it says why", "already listening" in said, said.strip()[-160:])
    check("the first is still answering", (ask() or {}).get("Ok") is True)
    shutdown()


def autostart_through_the_copy():
    """R-5 through the whole chain: copy -> sidecar -> image -> daemon."""
    print("\n=== a press with no daemon running ===")
    check("nothing is listening", ask(timeout=1.0) is None)
    r = subprocess.run([CLIENT, "status"], env=env(), capture_output=True, text=True, timeout=60)
    check("the copied client started one and got an answer", r.returncode == 0,
          r.stderr.strip()[:160])
    shutdown()


def speak_churn(rounds=8):
    """Speak, interrupt, speak again — the shape of the stale-flush defect."""
    print(f"\n=== {rounds} interrupted utterances ===")
    if not os.path.isdir(MODELS):
        print(f"  SKIP — no model set at {MODELS}; set VST_MODELS to run this")
        return
    p = start_daemon()
    if not check("daemon up", wait_up() is not None):
        p.kill()
        return

    spoke = 0
    for i in range(rounds):
        s = socket.socket(socket.AF_UNIX)
        s.settimeout(15)
        s.connect(SOCK)
        s.sendall(json.dumps({
            "Verb": "Speak",
            "Text": f"Interrupted utterance number {i}, which should be cut off before it ends.",
        }).encode() + b"\n")
        reply = json.loads(s.recv(65536).decode())
        s.close()
        if reply.get("Ok"):
            spoke += 1
        time.sleep(0.35)
        ask("Stop")
        time.sleep(0.1)

    check(f"{rounds} speak/stop rounds all accepted", spoke == rounds, f"{spoke}/{rounds}")

    # The one that matters: after all that interrupting, does a press still speak?
    ask("Stop")
    time.sleep(0.3)
    s = socket.socket(socket.AF_UNIX)
    s.settimeout(20)
    s.connect(SOCK)
    s.sendall(json.dumps({"Verb": "Speak", "Text": "And this one has to be heard."}).encode() + b"\n")
    accepted = json.loads(s.recv(65536).decode()).get("Ok")
    s.close()
    time.sleep(1.5)
    state = (ask() or {}).get("Status", {}).get("State")
    check("the utterance after the interruptions is not silenced",
          accepted and state in ("Preparing", "Speaking"), f"state {state}")
    shutdown()


if __name__ == "__main__":
    if not os.path.exists(SOURCE):
        print(f"no AppImage at {SOURCE}")
        sys.exit(2)

    os.makedirs(RUN, exist_ok=True)
    os.makedirs(HOME, exist_ok=True)
    shutil.copy2(SOURCE, IMAGE)
    os.chmod(IMAGE, 0o755)
    print(f"image  {SOURCE}\n  copied to {IMAGE}\nhome   {HOME}\nmodels {MODELS}"
          f"{'' if os.path.isdir(MODELS) else '   (missing — audio scenarios will skip)'}")

    try:
        lifecycle()
        client_race()
        client_repair()
        autostart_through_the_copy()
        second_daemon()
        speak_churn()
    finally:
        subprocess.run([IMAGE, "ctl", "shutdown"], env=env(),
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    print("\n=== final ===")
    print("RESULT:", "PASS" if not failures else f"PROBLEMS: {failures}")
    sys.exit(0 if not failures else 1)
