// Every test here writes a shell script as a fake renderer and then makes the module
// exec it. With test classes in parallel, a child forked by one test can still hold
// another test's script open for writing when that test execs it, and Linux refuses
// with ETXTBSY ("Text file busy"): the renderer never runs, and the test sees no call.
// Measured 2026-11-09: about one full run in twenty failed that way, never the same
// test twice. The suite takes half a second, so running it serially costs nothing.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
