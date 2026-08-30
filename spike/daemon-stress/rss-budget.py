#!/usr/bin/env python3
"""How much memory does the daemon hold, and does it grow?

    python3 spike/daemon-stress/rss-budget.py --models <dir> [--utterances 30]

THE ONE CHECK IN docs/TESTING-PLAN.md THAT CANNOT BE A CI JOB. Every other
budget there — the archive, the espeak payload, vst-ctl's startup, the pipeline
— is measured with no model on disk. This one needs 1.6 GB of them and a machine
that is not doing anything else, which is exactly the "needs a GPU, an audio
device, or a human ear" row of that document's own table: a spike, run by hand,
with the number recorded rather than a job pretending to be continuous.

WHY IT MATTERS MORE THAN THE ARCHIVE'S SIZE. The daemon is resident for a login
session. A tarball that gained 20 MB costs a download once; a daemon that gains
20 MB per utterance takes the machine down by lunchtime, and the user's report
is "my computer got slow", which nobody attributes to a text-to-speech engine.

TWO NUMBERS, AND THE SECOND IS THE ONE THAT TRAVELS. The ceiling is a property
of this machine's models and provider — worth recording in release notes the way
P2's table is, and not worth much on somebody else's hardware. The GROWTH across
N utterances is a property of the code: a leak shows up identically on any
machine, and a flat line is the claim being made.

NOTHING HERE TOUCHES A RUNNING DAEMON. It starts its own under a redirected
XDG_RUNTIME_DIR, so the socket it binds is not the one the user's session is
using — the same rule the speech-dispatcher harness and the socket-mode tests
are built on. It renders rather than speaks: no audio device is opened, and a
machine being measured stays quiet.
"""
import argparse, json, os, shutil, socket, subprocess, sys, tempfile, time

# --- the budgets, and TWO WRONG VERSIONS CAME FIRST --------------------------
#
# 1. A FIXED GROWTH BUDGET measured from the first utterance. Measured
#    2026-08-30: 30 utterances grew 52 MB and 100 grew 100 MB, so growth is
#    sub-linear — the same behaviour passes at N=30 and fails at N=100, and the
#    budget is really a budget on how long you ran the script.
#
# 2. COMPARING THE TWO HALVES of the run, on the theory that a warming heap
#    decelerates and a leak does not. It reads well and it does not work: a
#    daemon deliberately leaking 2 MB per utterance PASSED it — +108 MB then
#    +61 MB, which the ratio scored as "settling". Warm-up is large enough here
#    to hide a real leak inside its own deceleration.
#
# What actually works is to stop measuring during warm-up. Measured on this
# machine, the peak stops moving around utterance 60 and does not move again
# through 200 — so: render WARMUP utterances and ignore them entirely, then
# measure the next N. In that window a healthy daemon grew 0 MB and a daemon
# leaking 2 MB per utterance cannot hide, because there is nothing left for it
# to hide behind.
#
# Peaks rather than current RSS, because VmHWM only goes up: a GC dip in the
# wrong place would otherwise read as shrinkage.
# AND THE WARM-UP IS MEASURED, NOT GUESSED — the third thing this got wrong.
# A fixed 100 was right for Supertonic, whose peak stops moving by utterance 60,
# and WRONG for Piper, which is still climbing at 100 and settles around 200. A
# 100-utterance warm-up therefore reported Piper as leaking 1353 kB per
# utterance, which it is not: the same run with a 200-utterance warm-up grows by
# ZERO. A false leak report is the worst outcome available here — it sends
# somebody hunting a defect that does not exist, in a component that is fine.
#
# So warm-up now ends when the peak STOPS MOVING: batches of ten until VmHWM is
# unchanged across three of them. That measures the thing that differs between
# engines instead of assuming it, and how long it took is itself worth printing.
CEILING_MB = 1400        # warm, one engine loaded. Phase 0 measured ~830; Supertonic peaks at 842, Piper at 1087.
WARMUP_BATCH = 10
WARMUP_STABLE_BATCHES = 3   # 30 utterances with no new peak
WARMUP_CAP = 300            # a heap that has not settled by here is the finding
TAIL_GROWTH_MB = 25      # over the measured window, AFTER warm-up. Measured: 0.


def rss_kb(pid):
    """Resident set and peak resident set, from the kernel rather than from a guess."""
    cur = peak = 0
    with open(f"/proc/{pid}/status") as f:
        for line in f:
            if line.startswith("VmRSS:"):
                cur = int(line.split()[1])
            elif line.startswith("VmHWM:"):
                peak = int(line.split()[1])
    return cur, peak


