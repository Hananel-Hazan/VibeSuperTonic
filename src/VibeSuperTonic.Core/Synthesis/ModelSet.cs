using System.Security.Cryptography;
using System.Text;

namespace VibeSuperTonic.Core.Synthesis;

/// <summary>
/// Identity of the ONNX weights a measurement was taken against.
///
/// <para><b>Why it is a staleness trigger.</b> A benchmark profile is a property
/// of <em>this model set</em>, not only of this machine. Swapping the weights
/// changes the cost curve, and a thread count measured against one set and applied
/// to another is an extrapolation nobody performed — the same class of mistake as
/// applying another machine's profile, and less obvious because the folder looks
/// unchanged.</para>
///
/// <para><b>Shared by both platforms as of 2026-08-19.</b> It lived in the Linux
/// daemon beside the <c>/proc</c> readers, which is where it was written rather
/// than where it belongs: there is no platform in it, and the two products index
/// the same <c>models/onnx</c> layout. Two implementations would let Windows and
/// Linux disagree about whether the models had changed — for the same folder.</para>
/// </summary>
public static class ModelSet
{
    /// <summary>
    /// A short fingerprint of the ONNX files under <paramref name="modelsRoot"/>.
    ///
    /// <para>Names and sizes only — deliberately not mtimes and not content
    /// hashes. A portable folder copied to another machine gets new timestamps and
    /// the same models, and treating that as a model change would invalidate every
    /// profile the moment it travelled; the machine id is what catches the copy,
    /// and it catches it for the right reason. Hashing 380 MB of weights would
    /// answer the same question at a cost paid on every daemon start and every
    /// SAPI activation.</para>
    ///
    /// <para>Returns <c>"none"</c> when there is nothing to fingerprint and
    /// <c>"unknown"</c> when the directory cannot be read. Both are stable values
    /// that compare equal to themselves, so a machine with no models does not
    /// thrash between "stale" and "fresh" — it simply never matches a profile
    /// measured against real weights.</para>
    /// </summary>
    public static string Fingerprint(string modelsRoot)
    {
        try
        {
            string onnx = Path.Combine(modelsRoot, "onnx");
            if (!Directory.Exists(onnx)) return "none";

            var entries = new List<string>();
            foreach (string file in Directory.EnumerateFiles(onnx, "*.onnx", SearchOption.AllDirectories))
                entries.Add($"{Path.GetFileName(file)}:{new FileInfo(file).Length}");

            if (entries.Count == 0) return "none";

            // Ordinal sort so the fingerprint does not depend on directory
            // enumeration order, which differs between filesystems and therefore
            // between the two platforms reading the same USB stick.
            entries.Sort(StringComparer.Ordinal);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries)));
            return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }
        catch { return "unknown"; }
    }
}
