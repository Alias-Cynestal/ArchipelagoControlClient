using System.Text;

namespace Ap.Control.Utils.Save
{
    public sealed class SaveReader : BinaryReader
    {
        public SaveReader(Stream input) : base(input, Encoding.UTF8, leaveOpen: true)
        {
        }

        public long Position => BaseStream.Position;

        public long Remaining => BaseStream.Length - BaseStream.Position;

        public string ReadLengthPrefixedString()
        {
            uint len = ReadUInt32();
            return Encoding.UTF8.GetString(ReadBytes(checked((int)len)));
        }

        public void ExpectBytes(params byte[] expected)
        {
            long at = Position;
            byte[] actual = ReadBytes(expected.Length);
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidDataException(
                    $"Unexpected bytes at 0x{at:X}: expected [{string.Join(", ", expected)}], " +
                    $"got [{string.Join(", ", actual)}].");
            }
        }
    }
}
