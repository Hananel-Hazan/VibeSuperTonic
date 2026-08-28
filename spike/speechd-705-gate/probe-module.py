#!/usr/bin/env python3
"""A speech-dispatcher output module that exists to answer one question:

does the oldest supported distro's speech-dispatcher — Ubuntu 22.04, 0.11.1 —
accept audio handed back with `705 AUDIO`, or must a module open its own device?

It speaks the module protocol on stdin/stdout, logs every byte the server sends,
and answers a SPEAK with a 300 ms sine returned as a 705 block. Nothing in it is
VibeSuperTonic-specific on purpose: if this works, route B's module can.

THE COMMAND SHAPE, measured rather than assumed. A command arrives on one line
and is answered IMMEDIATELY; its parameters follow and are terminated by a lone
'.', which is answered a SECOND time. The server does not send the parameters
until the first reply arrives — so a module that reads the block before replying
deadlocks, and one that replies twice out of order desynchronises the stream and
takes the whole server down with it.
"""
import math
import os
import struct
import sys

LOG = open(os.environ.get("PROBE_LOG", "/gate/out/probe-recv.log"), "w", buffering=1)
OUT = sys.stdout.buffer
IN = sys.stdin.buffer

SR = 22050

# command -> (reply to the command, reply after its parameter block ends)
COMMANDS = {
    "INIT": ("299 OK LOADED SUCCESSFULLY", None),
    "AUDIO": ("203 OK AUDIO INITIALIZED", "203 OK AUDIO INITIALIZED"),
    "SET": ("203 OK SETTINGS RECEIVED", "203 OK SETTINGS RECEIVED"),
    "LOGLEVEL": ("203 OK LOGLEVEL SET", "203 OK LOGLEVEL SET"),
    "SPEAK": ("202 OK RECEIVING MESSAGE", "200 OK SPEAKING"),
    "CHAR": ("202 OK RECEIVING MESSAGE", "200 OK SPEAKING"),
    "KEY": ("202 OK RECEIVING MESSAGE", "200 OK SPEAKING"),
    "SOUND_ICON": ("202 OK RECEIVING MESSAGE", "200 OK SPEAKING"),
    "LIST VOICES": ("200-probe\ten\tnone\n200 OK VOICE LIST SENT", None),
    "STOP": ("703 STOP", None),
    "PAUSE": ("704 PAUSE", None),
}


def log(msg):
    LOG.write(msg + "\n")


def send(text):
    log("<< " + text.replace("\n", "\\n"))
    OUT.write((text + "\n").encode())
    OUT.flush()


def audio_block(ms=300, freq=440):
    """The thing being tested: audio handed back instead of played."""
    n = int(SR * ms / 1000)
    pcm = b"".join(
        struct.pack("<h", int(8000 * math.sin(2 * math.pi * freq * i / SR)))
        for i in range(n))
    hdr = (f"705-bits=16\n705-num_channels=1\n705-sample_rate={SR}\n"
           f"705-num_samples={n}\n705-big_endian=0\n705-AUDIO\n")
    log(f"<< 705 block: {n} samples, {len(pcm)} bytes of PCM")
    OUT.write(hdr.encode())
    OUT.write(pcm)
    OUT.write(b"\n705 AUDIO\n")
    OUT.flush()


pending = None
speaking = False

while True:
    raw = IN.readline()
    if not raw:
        log(">> stdin closed")
        break
    line = raw.decode("utf-8", "replace").rstrip("\r\n")
    log(">> " + line)

    if line == "QUIT":
        send("210 OK QUIT")
        break

    if line == ".":
        # End of a parameter block or of message data.
        if pending and COMMANDS[pending][1]:
            send(COMMANDS[pending][1])
        if speaking:
            send("701 BEGIN")
            audio_block()
            send("702 END")
            speaking = False
        pending = None
        continue

    if line in COMMANDS:
        pending = line
        speaking = line in ("SPEAK", "CHAR", "KEY", "SOUND_ICON")
        send(COMMANDS[line][0])
        continue

    # A parameter or a line of message data: logged, never answered. The
    # audio_output_method the server names here IS the gate's answer.
    if line.startswith("audio_output_method"):
        log("== SERVER-SIDE AUDIO NEGOTIATION: " + line)
