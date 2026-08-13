using System.Reflection;

namespace Ap.Control.Ui
{
    /// <summary>
    /// Where the in-game UI's JavaScript comes from: the copy embedded at build time from
    /// <c>ui/*.js</c>, which is what lets a published client be a single file and still serve the page.
    /// </summary>
    internal static class UiPayloadSource
    {
        private const string ResourcePrefix = "ui/";

        /// <summary>The JavaScript for a view, or null if there is none.</summary>
        internal static string? Read(string view)
        {
            // The view name arrives over the wire, so it must not be able to walk out of the prefix.
            string safe = Path.GetFileName(view);
            if (safe.Length == 0) return null;

            using Stream? s = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(ResourcePrefix + safe + ".js");
            if (s is null) return null;
            using var reader = new StreamReader(s);
            return reader.ReadToEnd();
        }
    }
}
