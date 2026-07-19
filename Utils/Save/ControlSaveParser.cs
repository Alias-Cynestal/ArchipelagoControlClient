using Ap.Control.Models;
using Ap.Control.Utils.Interfaces;

namespace Ap.Control.Utils.Save
{
    public sealed class ControlSaveParser : ISaveFileParser
    {
        public ControlSave Parse(Stream stream)
        {
            using var reader = new SaveReader(stream);
            return ControlSave.Read(reader);
        }

        public ControlSave Parse(byte[] data)
        {
            using var ms = new MemoryStream(data, writable: false);
            return Parse(ms);
        }

        public ControlSave ParseFile(string path)
        {
            using var fs = File.OpenRead(path);
            return Parse(fs);
        }
    }
}
