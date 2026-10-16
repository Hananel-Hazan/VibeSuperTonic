#!/usr/bin/env python3
"""Log every byte speech-dispatcher sends a module, and answer LIST VOICES with
a deliberately varied list, so we can see what comes back when a client selects
one of them."""
import os, sys

LOG = open(os.environ["PROBE_LOG"], "w", buffering=1)
OUT, IN = sys.stdout.buffer, sys.stdin.buffer

VOICES = [
    ("supertonic",          "en", "MALE1"),
    ("supertonic-female",   "en", "FEMALE1"),
    ("en_US-lessac-medium", "en-US", "FEMALE2"),
    ("de_DE-thorsten-low",  "de", "MALE2"),
    ("echo",                "en", "MALE3"),
]

def log(m): LOG.write(m + "\n")
def send(t):
    log("<< " + t.replace("\n", "\\n"))
    OUT.write((t + "\n").encode()); OUT.flush()

def read_block():
    """Parameter lines until a lone dot."""
    lines = []
    while True:
        raw = IN.readline()
        if not raw: return lines
        line = raw.decode("utf-8", "replace").rstrip("\r\n")
        log(">>   " + line)
        if line == ".": return lines
        lines.append(line)

while True:
    raw = IN.readline()
    if not raw: break
    cmd = raw.decode("utf-8", "replace").rstrip("\r\n")
    log(">> " + cmd)

    if cmd == "INIT":
        send("299-probe ready"); send("299 OK LOADED SUCCESSFULLY")
    elif cmd == "AUDIO":
        send("207 OK RECEIVING AUDIO SETTINGS"); read_block(); send("203 OK AUDIO INITIALIZED")
    elif cmd == "SET":
        send("203 OK RECEIVING SETTINGS"); read_block(); send("203 OK SETTINGS RECEIVED")
    elif cmd == "LOGLEVEL":
        send("207 OK RECEIVING LOGLEVEL SETTINGS"); read_block(); send("203 OK LOGLEVEL SET")
    elif cmd.startswith("LIST VOICES"):
        for name, lang, var in VOICES:
            send(f"200-{name}\t{lang}\t{var}")
        send("200 OK VOICE LIST SENT")
    elif cmd in ("SPEAK", "CHAR", "KEY", "SOUND_ICON"):
        send("202 OK RECEIVING MESSAGE"); read_block()
        send("200 OK SPEAKING"); send("701 BEGIN"); send("702 END")
    elif cmd == "STOP":
        send("703 STOP")
    elif cmd == "QUIT":
        send("210 OK QUIT"); break
    else:
        send("300 ERROR UNKNOWN COMMAND")
