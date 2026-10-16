#!/usr/bin/env python3
"""Why did the clipboard fallback not fire?

On every hotkey press, print the three numbers the decision actually turns on,
for BOTH selections: who owns it, when they claimed it, and how many bytes it
holds. No selection text is read or printed.

The branch is: PRIMARY unchanged since the daemon's previous read AND clipboard
claimed later than PRIMARY -> read clipboard.
"""
import os
import pathlib
import subprocess
import threading
import time

from Xlib import display, X, Xatom

# Whichever client is talking to the daemon under test -- an installed portable
# folder as often as the repo build, since this is meant to be usable against a
# copy someone is actually running.
CTL = os.environ.get("VST_CTL") or str(
    pathlib.Path(__file__).resolve().parents[2]
    / "src/VibeSuperTonic.Ctl/bin/Release/net10.0/vst-ctl")

d = display.Display()
win = d.screen().root.create_window(0, 0, 1, 1, 0, X.CopyFromParent)


def convert(sel_name, target, timeout=1.0):
    sel, tgt = d.get_atom(sel_name), d.get_atom(target)
    prop = d.get_atom("WHY_" + target)
    win.convert_selection(sel, tgt, prop, X.CurrentTime)
    d.flush()
    end = time.time() + timeout
    while time.time() < end:
        if not d.pending_events():
            time.sleep(0.01)
            continue
        e = d.next_event()
        if e.type == X.SelectionNotify and e.target == tgt and e.selection == sel:
            if e.property == X.NONE:
                return None
            r = win.get_full_property(prop, X.AnyPropertyType)
            win.delete_property(prop)
            return r
    return "timeout"


def snap(sel_name):
    o = d.get_selection_owner(d.get_atom(sel_name))
    if o == X.NONE or o is None:
        return f"{sel_name}: <no owner>"
    ts = convert(sel_name, "TIMESTAMP")
    txt = convert(sel_name, "UTF8_STRING")
    tsv = ts.value[0] if hasattr(ts, "value") and len(ts.value) else ts
    n = len(txt.value) if hasattr(txt, "value") else txt
    return f"{sel_name}: owner={hex(o.id)} claimed_at={tsv} bytes={n}"


lock = threading.Lock()


def on_press():
    with lock:
        print("PRESS", flush=True)
        print("   " + snap("PRIMARY"), flush=True)
        print("   " + snap("CLIPBOARD"), flush=True)


p = subprocess.Popen([CTL, "subscribe"], stdout=subprocess.PIPE,
                     stderr=subprocess.DEVNULL, text=True, bufsize=1)
for line in p.stdout:
    if '"State":"Preparing"' in line:
        on_press()
