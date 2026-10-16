#!/usr/bin/env python3
"""Stress and fuzz the vibesupertonicd control socket.

Everything here is a thing a real client can do: a hotkey held down, a window
closing mid-transfer, a UI reconnecting, a script sending nonsense. The daemon
has to survive all of it, because it is a long-lived process that owns the
user's speakers.
"""
import json, os, socket, sys, threading, time

SOCK = f"/run/user/{os.getuid()}/vibesupertonic/ctl.sock"

def connect(timeout=5.0):
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.settimeout(timeout)
    s.connect(SOCK)
    return s

def send_raw(payload: bytes, read_reply=True, timeout=5.0):
    """Write bytes, optionally read one line back."""
    try:
        s = connect(timeout)
    except Exception as e:
        return f"CONNECT-FAIL {e}"
    try:
        s.sendall(payload)
        if not read_reply:
            return None
        buf = b""
        while not buf.endswith(b"\n") and len(buf) < 1 << 20:
            chunk = s.recv(65536)
            if not chunk:
                break
            buf += chunk
        return buf.decode("utf-8", "replace").strip()
    except Exception as e:
        return f"IO-FAIL {type(e).__name__}: {e}"
    finally:
        s.close()

def verb(v, **kw):
    msg = {"Verb": v}
    msg.update(kw)
    return send_raw((json.dumps(msg) + "\n").encode())

def alive():
    r = verb("Status")
    return isinstance(r, str) and r.startswith("{")

def check(label):
    ok = alive()
    print(f"  {'ok ' if ok else 'DEAD'} <- {label}")
    return ok

# --------------------------------------------------------------------- fuzz

MALFORMED = [
    (b"\n", "bare newline"),
    (b"{}\n", "empty object"),
    (b"not json at all\n", "not json"),
    (b'{"Verb":"Nope"}\n', "unknown verb"),
    (b'{"Verb":99}\n', "verb as out-of-range number"),
    (b'{"Verb":"Speak"}\n', "speak with no text"),
    (b'{"Verb":"Speak","Text":null}\n', "speak with null text"),
    (b'{"Verb":"Speak","Text":""}\n', "speak with empty text"),
    (b'{"Verb":"Speak","Text":"   "}\n', "speak with whitespace text"),
    (b'{"Verb":"Toggle","Display":"nonsense:99"}\n', "bogus display"),
    (b'{"Verb":"Toggle","Display":""}\n', "empty display"),
    (b'{"Verb":"Toggle","Display":"' + b"A" * 100000 + b'"}\n', "100 KB display string"),
    (b'{"Verb":"Speak","Text":"' + b"x" * 500000 + b'"}\n', "500 KB text"),
    (b'{"Verb":"Speak","Text":"\\ud800"}\n', "lone surrogate"),
    (b'{"Verb":"Speak","Text":"\\u0000null byte"}\n', "embedded NUL"),
    (b'{"Verb":"Status"}' + b"\n" * 50, "many trailing newlines"),
    (b'{"Verb":"Status"}{"Verb":"Status"}\n', "two objects one line"),
    (b'\xff\xfe\x00\x01binary\n', "raw binary"),
    (b'{"Verb":"Status","Extra":{"deep":{"deeper":[1,2,3]}}}\n', "unknown nested field"),
]

def fuzz():
    print("\n=== fuzz: malformed requests ===")
    bad = []
    for payload, label in MALFORMED:
        r = send_raw(payload)
        short = (r or "")[:70]
        print(f"  {label:32} -> {short}")
        if not alive():
            bad.append(label)
            print(f"     *** DAEMON DIED after: {label}")
            return bad
    return bad

def half_open():
    print("\n=== abrupt disconnects ===")
    # Connect and vanish without writing.
    for _ in range(50):
        try:
            connect(2.0).close()
        except Exception as e:
            print(f"  connect failed: {e}")
            break
    check("50 connect-and-close")

    # Write a partial line then vanish.
    for _ in range(50):
        try:
            s = connect(2.0)
            s.sendall(b'{"Verb":"Stat')
            s.close()
        except Exception:
            pass
    check("50 partial-write-and-close")

    # Send a request and never read the reply.
    for _ in range(50):
        send_raw(b'{"Verb":"Status"}\n', read_reply=False)
    check("50 write-without-reading")

def concurrent():
    print("\n=== concurrent clients ===")
    errors = []
    def worker(n):
        for _ in range(20):
            r = verb("Status")
            if not (isinstance(r, str) and r.startswith("{")):
                errors.append((n, r))
    threads = [threading.Thread(target=worker, args=(i,)) for i in range(20)]
    for t in threads: t.start()
    for t in threads: t.join()
    print(f"  400 status calls across 20 threads, {len(errors)} failures")
    if errors:
        print(f"    first: {errors[0]}")
    check("concurrent status")
    return errors

def toggle_storm(n=120):
    print(f"\n=== toggle storm ({n} presses, no delay) ===")
    actions = {}
    for _ in range(n):
        r = verb("Toggle", Display=os.environ.get("DISPLAY"))
        try:
            a = json.loads(r).get("Action", "?") if r.startswith("{") else "ERR"
        except Exception:
            a = "ERR"
        actions[a] = actions.get(a, 0) + 1
    print(f"  {actions}")
    check("toggle storm")
    verb("Stop")
    return actions

def subscriber_churn(seconds=6):
    print(f"\n=== subscriber churn while speaking ({seconds}s) ===")
    stop = threading.Event()
    counts = {"opened": 0, "events": 0, "errors": 0}
    def churn():
        while not stop.is_set():
            try:
                s = connect(3.0)
                s.sendall(b'{"Verb":"Subscribe"}\n')
                counts["opened"] += 1
                s.recv(65536)          # snapshot
                time.sleep(0.02)
                s.close()              # vanish mid-stream
            except Exception:
                counts["errors"] += 1
    threads = [threading.Thread(target=churn) for _ in range(6)]
    for t in threads: t.start()
    deadline = time.time() + seconds
    while time.time() < deadline:
        verb("Toggle", Display=os.environ.get("DISPLAY"))
        time.sleep(0.5)
        verb("Stop")
        time.sleep(0.2)
    stop.set()
    for t in threads: t.join()
    print(f"  subscribers opened/abandoned: {counts['opened']}, connect errors: {counts['errors']}")
    check("subscriber churn")

def stop_during_render():
    print("\n=== stop immediately after speak, repeatedly ===")
    for i in range(25):
        verb("Speak", Text="The sea is everything. It covers seven tenths of the globe.")
        time.sleep(0.02 * (i % 5))     # vary where in the render the stop lands
        verb("Stop")
    check("25 speak/stop races")

def pause_resume_race():
    print("\n=== pause/resume race ===")
    verb("Speak", Text="A somewhat longer sentence so that there is audio to pause and resume.")
    for _ in range(40):
        verb("Pause")
        verb("Resume")
    verb("Stop")
    check("pause/resume storm")

if __name__ == "__main__":
    if not alive():
        print("daemon not responding; start it first")
        sys.exit(1)
    print(f"daemon up: {verb('Status')}")

    failures = []
    if fuzz(): failures.append("fuzz")
    half_open()
    if concurrent(): failures.append("concurrent")
    toggle_storm()
    stop_during_render()
    pause_resume_race()
    subscriber_churn()

    print("\n=== final ===")
    ok = check("still alive at the end")
    print("RESULT:", "PASS" if ok and not failures else f"PROBLEMS: {failures or 'daemon died'}")
    sys.exit(0 if ok and not failures else 1)
