using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The wire format, which is a compatibility surface whether or not it was meant
/// to be one: the moment a user writes a script against it, changing it breaks
/// something outside this repo.
/// </summary>
public class ProtocolTests
{
    [Fact]
    public void A_request_survives_the_round_trip()
    {
        var sent = new Request { Verb = RequestVerb.Speak, Text = "Hello world.", Voice = "M1", Language = "en" };
        var back = Protocol.TryDecode<Request>(Protocol.Encode(sent));

        Assert.Equal(sent, back);
    }

    [Fact]
    public void Verbs_and_states_go_over_the_wire_as_names()
    {
        // Numbers would survive the round trip too, and would silently change
        // meaning the first time someone reorders an enum — while remaining
        // unreadable in a log and unscriptable with jq.
        string line = Protocol.Encode(new Request { Verb = RequestVerb.Subscribe });
        Assert.Contains("\"Subscribe\"", line);
        Assert.DoesNotContain("\"verb\":6", line, StringComparison.OrdinalIgnoreCase);

        string status = Protocol.Encode(new Response
        {
            Ok = true,
            Status = new StatusPayload(SpeechState.Speaking, false, true, "M1", "en", "0.2.7.5"),
        });
        Assert.Contains("\"Speaking\"", status);
    }

    [Fact]
    public void Unset_fields_are_omitted_rather_than_sent_as_null()
    {
        string line = Protocol.Encode(new Request { Verb = RequestVerb.Stop });

        Assert.DoesNotContain("null", line);
        Assert.DoesNotContain("text", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void One_message_is_one_line()
    {
        // The framing IS the newline. A message containing a raw newline would
        // desynchronise the stream for everything after it.
        string line = Protocol.Encode(new Request
        {
            Verb = RequestVerb.Speak,
            Text = "First line.\nSecond line.\r\nThird.",
        });

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Equal("First line.\nSecond line.\r\nThird.",
            Protocol.TryDecode<Request>(line)!.Text);
    }

    [Fact]
    public void Field_names_are_matched_case_insensitively()
    {
        // Hand-written clients are an intended use of a line protocol, and
        // "verb" versus "Verb" is not a distinction worth failing over.
        var r = Protocol.TryDecode<Request>("""{"verb":"toggle"}""");

        Assert.NotNull(r);
        Assert.Equal(RequestVerb.Toggle, r!.Verb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"verb\":\"nonsense\"}")]
    [InlineData("{\"verb\":")]
    [InlineData("[1,2,3]")]
    public void Rubbish_decodes_to_null_instead_of_throwing(string line)
    {
        // The daemon reads from a socket any local process can connect to. A
        // malformed line is a client bug or a port scan; neither should end a
        // speech session.
        Assert.Null(Protocol.TryDecode<Request>(line));
    }

    [Fact]
    public void A_session_event_survives_the_round_trip()
    {
        var sent = new SessionEvent
        {
            Kind = SessionEventKind.WordBoundary,
            SourceOffset = 42,
            SourceLength = 2,
            AudioSeconds = 1.75,
        };

        var back = Protocol.TryDecode<SessionEvent>(Protocol.Encode(sent));
        Assert.Equal(sent, back);
    }

    [Fact]
    public void The_socket_path_is_per_user_and_under_the_runtime_directory()
    {
        // XDG_RUNTIME_DIR is set here rather than inherited. Reading whatever
        // the environment happened to provide made this test assert the runtime
        // directory branch on a developer box and the /tmp fallback branch on a
        // Windows CI runner, where the variable is never set -- and the fallback
        // directory is named vibesupertonic-<user>, so the assertion below could
        // not match it. The test named the branch it wanted; now it selects it.
        string? saved = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string runtime = Path.Combine(Path.GetTempPath(), "vst-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtime);
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime);
            string path = Protocol.SocketPath();

            Assert.StartsWith(runtime, path);
            Assert.EndsWith(Path.Combine(Protocol.SocketDirName, Protocol.SocketFileName), path);
            Assert.True(Path.IsPathRooted(path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", saved);
            Directory.Delete(runtime, recursive: true);
        }
    }

    [Fact]
    public void The_socket_path_falls_back_when_XDG_RUNTIME_DIR_is_unset()
    {
        // Unset in some su'd and container sessions. The daemon has to still
        // come up there, so the fallback is tested rather than assumed.
        string? saved = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);
            string path = Protocol.SocketPath();

            Assert.EndsWith(Protocol.SocketFileName, path);

            // Asserted on the directory name, not on the whole path: a Windows
            // temp path is under C:\Users\<user>\AppData\Local\Temp, so a
            // Contains(UserName) over the whole string passes there whether or
            // not the fallback is per-user at all.
            Assert.Equal($"{Protocol.SocketDirName}-{Environment.UserName}",
                         Path.GetFileName(Path.GetDirectoryName(path)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", saved);
        }
    }

    [Fact]
    public void A_toggle_carries_the_clients_display()
    {
        // Phase 4 reads the X11 PRIMARY selection from this, and it has to come
        // from the client because the daemon's own environment is whatever it
        // was started with and cannot change afterwards. A daemon started by
        // systemd --user has no $DISPLAY and never will.
        var sent = new Request { Verb = RequestVerb.Toggle, Display = ":0" };
        var back = Protocol.TryDecode<Request>(Protocol.Encode(sent));

        Assert.Equal(":0", back!.Display);
    }

    [Fact]
    public void A_notice_rides_on_a_successful_response_without_becoming_an_error()
    {
        // R-9 truncates a selection at 100 KB and the plan says to report what
        // was dropped. That is neither a failure nor silence, and a response
        // that could only say Ok or Error had nowhere to put it.
        var sent = new Response { Ok = true, Notice = "selection truncated at 100 KB" };
        var back = Protocol.TryDecode<Response>(Protocol.Encode(sent))!;

        Assert.True(back.Ok);
        Assert.Null(back.Error);
        Assert.Equal(sent.Notice, back.Notice);
    }

    [Fact]
    public void New_protocol_fields_reach_the_source_generated_serializer()
    {
        // The trap this pins: NativeAOT disables reflection-based serialization,
        // so a property the generated context does not know about is dropped on
        // the shipped vst-ctl and present everywhere else. Encoding through
        // Protocol.Encode — which goes via ProtocolJson, not the plain options —
        // is what makes this test able to see the difference.
        string line = Protocol.Encode(new Request { Verb = RequestVerb.Toggle, Display = ":0" });
        Assert.Contains("Display", line, StringComparison.OrdinalIgnoreCase);

        string reply = Protocol.Encode(new Response { Ok = true, Notice = "truncated" });
        Assert.Contains("Notice", reply, StringComparison.OrdinalIgnoreCase);

        // Phase 4b's additions. ConfigPayload is a new type rather than a new
        // property, which is the case that fails hardest under AOT: a type the
        // generated context has never heard of throws NotSupportedException on
        // the shipped binary and serializes perfectly in every test that does
        // not go through ProtocolJson.
        string config = Protocol.Encode(new Response
        {
            Ok = true,
            Config = new ConfigPayload(
                BaseDir: "/opt/vst", DataDir: "/opt/vst/data", ModelsRoot: "/opt/vst/models",
                DataDirWritable: true, SettingsFound: true, PronunciationsFound: false,
                RuleCount: 2, RulesEnabled: true, Voice: "M1", Language: "en",
                TotalStep: 8, MaxChunkChars: 200, MinChunkChars: 100,
                InterChunkSilenceMs: 200,
                Notes: new[] { "a note" }),
        });
        Assert.Contains("DataDir", config, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/opt/vst/models", config, StringComparison.Ordinal);
        Assert.Contains("a note", config, StringComparison.Ordinal);

        // SessionEvent.Notice — a new property on a type the context already
        // knows, which is the quieter half of the same trap. It cannot throw the
        // way an unknown type does; it simply does not appear on the wire, so the
        // tray shows nothing and there is no error anywhere to explain it.
        string prepared = Protocol.Encode(
            SessionEvent.Preparing("The sea is everything.", 0, "selection truncated at 100 KB."));
        Assert.Contains("Notice", prepared, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("truncated", prepared, StringComparison.Ordinal);

        var back = Protocol.TryDecode<SessionEvent>(prepared);
        Assert.Equal("selection truncated at 100 KB.", back!.Notice);

        // And absent when there is nothing to say — WhenWritingNull is what keeps
        // the stream readable with jq.
        Assert.DoesNotContain("Notice",
            Protocol.Encode(SessionEvent.Preparing("Hello.", 0)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_new_verbs_round_trip_by_name()
    {
        // Enums cross the wire as names so reordering the enum cannot silently
        // change what a client asked for.
        foreach (var verb in new[] { RequestVerb.Reload, RequestVerb.Config, RequestVerb.Read })
        {
            string line = Protocol.Encode(new Request { Verb = verb });
            Assert.Contains(verb.ToString(), line, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(verb, Protocol.TryDecode<Request>(line)!.Verb);
        }
    }
}
