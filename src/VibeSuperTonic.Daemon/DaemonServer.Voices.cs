using System.Text;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Models;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// The three voice verbs — P4's half of the daemon.
///
/// <para><b>Verbs before tabs</b>, the rule <c>benchmark</c> was landed under and
/// the reason Phase 8 was resequenced when it turned out a button had been wired
/// to nothing. All three of these have to work over ssh on a machine with no
/// window, and the Voices tab calls exactly them rather than reaching into the
/// store itself.</para>
///
/// <para>A partial of <see cref="DaemonServer"/> rather than a service of its
/// own, because every one of them needs the same three things the rest of the
/// server needs — the live config, the engine router, and the session's idea of
/// whether it is busy — and threading those through a second object would buy
/// nothing but a constructor.</para>
/// </summary>
public sealed partial class DaemonServer
{
    /// <summary>
    /// The catalog, loaded once and kept. It is a shipped file next to the
    /// executable that cannot change without the program being replaced, so
    /// re-reading it per request would be a syscall in service of nothing —
    /// unlike <c>settings.json</c>, which a user edits while the daemon runs.
    /// </summary>
    private PiperCatalog? _catalog;
    private bool _catalogLoaded;

    /// <summary>One install at a time. Two concurrent fetches of the same voice would race on one directory.</summary>
    private int _installing;

    private PiperCatalog? Catalog()
    {
        if (_catalogLoaded) return _catalog;
        _catalog = PiperCatalog.TryLoad(LinuxDataPaths.BaseDir);
        _catalogLoaded = true;
        return _catalog;
    }

    private PiperVoiceInstaller Installer() => new(_config.ModelsRoot);

    /// <summary>
    /// Write one reply while holding the writer's lock.
    ///
    /// <para>Synchronous, and it has to be: the same <see cref="StreamWriter"/> is
    /// written from the downloader's own thread through the progress callbacks,
    /// so every write takes one lock — and C# will not let you await inside one.
    /// <c>AutoFlush</c> is on, so this is the same syscall the async form would
    /// have made.</para>
    /// </summary>
    private static void WriteLocked(StreamWriter writer, object gate, Response response)
    {
        lock (gate)
        {
            try { writer.WriteLine(Protocol.Encode(response)); }
            catch (IOException) { /* the client went away mid-install */ }
        }
    }

    // ------------------------------------------------------------------ voices

