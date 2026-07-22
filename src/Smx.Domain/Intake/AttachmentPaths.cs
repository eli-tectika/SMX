using System.Text;

namespace Smx.Domain.Intake;

/// Where an attachment's bytes and its extracted text live, and how a browser-supplied filename is made
/// safe to put in a path.
///
/// Sanitisation is not decorative. The filename arrives from the operator's machine, and a path that
/// climbs out of `intake/{sessionId}/{fileId}/` would let one interview's upload land on top of
/// another's — or somewhere else in the `bronze` container entirely, which also holds the SDS corpus.
public static class AttachmentPaths
{
    public static string Blob(string sessionId, string fileId, string filename) =>
        $"intake/{sessionId}/{fileId}/{SafeFilename(filename)}";

    /// A FIXED name, deliberately independent of the original filename: the reader
    /// (`read_attachment`, in the orchestrator) resolves text from (sessionId, fileId) and should not
    /// have to know, or trust, what the file was called.
    public static string Text(string sessionId, string fileId) =>
        $"intake/{sessionId}/{fileId}/extracted.txt";

    /// Allowlist, not denylist: everything outside [A-Za-z0-9._-] becomes '_', and any directory part
    /// is dropped first. A denylist of "../" and friends is the version that gets bypassed.
    public static string SafeFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return "file";

        // Drop directory components under BOTH separators — the upload may come from Windows.
        var name = filename.Replace('\\', '/');
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0) name = name[(lastSlash + 1)..];

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');

        // Collapse runs of '_' so "normal name (1).pdf" does not become "normal_name__1_.pdf".
        var collapsed = new StringBuilder(sb.Length);
        foreach (var c in sb.ToString())
            if (c != '_' || collapsed.Length == 0 || collapsed[^1] != '_') collapsed.Append(c);

        // An underscore glued to the extension dot is punctuation noise the substitution introduced,
        // not something the operator typed as part of the name — "(1).pdf" should read as "1.pdf",
        // not "1_.pdf". Without this, the extension-adjacent '_' from collapsing "(1)." survives the
        // collapse step (it only merges consecutive '_', it doesn't look at neighbouring '.').
        var deglued = collapsed.ToString().Replace("_.", ".");

        var result = deglued.Trim('_', '.');
        // "..", "." and "" all sanitise to nothing; a blank final segment would make the path a folder.
        if (result.Length == 0) return "file";
        return result.Length > 120 ? result[..120] : result;
    }

    public static string Extension(string filename)
    {
        var ext = Path.GetExtension(SafeFilename(filename));
        return string.IsNullOrEmpty(ext) ? "" : ext.ToLowerInvariant();
    }
}
