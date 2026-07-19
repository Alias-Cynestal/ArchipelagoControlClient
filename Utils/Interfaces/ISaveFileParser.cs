using Ap.Control.Models;

namespace Ap.Control.Utils.Interfaces
{
    public interface ISaveFileParser
    {
        ControlSave Parse(Stream stream);
        ControlSave Parse(byte[] data);
        ControlSave ParseFile(string path);
    }
}
