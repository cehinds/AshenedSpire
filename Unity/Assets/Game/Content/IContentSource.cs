namespace Ashen.Content
{
    /// <summary>
    /// Reads content files by manifest-relative path (docs/design/08 §2). Implementations: a directory source
    /// (editor, Windows player, tests) and, in Ashen.Platform, a UnityWebRequest source (Android/WebGL).
    /// Paths always come from the manifest or generated constants, never from directory listings.
    /// </summary>
    public interface IContentSource
    {
        bool Exists(string relativePath);
        string ReadText(string relativePath);
    }
}
