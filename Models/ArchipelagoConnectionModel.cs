using System;
using System.Collections.Generic;
using System.Text;

namespace Ap.Control.Models
{
    public sealed class ArchipelagoConnectionModel
    {
        public Uri Uri { get; }
        public String Username { get; }
        public String? Password { get; }

        public ArchipelagoConnectionModel(Uri uri, string username, string? password)
        {
            Uri = uri;
            Username = username;
            Password = password;
        }
    }
}
