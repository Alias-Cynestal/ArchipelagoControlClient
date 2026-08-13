using System.Text;

namespace Ap.Control.Patcher
{
    /// <summary>One byte-level substitution, with a label for reporting.</summary>
    internal sealed record Edit(string Label, byte[] Old, byte[] New)
    {
        internal static Edit Of(string label, string old, string @new)
            => new(label, Encoding.ASCII.GetBytes(old), Encoding.ASCII.GetBytes(@new));
    }

    /// <summary>
    /// How a patch gets back to the original byte length after its edits.
    ///
    /// Length neutrality is not optional: the .bin index and the .packmeta each store this file's
    /// size, so growing or shrinking the blob makes them disagree and the game will not launch.
    /// Since edits rarely happen to be the same size as what they replace, every patch needs a way to
    /// give back or take up the difference somewhere syntactically harmless.
    /// </summary>
    internal abstract record Balancer
    {
        /// <summary>
        /// Return <paramref name="patched"/> adjusted by exactly <paramref name="needed"/> bytes.
        /// Positive = the patch shrank the file and must grow back; negative = it grew and must shrink.
        /// </summary>
        internal abstract byte[] Balance(byte[] patched, int needed);
    }

    /// <summary>
    /// Gives back space by inserting a block comment after <paramref name="Anchor"/> — inert in JS, so
    /// it can absorb any surplus. Used by patches whose replacements are shorter than the originals.
    /// </summary>
    internal sealed record PadBalancer(byte[] Anchor, string PadWord) : Balancer
    {
        internal override byte[] Balance(byte[] patched, int needed)
        {
            if (needed == 0) return patched;
            if (needed < 0)
                throw new PatchException(
                    $"patched content is {-needed} bytes LONGER than the original, but this patch can " +
                    "only pad; it has no donor to take bytes from");

            byte[] pad = Comment.Of(needed, PadWord);

            int at = Bytes.IndexOf(patched, Anchor);
            if (at < 0) throw new PatchException("pad anchor not found in patched content");
            at += Anchor.Length;

            var outBuf = new byte[patched.Length + needed];
            patched.AsSpan(0, at).CopyTo(outBuf);
            pad.CopyTo(outBuf, at);
            patched.AsSpan(at).CopyTo(outBuf.AsSpan(at + needed));
            return outBuf;
        }
    }

    /// <summary>
    /// Takes space from a donor string literal, shortening its text by exactly the shortfall. Used by
    /// patches whose replacements are longer than the originals. The donor must be a literal whose
    /// value nothing depends on — dev-only mock data, not live content.
    /// </summary>
    internal sealed record DonorBalancer(byte[] Prefix, byte[] Text, byte[] Suffix) : Balancer
    {
        internal byte[] Original => [.. Prefix, .. Text, .. Suffix];

        internal byte[] Shrunk(int by) => [.. Prefix, .. Text.AsSpan(0, Text.Length - by), .. Suffix];

        internal override byte[] Balance(byte[] patched, int needed)
        {
            if (needed == 0) return patched;
            if (needed > 0)
                throw new PatchException(
                    $"patched content is {needed} bytes SHORTER than the original, but this patch only " +
                    "knows how to donate bytes, not absorb them");

            int take = -needed;
            if (take > Text.Length)
                throw new PatchException($"deficit {take} exceeds donor capacity {Text.Length}");

            byte[] original = Original;
            if (Bytes.Count(patched, original) != 1)
                throw new PatchException("donor literal is not present exactly once");

            return Bytes.Replace(patched, original, Shrunk(take));
        }
    }

    /// <summary>
    /// A block comment of an exact byte length — inert wherever a JS expression list, array body or
    /// statement can appear, so it is the universal filler for making an edit length-neutral.
    /// </summary>
    internal static class Comment
    {
        internal const int Minimum = 4;   // "/**/"

        internal static byte[] Of(int length, string word)
        {
            if (length < Minimum)
                throw new PatchException($"cannot build a {length}-byte block comment (needs >= {Minimum})");

            // The filler repeats a recognisable word so padding is identifiable in a hex dump rather
            // than looking like corruption.
            var sb = new StringBuilder("/*");
            while (sb.Length < length - 2) sb.Append(word);
            byte[] bytes = Encoding.ASCII.GetBytes(sb.ToString()[..(length - 2)] + "*/");
            if (bytes.Length != length) throw new PatchException("comment construction failed");
            return bytes;
        }
    }