    private Response Voices()
    {
        RefreshConfig(force: false);

        var installer = Installer();
        var catalog = Catalog();
        var notes = new List<string>();

        var def = VoiceId.Parse(EffectiveVoice);

        // The prefix wins over the store. `supertonic:M1` means Supertonic even
        // on a machine where a Piper voice happens to be installed under the
        // name M1 — which is the entire reason the qualified form exists, and
        // asking IsPiperVoice(def.Bare) on its own would throw it away here.
        bool defaultIsPiper = def.MayBePiper && IsPiperVoice(def.Bare);

        var installed = new List<VoiceEntry>();

        var styles = SupertonicStyles();

        // Is the configured default a Supertonic style that actually exists?
        // Asked separately from "is it a Piper voice" because the third answer —
        // NEITHER — is a real state and used to be invisible. Removing the voice
        // named as the default left the setting pointing at nothing, and the row
        // was built from def.Bare regardless, so the list cheerfully reported
        // `supertonic:ca_ES-upc_ona-x_low` as the default: a style that does not
        // exist, marked as the one the next press would use. The next press would
        // in fact have failed.
        bool defaultIsStyle = !defaultIsPiper
            && styles.Contains(def.Bare, StringComparer.OrdinalIgnoreCase);

        // Supertonic as ONE row. It is a handful of styles over a shared 383 MB
        // model set rather than a handful of downloads, so listing it as one row
        // per style with a size each would state something false about both the
        // disk and the choice.
        if (styles.Count > 0)
        {
            installed.Add(new VoiceEntry(
                // The style in force, or the one REMEMBERED while a Piper voice
                // holds the default. DefaultVoice keeps the last Supertonic style
                // for exactly this reason — SetVoice writes it only for a
                // Supertonic voice — so pressing Use on this row takes the user
                // back to their own style rather than to whichever sorts first.
                Id: VoiceId.ForSupertonic(
                        defaultIsStyle ? def.Bare
                        : styles.FirstOrDefault(
                            st => string.Equals(st, _config.Settings.DefaultVoice, StringComparison.OrdinalIgnoreCase))
                          ?? styles[0]).ToString(),
                Engine: "supertonic",
                Name: "Supertonic",
                Installed: true,
                IsDefault: defaultIsStyle,
                Language: $"{SupertonicLanguages.All.Length} languages",
                SampleRate: 44100,
                Licence: "OpenRAIL-M",
                LicenceClass: "openrail-m",
                Speakers: styles.Count,
                SpeakerNames: styles));
        }
        else
        {
            notes.Add($"no voice_styles/ under {_config.ModelsRoot} — Supertonic is not installed here.");
        }

        if (!defaultIsPiper && !defaultIsStyle)
        {
            notes.Add($"the configured default voice '{EffectiveVoice}' is not installed — " +
                      "the next press will refuse until another is chosen.");
        }

        foreach (string id in installer.Installed())
        {
            var entry = catalog?.Find(id);
            bool isDefault = defaultIsPiper
                && string.Equals(def.Bare, id, StringComparison.OrdinalIgnoreCase);

            installed.Add(new VoiceEntry(
                // WITH THE CONFIGURED SPEAKER, when this is the voice in force.
                // Supertonic's row has always carried the style that is chosen;
                // a Piper row carrying no speaker means the id says something
                // different from what the daemon will actually say, and the
                // Voices tab — which builds its speaker dropdown FROM this id —
                // showed speaker 0 for a voice configured with speaker 6. Chosen
                // 6, pressed Use, watched it snap back, reported 2026-08-28.
                //
                // Only for the default row: an installed voice nobody has chosen
                // has no speaker, and inventing 0 for it would claim a choice
                // that was never made.
                Id: VoiceId.ForPiper(id).WithSpeaker(isDefault ? def.Speaker : null).ToString(),
                Engine: "piper",
                Name: entry?.Name ?? id,
                Installed: true,
                IsDefault: isDefault,
                LanguageCode: entry?.Language.Code,
                Language: entry?.Language.Display(),
                Quality: entry?.Quality,
                // The config on disk is the authority for an installed voice; the
                // catalog may not carry it at all. en_US-lessac is the live
                // example — hand-installed, speaks perfectly, and excluded from
                // the catalog because its terms are a bespoke licence page.
                SampleRate: _config.PiperVoices.Config(id)?.SampleRate ?? entry?.SampleRate,
                Bytes: installer.BytesOnDisk(id),
                Licence: entry?.Licence.Name ?? "not in the catalog — installed by hand",
                LicenceClass: entry?.Licence.Class,
                LicenceUrl: entry?.Licence.Url,
                Speakers: entry?.Speakers ?? _config.PiperVoices.Config(id)?.NumSpeakers,
                SpeakerNames: entry?.SpeakerNames,
                Calibrated: _config.PiperVoices.Calibration(id) is not null));
        }

        var available = new List<VoiceEntry>();
        if (catalog is null)
        {
            notes.Add($"no {PiperCatalog.FileName} beside {LinuxDataPaths.BaseDir} — " +
                      "nothing can be offered for download, but installed voices still work.");
        }
        else
        {
            foreach (var v in catalog.Voices.Where(v => !installer.IsInstalled(v.Id)))
            {
                available.Add(new VoiceEntry(
                    Id: v.Qualified().ToString(),
                    Engine: "piper",
                    Name: v.Name,
                    Installed: false,
                    IsDefault: false,
                    LanguageCode: v.Language.Code,
                    Language: v.Language.Display(),
                    Quality: v.Quality,
                    SampleRate: v.SampleRate,
                    Bytes: v.Bytes,
                    Licence: v.Licence.Name,
                    LicenceClass: v.Licence.Class,
                    LicenceUrl: v.Licence.Url,
                    Speakers: v.Speakers,
                    SpeakerNames: v.SpeakerNames));
            }
        }

        return new Response
        {
            Ok = true,
            Voices = new VoicesPayload(
                installed, available, installer.StoreRoot,
                catalog?.Source?.Revision,
                notes.Count > 0 ? notes : null),
        };
    }

