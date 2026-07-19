using System.Buffers.Binary;

namespace Ap.Control.Patcher
{
    /// <summary>
    /// Reads Control's <c>.bin</c> package index and the file blobs it points at inside the paired
    /// <c>.rmdp</c> archive.
    ///
    /// The index is never written. Every patch here is length-neutral and overwritten in place inside
    /// the .rmdp, precisely so that neither the .bin nor the .packmeta has to change — both store a
    /// per-file size, and a game that finds them disagreeing refuses to launch.
    /// </summary>
    internal static class PackFile
    {
        /// <summary>Where a file's content lives inside the .rmdp.</summary>
        internal readonly record struct Entry(long Offset, long Length);

        private const int HeaderSize = 0x9D;
        private const int DirRecordSize = 48;    // qqiqiqq, packed (no alignment)
        private const int FileRecordSize = 44;   // qqiqqq,  packed
        private const int FileRecordStride = FileRecordSize + 16;
        private const int NamesGap = 44;

        /// <summary>Locate <paramref name="target"/> in the package index. Throws if it isn't there.</summary>
        internal static Entry FindEntry(string binPath, string target)
        {
            byte[] data = File.ReadAllBytes(binPath);

            // Byte 0 selects endianness for the whole index; Control ships little-endian, but the
            // format allows either and the original tooling honoured both.
            bool le = data[0] == 0;
            int ReadI32(int at) => le
                ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at))
                : BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at));
            long ReadI64(int at) => le
                ? BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(at))
                : BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(at));

            int dirCount = ReadI32(5);
            int fileCount = ReadI32(9);

            int o = HeaderSize + dirCount * DirRecordSize;
            var records = new (long NameOffset, long Offset, long Length)[fileCount];
            for (int i = 0; i < fileCount; i++)
            {
                records[i] = (ReadI64(o + 20), ReadI64(o + 28), ReadI64(o + 36));
                o += FileRecordStride;
            }

            int namesBase = o + NamesGap;
            foreach (var (nameOffset, offset, length) in records)
                if (NameAt(data, namesBase + (int)nameOffset) == target)
                    return new Entry(offset, length);

            throw new PatchException($"{target} not found in {binPath}");
        }

        private static string NameAt(byte[] data, int at)
        {
            int end = Array.IndexOf(data, (byte)0, at);
            return System.Text.Encoding.UTF8.GetString(data, at, end - at);
        }

        internal static byte[] ReadBlob(string rmdpPath, Entry entry)
        {
            using var fs = new FileStream(rmdpPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(entry.Offset, SeekOrigin.Begin);
            var buf = new byte[entry.Length];
            fs.ReadExactly(buf);
            return buf;
        }

        /// <summary>
        /// Overwrite a blob in place. <paramref name="content"/> must be exactly the entry's length —
        /// anything else would desynchronise the .bin and .packmeta sizes.
        /// </summary>
        internal static void WriteBlob(string rmdpPath, Entry entry, byte[] content)
        {
            if (content.LongLength != entry.Length)
                throw new PatchException(
                    $"refusing to write {content.LongLength} bytes over a {entry.Length}-byte entry; " +
                    "in-place patching requires exact length neutrality");

            using var fs = new FileStream(rmdpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            fs.Seek(entry.Offset, SeekOrigin.Begin);
            fs.Write(content);
            fs.Flush(flushToDisk: true);
        }
    }

    /// <summary>An expected, explainable failure — reported to the user without a stack trace.</summary>
    internal sealed class PatchException(string message) : Exception(message);
}