    /// <summary>
    /// Takes space by emptying a dead bracketed region — a mock-data array that the shipped game never
    /// evaluates — and leaving a block comment in its place, so <c>[{...},{...}]</c> becomes
    /// <c>[/*pad*/]</c>. Where <see cref="DonorBalancer"/> can only shave one string literal, this frees
    /// the whole region, which is what a patch that injects real code needs.
    ///
    /// <paramref name="Marker"/> must be unique in the file and END with the region's opening bracket.
    /// </summary>
    internal sealed record CollapseBalancer(byte[] Marker, string PadWord) : Balancer
    {
        internal override byte[] Balance(byte[] patched, int needed)
        {
            if (needed == 0) return patched;
            if (needed > 0)
                throw new PatchException(
                    $"patched content is {needed} bytes SHORTER than the original, but this patch only " +
                    "knows how to free space, not take it up");

            if (Bytes.Count(patched, Marker) != 1)
                throw new PatchException("collapse marker is not present exactly once");

            int open = Bytes.IndexOf(patched, Marker) + Marker.Length - 1;
            int close = MatchBracket(patched, open);
            int interior = close - open - 1;

            int take = -needed;
            int capacity = interior - Comment.Minimum;
            if (take > capacity)
                throw new PatchException(
                    $"deficit of {take} bytes exceeds the dead region's {capacity}-byte capacity");

            byte[] comment = Comment.Of(interior - take, PadWord);

            var outBuf = new byte[patched.Length - take];
            patched.AsSpan(0, open + 1).CopyTo(outBuf);
            comment.CopyTo(outBuf, open + 1);
            patched.AsSpan(close).CopyTo(outBuf.AsSpan(open + 1 + comment.Length));
            return outBuf;
        }

        /// <summary>
        /// Index of the bracket closing the one at <paramref name="open"/>. String-aware, because the
        /// mock data is full of bracket characters inside quoted text. Regex literals would also need
        /// handling, but a region only qualifies as a donor if it is pure data, which has none.
        /// </summary>
        private static int MatchBracket(byte[] data, int open)
        {
            byte opener = data[open];
            byte closer = opener switch
            {
                (byte)'[' => (byte)']',
                (byte)'{' => (byte)'}',
                (byte)'(' => (byte)')',
                _ => throw new PatchException($"collapse marker does not end with a bracket (got '{(char)opener}')"),
            };

            int depth = 0;
            byte quote = 0;
            for (int i = open; i < data.Length; i++)
            {
                byte c = data[i];
                if (quote != 0)
                {
                    if (c == (byte)'\\') i++;
                    else if (c == quote) quote = 0;
                    continue;
                }
                switch (c)
                {
                    case (byte)'"' or (byte)'\'' or (byte)'`':
                        quote = c;
                        break;
                    case var _ when c == opener:
                        depth++;
                        break;
                    case var _ when c == closer:
                        if (--depth == 0) return i;
                        break;
                }
            }
            throw new PatchException("unbalanced brackets: could not find the end of the dead region");
        }
    }

    /// <summary>
    /// A complete patch: which packaged file it edits, the substitutions, and how it rebalances length.
    /// <paramref name="Validate"/> runs on the finished bytes for patch-specific invariants that the
    /// generic machinery cannot know about; it returns an error string, or null when all is well.
    /// </summary>
    internal sealed record PatchDef(
        string Id,
        string Title,
        string Package,
        string Target,
        IReadOnlyList<Edit> Edits,
        Balancer Balance,
        Func<byte[], string?>? Validate = null)
    {
        /// <summary>Apply every edit, rebalance to the original length, and run the patch's own checks.</summary>
        internal byte[] Build(byte[] blob)
        {
            byte[] outBuf = blob;
            foreach (var edit in Edits)
            {
                int n = Bytes.Count(outBuf, edit.Old);
                if (n != 1)
                    throw new PatchException(
                        $"anchor '{edit.Label}': expected exactly 1 occurrence, found {n} " +
                        "(game updated? already patched?)");
                outBuf = Bytes.Replace(outBuf, edit.Old, edit.New);
            }

            outBuf = Balance.Balance(outBuf, blob.Length - outBuf.Length);

            if (outBuf.Length != blob.Length)
                throw new PatchException($"length not neutral: {outBuf.Length} vs {blob.Length}");

            if (Validate?.Invoke(outBuf) is { } problem)
                throw new PatchException(problem);

            return outBuf;
        }

        internal PatchState StateOf(byte[] blob)
        {
            if (Edits.All(e => Bytes.Count(blob, e.New) == 1)) return PatchState.Patched;
            if (Edits.All(e => Bytes.Count(blob, e.Old) == 1)) return PatchState.Stock;
            return PatchState.Unknown;
        }
    }

    internal enum PatchState { Stock, Patched, Unknown }

    /// <summary>Byte-array search helpers — the ones <c>bytes</c> gives you for free in Python.</summary>
    internal static class Bytes
    {
        internal static int IndexOf(byte[] haystack, byte[] needle, int from = 0)
        {
            int at = haystack.AsSpan(from).IndexOf(needle);
            return at < 0 ? -1 : at + from;
        }

        internal static int Count(byte[] haystack, byte[] needle)
        {
            int n = 0, at = 0;
            while ((at = IndexOf(haystack, needle, at)) >= 0) { n++; at += needle.Length; }
            return n;
        }

        internal static byte[] Replace(byte[] haystack, byte[] needle, byte[] replacement)
        {
            int at = IndexOf(haystack, needle);
            if (at < 0) throw new PatchException("replace: needle not found");

            var outBuf = new byte[haystack.Length - needle.Length + replacement.Length];
            haystack.AsSpan(0, at).CopyTo(outBuf);
            replacement.CopyTo(outBuf, at);
            haystack.AsSpan(at + needle.Length).CopyTo(outBuf.AsSpan(at + replacement.Length));
            return outBuf;
        }
    }
}
