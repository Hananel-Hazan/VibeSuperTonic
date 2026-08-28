namespace VibeSuperTonic.Core.SpeechD;

/// <summary>
/// The escaping speech-dispatcher applies to the samples inside a <c>705</c>
/// audio block.
///
/// <para><b>This is the detail that cost the gate two days.</b> An audio block is
/// terminated by a newline, so a newline <em>inside</em> the samples ends it
/// early — and <c>0x0A</c> turns up in virtually any real audio within
/// milliseconds. speech-dispatcher escapes it, and the escape byte itself, by
/// emitting <c>0x7D</c> followed by the original with bit 5 inverted; the server
/// undoes it on the way in.</para>
///
/// <para><b>Omitting it raises no error anywhere.</b> The server reads a
/// truncated block, plays it, and then waits forever for an end-of-utterance
/// that has been desynchronised into the sample data — which reaches a user as a
/// screen reader that says one thing and then goes quiet for good. The first
/// diagnosis blamed the terminator and varied it twice; the terminator was never
/// the problem. docs/SPEECHD-PLAN.md, trap 17.</para>
///
/// <para>Read out of <c>module_tts_output_send_server</c> in
/// <c>src/modules/module_process.c</c>, <b>byte-identical in 0.11.1 and
/// 0.12.1</b> — so there is no per-distro variant of this to negotiate.</para>
/// </summary>
public static class Hdlc
{
    /// <summary>The escape byte. Also the one other byte that must be escaped.</summary>
    public const byte EscapeByte = 0x7D;

    /// <summary>Bit 5, flipped in the escaped byte so the escape is reversible.</summary>
    public const byte Invert = 0x20;

    /// <summary>The byte that would otherwise end the block early.</summary>
    public const byte Newline = 0x0A;

    /// <summary>True when <paramref name="b"/> cannot travel unescaped.</summary>
    public static bool NeedsEscape(byte b) => b == Newline || b == EscapeByte;

    /// <summary>
    /// Escape <paramref name="pcm"/> for transport inside an audio block.
    /// </summary>
    public static byte[] Escape(ReadOnlySpan<byte> pcm)
    {
        int extra = 0;
        foreach (byte b in pcm)
            if (NeedsEscape(b)) extra++;

        // The common case: nothing to escape, so nothing is copied twice.
        var outBytes = new byte[pcm.Length + extra];
        int at = 0;

        foreach (byte b in pcm)
        {
            if (NeedsEscape(b))
            {
                outBytes[at++] = EscapeByte;
                outBytes[at++] = (byte)(b ^ Invert);
            }
            else
            {
                outBytes[at++] = b;
            }
        }

        return outBytes;
    }

    /// <summary>
    /// Undo <see cref="Escape"/>. Not used in the module — the server does this
    /// end — but the tests need it, and a round trip is the only check of an
    /// escape that is worth anything.
    /// </summary>
    public static byte[] Unescape(ReadOnlySpan<byte> escaped)
    {
        var outBytes = new byte[escaped.Length];
        int at = 0;

        for (int i = 0; i < escaped.Length; i++)
        {
            if (escaped[i] == EscapeByte && i + 1 < escaped.Length)
                outBytes[at++] = (byte)(escaped[++i] ^ Invert);
            else
                outBytes[at++] = escaped[i];
        }

        return outBytes[..at];
    }
}
