using System.Security.Cryptography;
using System.Text.Json;

namespace Ap.Control.Patcher
{
    /// <summary>What was backed up, and where it came from. Written beside the .orig blob.</summary>
    internal sealed record Manifest(
        string Package,
        string Target,
        string[] Applied,
        long Offset,
        long Length,
        string OrigSha1,
        string PatchedSha1);

    /// <summary>
    /// Holds the untouched original of every patched blob, so restore is exact rather than
    /// reconstructed. Kept under LocalAppData rather than beside the exe: a backup that lives with the
    /// tool is lost the moment the user moves, re-downloads, or rebuilds it — and a lost backup means
    /// the only way back to stock is a Steam file verification.
    /// </summary>
    internal sealed class BackupStore(string? overrideRoot = null)
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        private string Root { get; } = overrideRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ap.Control", "patch-backup");

        internal string DirFor(PatchDef patch) => Path.Combine(Root, patch.Id);
        private string BlobPath(PatchDef patch) => Path.Combine(DirFor(patch), patch.Target + ".orig");
        private string ManifestPath(PatchDef patch) => Path.Combine(DirFor(patch), "manifest.json");

        internal bool Has(PatchDef patch) => File.Exists(BlobPath(patch)) && File.Exists(ManifestPath(patch));

        internal Manifest? ReadManifest(PatchDef patch)
        {
            try
            {
                return File.Exists(ManifestPath(patch))
                    ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath(patch)))
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        internal byte[] ReadBlob(PatchDef patch) => File.ReadAllBytes(BlobPath(patch));

        internal long BlobSize(PatchDef patch) => new FileInfo(BlobPath(patch)).Length;

        /// <summary>
        /// Save the original blob if it isn't already saved, then record the manifest. The blob is
        /// never overwritten: the first save is by definition the pre-patch state, and re-saving after
        /// a later run would capture already-patched bytes as if they were stock.
        /// </summary>
        internal bool Save(PatchDef patch, byte[] original, byte[] patched, PackFile.Entry entry)
        {
            Directory.CreateDirectory(DirFor(patch));

            bool wroteBlob = false;
            if (!File.Exists(BlobPath(patch)))
            {
                File.WriteAllBytes(BlobPath(patch), original);
                wroteBlob = true;
            }

            File.WriteAllText(ManifestPath(patch), JsonSerializer.Serialize(new Manifest(
                patch.Package, patch.Target, [.. patch.Edits.Select(e => e.Label)],
                entry.Offset, entry.Length, Sha1(original), Sha1(patched)), Json));

            return wroteBlob;
        }

        internal static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
    }
}
