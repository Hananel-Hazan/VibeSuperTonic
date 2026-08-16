#!/usr/bin/env python3
"""Stress the X11 selection path.

The case that matters most is an owner that disappears between
XGetSelectionOwner and the property read. Xlib's default error handler calls
exit(), so before X11SelectionSource installed its own, this sequence would have
taken the daemon down with no exception and nothing in its log.
"""
import os, subprocess, sys, time, signal

OWN, CTL = sys.argv[1], sys.argv[2]
DPID = int(sys.argv[3])

def daemon_alive():
    try:
        os.kill(DPID, 0)
        return True
    except OSError:
        return False

def ctl(*a, timeout=30):
    r = subprocess.run([CTL, "--no-start", *a], capture_output=True, text=True, timeout=timeout)
    return (r.stdout + r.stderr).strip().splitlines()[-1] if (r.stdout + r.stderr).strip() else ""

def owner(*a):
    p = subprocess.Popen([OWN, *a], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.2)
    return p

def kill(p):
    try:
        p.kill(); p.wait(timeout=5)
    except Exception:
        pass

fail = []

print("=== A · wedged owner: never answers, must time out at ~300 ms ===")
p = owner("--hang", "wedged")
for i in range(4):
    t0 = time.time()
    out = ctl("toggle")
    ms = int((time.time() - t0) * 1000)
    print(f"  {ms:4d} ms : {out[:80]}")
    if "did not answer" not in out:
        fail.append(f"wedged owner not reported as timeout: {out!r}")
    ctl("stop")
kill(p); time.sleep(1)
print(f"  daemon alive: {daemon_alive()}")

print("\n=== B · owner dies mid-capture, 40 rounds (the BadWindow race) ===")
died = 0
for i in range(40):
    p = subprocess.Popen([OWN, f"Race number {i} under test."],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(0.10)
    c = subprocess.Popen([CTL, "--no-start", "toggle"],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    p.kill()                       # vanish while the request is in flight
    try: c.wait(timeout=15)
    except subprocess.TimeoutExpired:
        c.kill(); fail.append(f"round {i}: client hung")
    p.wait(timeout=5)
    if not daemon_alive():
        fail.append(f"DAEMON DIED at owner-death round {i}")
        died = 1
        break
    subprocess.run([CTL, "--no-start", "stop"], capture_output=True, timeout=15)
print(f"  completed, daemon alive: {daemon_alive()}")

print("\n=== C · 50 rapid captures against a live owner ===")
p = owner("Rapid capture test sentence.")
ok = 0
for i in range(50):
    out = ctl("toggle")
    if "speak" in out or "stop" in out or "ignored" in out:
        ok += 1
    ctl("stop")
print(f"  {ok}/50 toggles answered coherently; daemon alive: {daemon_alive()}")
if ok < 50:
    fail.append(f"only {ok}/50 toggles answered")
kill(p); time.sleep(1)

print("\n=== D · selection changes between presses ===")
texts = ["First selection here.", "Second and different.", "A third one entirely."]
for t in texts:
    p = owner(t)
    out = ctl("toggle")
    print(f"  {t[:24]:26} -> {out[:40]}")
    ctl("stop")
    kill(p); time.sleep(0.6)
print(f"  daemon alive: {daemon_alive()}")

print("\n=== E · 200 KB selection, then immediate stop, 10 rounds ===")
p = owner("--size", "200000")
for i in range(10):
    ctl("toggle")
    ctl("stop")
print(f"  daemon alive: {daemon_alive()}")
kill(p)

print("\n=== RESULT ===")
if fail:
    for f in fail: print(f"  FAIL: {f}")
    sys.exit(1)
print("  all X11 selection stress passed")
