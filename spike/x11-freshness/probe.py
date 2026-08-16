#!/usr/bin/env python3
"""Report who owns the X11 PRIMARY selection, what it offers, and what it holds.

Answers the question the daemon cannot: when a hotkey press reads stale text,
is that because the product misread PRIMARY, or because the application never
took ownership and PRIMARY still legitimately belongs to whoever set it last?
"""
import sys, time
from Xlib import display, X, Xatom

d = display.Display()
root = d.screen().root
win = root.create_window(0, 0, 1, 1, 0, X.CopyFromParent)


def atom(name):
    return d.get_atom(name)


def owner_info():
    o = d.get_selection_owner(Xatom.PRIMARY)
    if o == X.NONE or o is None:
        return None, "<no owner>", None
    cls = title = None
    w = o
    for _ in range(6):                      # owner is often an unmapped child
        try:
            cls = cls or w.get_wm_class()
            n = w.get_full_property(atom("_NET_WM_NAME"), 0)
            if n and not title:
                title = n.value.decode("utf-8", "replace")
        except Exception:
            pass
        if cls and title:
            break
        try:
            w = w.query_tree().parent
        except Exception:
            break
        if not w:
            break
    return o, (cls[1] if cls else "<unknown>"), title


def fetch(target_name, timeout=1.0):
    prop = atom("VST_PROBE")
    win.convert_selection(Xatom.PRIMARY, atom(target_name), prop, X.CurrentTime)
    d.flush()
    end = time.time() + timeout
    while time.time() < end:
        e = d.pending_events() and d.next_event() or None
        if e is None:
            time.sleep(0.02)
            continue
        if e.type == X.SelectionNotify:
            # Match the reply to THIS request. Without the target check, the
            # SelectionNotify still queued from the previous convert_selection
            # is consumed here and its property read as the wrong type -- an
            # ATOM array decoded as UTF-8 looks like plausible garbage text.
            if e.target != atom(target_name):
                continue
            if e.property == X.NONE:
                return None
            r = win.get_full_property(prop, X.AnyPropertyType)
            win.delete_property(prop)
            return r
    return None


def targets():
    r = fetch("TARGETS")
    if not r:
        return []
    names = []
    for a in r.value:
        try:
            names.append(d.get_atom_name(a))
        except Exception:
            pass
    return names


def text():
    for t in ("UTF8_STRING", "STRING"):
        r = fetch(t)
        if r and r.value:
            v = r.value
            if isinstance(v, bytes):
                return t, v.decode("utf-8", "replace")
            return t, str(v)
    return None, None


def snapshot():
    o, cls, title = owner_info()
    tg = targets() if o else []
    tname, txt = text() if o else (None, None)
    return {
        "owner_id": hex(o.id) if o else None,
        "class": cls,
        "title": (title or "")[:60],
        "targets": tg,
        "target_used": tname,
        "chars": len(txt) if txt else 0,
        "text": " ".join(txt.split())[:90] if txt else "",
    }


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "watch":
        prev = None
        print("watching PRIMARY ownership — select text in an app", flush=True)
        while True:
            s = snapshot()
            key = (s["owner_id"], s["chars"], s["text"])
            if key != prev:
                prev = key
                utf8 = "UTF8_STRING" in s["targets"]
                print(
                    f"OWNER {s['class']} ({s['owner_id']}) "
                    f"target={s['target_used']} utf8_offered={utf8} "
                    f"chars={s['chars']} :: {s['text'][:60]}",
                    flush=True,
                )
            time.sleep(0.4)
    else:
        s = snapshot()
        for k, v in s.items():
            print(f"{k:12} {v}")
