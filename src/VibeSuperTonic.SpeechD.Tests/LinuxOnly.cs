using System.Runtime.Versioning;

// This project is built and run on Linux only (VibeSuperTonic.linux.slnx). Without the
// attribute every call into a Linux-only product API raised CA1416, ~800 lines that
// buried the real warnings (review item 69).
[assembly: SupportedOSPlatform("linux")]
