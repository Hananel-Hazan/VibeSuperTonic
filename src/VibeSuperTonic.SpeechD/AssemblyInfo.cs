using System.Runtime.CompilerServices;

// The module's types are internal — nothing links this binary, speech-dispatcher
// runs it — but the protocol state machine has to be testable, for the reason
// the test project's csproj gives: a desync here does not degrade the module, it
// stops the whole server from starting.
[assembly: InternalsVisibleTo("VibeSuperTonic.SpeechD.Tests")]
