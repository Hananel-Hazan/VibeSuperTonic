#!/usr/bin/env python3
"""An X11 selection owner that honours TARGETS, TIMESTAMP and UTF8_STRING.

Exists to test the staleness detection without a human at the keyboard. The
spike's x11-own serves PRIMARY only and this needs CLIPBOARD too, plus the
ability to re-assert ownership on demand -- which is what a real application
does when the user makes a new selection, and whose absence the daemon now
detects.

Single-threaded on purpose. An earlier version served requests from a second
thread, which swallowed the PropertyNotify that server_time() waits for: every
selection was then acquired with CurrentTime, so TIMESTAMP answered 0 and the
freshness test it exists to support could never have passed.

Commands on stdin, one per line:
    primary <text>      take PRIMARY with this text (new ownership timestamp)
    clipboard <text>    take CLIPBOARD with this text
    quit
"""
import os
import select
import sys
import time

from Xlib import display, X, Xatom

d = display.Display()
root = d.screen().root
win = root.create_window(0, 0, 1, 1, 0, X.CopyFromParent,
                         event_mask=X.PropertyChangeMask)

content = {}          # selection atom -> bytes
owned_at = {}         # selection atom -> timestamp at which we acquired it


def handle(e):
    """Answer one SelectionRequest. Ignores everything else."""
    if e.type != X.SelectionRequest:
        return
    sel, target, req = e.selection, e.target, e.requestor
    prop = e.property if e.property != X.NONE else target
    refused = False
    try:
        if target == d.get_atom("TARGETS"):
            atoms = [d.get_atom(n) for n in
                     ("TARGETS", "TIMESTAMP", "UTF8_STRING", "STRING", "TEXT")]
            req.change_property(prop, Xatom.ATOM, 32, atoms)
        elif target == d.get_atom("TIMESTAMP"):
            req.change_property(prop, Xatom.INTEGER, 32, [owned_at.get(sel, 0)])
        elif target in (d.get_atom("UTF8_STRING"), Xatom.STRING, d.get_atom("TEXT")):
            req.change_property(prop, target, 8, content.get(sel, b""))
        else:
            refused = True
    except Exception:
        refused = True

    req.send_event(
        display.event.SelectionNotify(
            time=e.time, requestor=req, selection=sel, target=target,
            property=X.NONE if refused else prop),
        event_mask=0, propagate=False)
    d.flush()


def server_time():
    """A valid server timestamp: X refuses CurrentTime in a selection reply."""
    prop = d.get_atom("VST_TIME_TICK")
    win.change_property(prop, Xatom.STRING, 8, b"x")
    d.flush()
    end = time.time() + 1.0
    while time.time() < end:
        if not d.pending_events():
            time.sleep(0.002)
            continue
        e = d.next_event()
        if e.type == X.PropertyNotify and e.atom == prop:
            return e.time
        handle(e)                      # keep serving while we wait
    return X.CurrentTime


def take(sel_name, text):
    sel = d.get_atom(sel_name)
    t = server_time()
    content[sel] = text.encode("utf-8")
    owned_at[sel] = t
    win.set_selection_owner(sel, t)
    d.flush()
    ok = d.get_selection_owner(sel) == win
    print(f"{'ok' if ok else 'FAILED'} {sel_name} t={t} bytes={len(content[sel])}",
          flush=True)


def commands(buf):
    """Split whole lines out of the pending stdin bytes.

    Read at the fd level rather than with sys.stdin.readline(), because
    select() reports the FD and readline() fills a buffer above it: given two
    commands in one write, the first readline() swallows both into Python's
    buffer, the fd then reports not-readable, and the second command is never
    seen. That silently turned the two-command cases -- the only ones that set
    up a clipboard -- into single-command ones, so the clipboard fallback
    appeared not to work when it had never been given a clipboard.
    """
    *lines, rest = buf.split(b"\n")
    return [l.decode("utf-8", "replace").strip() for l in lines], rest


print("owner ready", flush=True)
xfd = d.fileno()
pending = b""
running = True
while running:
    # Short poll: the requestor gives up after 300 ms, so a sleepy owner would
    # look like the "did not answer in time" case rather than the one under test.
    r, _, _ = select.select([xfd, sys.stdin], [], [], 0.02)

    while d.pending_events():
        handle(d.next_event())

    if sys.stdin in r:
        chunk = os.read(0, 65536)
        if not chunk:
            break
        pending += chunk
        lines, pending = commands(pending)
        for line in lines:
            if not line:
                continue
            if line == "quit":
                running = False
                break
            verb, _, text = line.partition(" ")
            if verb == "primary":
                take("PRIMARY", text)
            elif verb == "clipboard":
                take("CLIPBOARD", text)
            else:
                print(f"unknown: {verb}", flush=True)