def wait_for_socket(path, timeout=30):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if os.path.exists(path):
            try:
                s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                s.settimeout(2)
                s.connect(path)
                s.close()
                return True
            except OSError:
                pass
        time.sleep(0.2)
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--models", required=True, help="a models/ directory with onnx/ in it")
    ap.add_argument("--tree", default="dist/release-linux/VibeSuperTonic",
                    help="where vibesupertonicd and vst-ctl are (default: the packed tree)")
    ap.add_argument("--voice", default=None, help="engine-qualified id, e.g. piper:en_GB-cori-high")
    ap.add_argument("--warmup", type=int, default=0,
                    help="fixed warm-up; 0 (the default) warms up until the peak stops moving")
    ap.add_argument("--utterances", type=int, default=100,
                    help="utterances measured, after the warm-up")
    ap.add_argument("--ceiling-mb", type=int, default=CEILING_MB)
    args = ap.parse_args()

    daemon = os.path.join(args.tree, "vibesupertonicd")
    ctl = os.path.join(args.tree, "vst-ctl")
    for p in (daemon, ctl):
        if not os.access(p, os.X_OK):
            sys.exit(f"{p} is missing or not executable — run: bash build/pack-tar.sh")

    # A SHORT path, because AF_UNIX truncates around 108 bytes and a socket under
    # a long one makes the daemon die claiming it cannot bind — which reads like
    # a permissions problem and is not.
    run = tempfile.mkdtemp(prefix="/tmp/vst-rss-")
    env = dict(os.environ, XDG_RUNTIME_DIR=run)
    sock = os.path.join(run, "vibesupertonic", "ctl.sock")

    print(f"models   {args.models}")
    print(f"binaries {args.tree}")
    print(f"socket   {sock}  (a private one — the session's daemon is untouched)")
    print()

    proc = subprocess.Popen([daemon, "--models", args.models],
                            env=env, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    try:
        if not wait_for_socket(sock):
            err = proc.stderr.read().decode(errors="replace")[-2000:] if proc.stderr else ""
            sys.exit(f"the daemon never bound {sock}\n{err}")

        def render(text):
            cmd = [ctl, "render", "--out", "/dev/null"]
            if args.voice:
                cmd += ["--voice", args.voice]
            cmd.append(text)
            r = subprocess.run(cmd, env=env, capture_output=True, timeout=180)
            if r.returncode != 0:
                sys.exit("render failed: " + r.stderr.decode(errors="replace").strip())

        def sample(label):
            cur, peak = rss_kb(proc.pid)
            print(f"  {label:28} {cur / 1024:8.0f} MB   (peak {peak / 1024:.0f} MB)")
            return cur / 1024

        print("RSS, resident set of the daemon process:")
        idle = sample("bound, nothing loaded")

        render("The quick brown fox jumps over the lazy dog.")
        warm = sample("after the first utterance")

        warm_peak = rss_kb(proc.pid)[1] / 1024

        # WARM UP UNTIL IT STOPS GROWING, rather than for a number somebody
        # picked. Supertonic settles by ~60 utterances and Piper by ~200; a fixed
        # 100 reports the second as a leak.
        done = 0
        settled = False
        last_peak = rss_kb(proc.pid)[1] / 1024
        stable = 0
        while done < (args.warmup or WARMUP_CAP):
            for _ in range(WARMUP_BATCH):
                render(f"Warming up, utterance number {done}.")
                done += 1
            sample(f"warm-up {done}")
            peak_now = rss_kb(proc.pid)[1] / 1024
            stable = stable + 1 if peak_now <= last_peak else 0
            last_peak = max(last_peak, peak_now)
            if not args.warmup and stable >= WARMUP_STABLE_BATCHES:
                settled = True
                break

        if args.warmup:
            print(f"  --- {done} warm-up utterances as asked, measuring ---")
        elif settled:
            print(f"  --- the peak stopped moving after {done} utterances, measuring ---")
        else:
            print(f"  --- THE PEAK NEVER STOPPED MOVING in {done} utterances ---")
            print("      That is already the shape of a leak; the measurement below says how big.")

        base, base_peak = (v / 1024 for v in rss_kb(proc.pid))

        for i in range(args.utterances):
            render(f"This is utterance number {i}, and it is here to be counted.")
            if (i + 1) % 25 == 0:
                sample(f"measured {i + 1}/{args.utterances}")

        end, end_peak = (v / 1024 for v in rss_kb(proc.pid))
        tail = end_peak - base_peak

        print()
        print(f"idle {idle:.0f} MB -> warm {warm:.0f} MB -> "
              f"after {done} warm-up {base:.0f} MB (peak {base_peak:.0f} MB) -> "
              f"after {args.utterances} measured {end:.0f} MB (peak {end_peak:.0f} MB)")
        print(f"peak grew +{base_peak - warm_peak:.0f} MB over {done} warm-up utterances "
              f"and +{tail:.0f} MB over the {args.utterances} measured after them")
        print()

        failed = False
        if warm > args.ceiling_mb:
            print(f"OVER THE CEILING: warm is {warm:.0f} MB against {args.ceiling_mb} MB.")
            print("  This daemon is resident for a whole login session.")
            failed = True
        else:
            print(f"ceiling  ok: warm {warm:.0f} MB <= {args.ceiling_mb} MB")

        # THE CHECK THAT TRAVELS. A ceiling belongs to this machine's models and
        # provider; growth AFTER warm-up belongs to the code, and reads the same
        # on any hardware.
        if tail > TAIL_GROWTH_MB:
            print(f"IT LEAKS: +{tail:.0f} MB over {args.utterances} utterances rendered AFTER the")
            print(f"  heap had finished growing, against a budget of {TAIL_GROWTH_MB} MB — about")
            print(f"  {tail * 1024 / args.utterances:.0f} kB per utterance. This daemon is resident for a whole")
            print("  login session, and the user's report is 'my computer got slow'.")
            failed = True
        else:
            print(f"leak     ok: +{tail:.0f} MB over {args.utterances} measured utterances <= {TAIL_GROWTH_MB} MB")

        return 1 if failed else 0
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()
        shutil.rmtree(run, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