    /// <summary>
    /// Supertonic's styles, from the filenames in <c>voice_styles/</c>. There is
    /// no manifest of them and never has been — the engine resolves
    /// <c>&lt;style&gt;.json</c> by name — so the directory is the list.
    /// </summary>
    private IReadOnlyList<string> SupertonicStyles()
    {
        try
        {
            string dir = Path.Combine(_config.ModelsRoot, "voice_styles");
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private bool IsPiperVoice(string bareId) => _config.PiperVoices.ModelPath(bareId) is not null;

    // ----------------------------------------------------------------- install

    /// <summary>
    /// Download one catalog voice, reporting progress as it goes, then calibrate
    /// it in the background.
    ///
    /// <para><b>Multi-reply, like the sweep.</b> Same contract, so a client reads
    /// lines until one arrives without <see cref="Response.VoiceProgress"/> set.
    /// A 137 MB voice is minutes on a domestic link and there is nothing else the
    /// user can be told in the meantime.</para>
    /// </summary>
    private async Task VoiceInstallAsync(StreamWriter writer, Request request, CancellationToken token)
    {
        RefreshConfig(force: false);

        if (string.IsNullOrWhiteSpace(request.Voice))
        {
            await WriteAsync(writer, Response.Fail("voice install needs a voice id"));
            return;
        }

        var wanted = VoiceId.Parse(request.Voice);
        if (Catalog() is not { } catalog)
        {
            await WriteAsync(writer, Response.Fail(
                $"no {PiperCatalog.FileName} beside {LinuxDataPaths.BaseDir} — " +
                "this install has no catalog to download from"));
            return;
        }

        if (catalog.Find(wanted.Bare) is not { } voice)
        {
            await WriteAsync(writer, Response.Fail(
                $"'{wanted.Bare}' is not in the catalog. `vst-ctl voices` lists what is."));
            return;
        }

        // Trap 7, enforced rather than documented. The daemon has no screen, so
        // what it can insist on is that the client claims to have shown one — and
        // the refusal names the terms so a script can accept them deliberately
        // rather than by discovering a flag.
        if (request.AcceptLicence != true)
        {
            await WriteAsync(writer, Response.Fail(
                $"{voice.Id} is licensed \"{voice.Licence.Name}\"" +
                (string.IsNullOrWhiteSpace(voice.Licence.Url) ? "" : $" ({voice.Licence.Url})") +
                (voice.Licence.IsNonCommercial ? " — NON-COMMERCIAL USE ONLY" : "") +
                ". Re-send with acceptLicence to download it."));
            return;
        }

        if (Interlocked.CompareExchange(ref _installing, 1, 0) != 0)
        {
            await WriteAsync(writer, Response.Fail("a voice install is already running"));
            return;
        }

        try
        {
            var installer = Installer();

            // Progress is written from the downloader's thread, and a StreamWriter
            // is not thread-safe. One writer, one lock, and the terminal reply
            // takes the same one.
            var writeLock = new object();
            var log = new Progress<string>(line =>
            {
                lock (writeLock)
                {
                    try
                    {
                        writer.WriteLine(Protocol.Encode(new Response
                        {
                            Ok = true,
                            VoiceProgress = new VoiceProgress(voice.Id, Message: line),
                        }));
                    }
                    catch (IOException) { /* the client went away; the install continues */ }
                }
            });

            var bytes = new Progress<DownloadProgress>(p =>
            {
                lock (writeLock)
                {
                    try
                    {
                        writer.WriteLine(Protocol.Encode(new Response
                        {
                            Ok = true,
                            VoiceProgress = new VoiceProgress(
                                voice.Id, Path.GetFileName(p.Path), p.BytesReceived, p.BytesTotal),
                        }));
                    }
                    catch (IOException) { }
                }
            });

            Log($"voice install: {voice.Id} ({voice.Bytes / (1024 * 1024)} MB, {voice.Licence.Name})");

            VoiceInstallResult result;
            try
            {
                result = await installer.InstallAsync(voice, log, bytes, token);
            }
            catch (OperationCanceledException)
            {
                Log($"voice install: {voice.Id} cancelled");
                WriteLocked(writer, writeLock, Response.Fail($"{voice.Id} install cancelled"));
                return;
            }

            if (!result.Ok)
            {
                Log($"voice install: {result.Message}");
                WriteLocked(writer, writeLock, new Response
                {
                    Ok = false,
                    Error = result.Message,
                    Voice = new VoiceActionPayload(voice.Id, false, result.Path, result.Message),
                });
                return;
            }

            // The store caches by directory mtime, and creating a directory under
            // it moves that — but the config cache is keyed per voice and this
            // one has never been read. Touching it now means the reply's
            // "calibrating" claim is made against a store that can already see
            // the voice.
            _config.PiperVoices.Config(voice.Id);

            // THE STORE MUST SEE IT BEFORE ANYTHING ASKS. The download lands two
            // files INSIDE piper/<id>/, which does not change piper/'s own
            // timestamp — so a scan that ran during the download cached the set
            // without this voice and had no reason to look again. Observed
            // 2026-09-01: the voice below installed, was invisible to the router
            // fifteen seconds later, and five presses in a row went to Supertonic
            // with a Piper voice id. The calibration a line down could not see it
            // either, which is why that install has no "; calibrating".
            //
            // PiperVoiceStore.StoreStamp now notices this on its own; this makes
            // it certain on the one path that KNOWS the store just changed.
            _engines?.Voices.Invalidate();

            bool calibrating = _engines?.StartCalibration(voice.Id, line =>
            {
                lock (writeLock)
                {
                    try
                    {
                        writer.WriteLine(Protocol.Encode(new Response
                        {
                            Ok = true,
                            VoiceProgress = new VoiceProgress(voice.Id, Message: line),
                        }));
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                }
            }) ?? false;

            Log($"voice install: {result.Message}" + (calibrating ? "; calibrating" : ""));

            WriteLocked(writer, writeLock, new Response
            {
                Ok = true,
                Voice = new VoiceActionPayload(
                    voice.Id, true, result.Path, result.Message, calibrating),
                Notice = calibrating
                    ? "measuring this voice's rate curve in the background — until it finishes, " +
                      "a requested rate is served by the uncalibrated reciprocal"
                    : null,
            });
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
        }
    }

    // ------------------------------------------------------------------ remove

    private Response VoiceRemove(Request request)
    {
        RefreshConfig(force: false);

        if (string.IsNullOrWhiteSpace(request.Voice))
            return Response.Fail("voice remove needs a voice id");

        var wanted = VoiceId.Parse(request.Voice);
        var installer = Installer();

        if (!installer.IsInstalled(wanted.Bare))
            return Response.Fail($"'{wanted.Bare}' is not installed");

        // Removing the voice the next press will ask for leaves the daemon with a
        // setting it cannot honour, and the first symptom would be a hotkey that
        // refuses with a sentence about a missing voice. Better in front of the
        // person doing the removing.
        var def = VoiceId.Parse(EffectiveVoice);
        if (string.Equals(def.Bare, wanted.Bare, StringComparison.OrdinalIgnoreCase) && !(request.Force ?? false))
        {
            return Response.Fail(
                $"'{wanted.Bare}' is the configured default voice — choose another voice first, " +
                "or re-send with force.");
        }

        // Speaking through the voice being deleted. Linux will let the file go
        // while the session holds it open and the utterance would finish
        // normally, but the next press would find nothing; refusing is the
        // honest answer for the two seconds it costs.
        if (_engines?.PiperVoice is { } speaking
            && string.Equals(speaking, wanted.Bare, StringComparison.OrdinalIgnoreCase)
            && _session.State != SpeechState.Idle)
        {
            return Response.Fail($"'{wanted.Bare}' is speaking right now — stop it first");
        }

        var result = installer.Remove(wanted.Bare);
        if (result.Ok)
        {
            // Same reason as the install path: the directory is gone and nothing
            // may go on answering with it. Removing DOES move piper/'s own
            // timestamp, so this is belt and braces rather than a fix — but the
            // two paths should not differ in whether they say so.
            _engines?.Voices.Invalidate();
            _engines?.ForgetCalibration(wanted.Bare);
            Log($"voice remove: {result.Message}");
        }

        return new Response
        {
            Ok = result.Ok,
            Error = result.Ok ? null : result.Message,
            Voice = new VoiceActionPayload(wanted.Bare, false, result.Path, result.Message),
        };
    }
}
