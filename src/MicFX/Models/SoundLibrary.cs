using System.IO;
using System.Security.Cryptography;

namespace MicFX.Models;

/// <summary>
/// MicFX's own copies of soundboard clips (%APPDATA%\MicFX\Sounds), so a
/// tile keeps working when the original file is moved, renamed or deleted.
/// </summary>
public sealed class SoundLibrary
{
    public string Folder { get; }

    public SoundLibrary(string folder)
    {
        Folder = Path.GetFullPath(folder);
    }

    public static SoundLibrary Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicFX", "Sounds"));

    public bool Contains(string path)
    {
        string full = Path.GetFullPath(path);
        return full.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies a file into the library and returns the copy's path. A file
    /// that is already in the library is returned as is; an identical copy
    /// already there is reused; a different file with the same name gets a
    /// numbered name ("clip (2).mp3").
    /// </summary>
    public string Import(string source)
    {
        if (Contains(source)) return source;
        Directory.CreateDirectory(Folder);
        string name = Path.GetFileNameWithoutExtension(source);
        string extension = Path.GetExtension(source);
        for (int n = 1; ; n++)
        {
            string candidate = Path.Combine(Folder, n == 1 ? name + extension : $"{name} ({n}){extension}");
            if (File.Exists(candidate))
            {
                if (SameContent(candidate, source)) return candidate;
                continue;
            }
            // Copy under a temporary name first, so an interrupted copy never
            // leaves a truncated file under the real name.
            string temp = candidate + ".part";
            File.Copy(source, temp, overwrite: true);
            File.Move(temp, candidate);
            return candidate;
        }
    }

    /// <summary>Deletes a library copy. Files outside the library are never touched.</summary>
    public void Release(string path)
    {
        if (Contains(path) && File.Exists(path))
            File.Delete(path);
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var streamA = File.OpenRead(a);
        using var streamB = File.OpenRead(b);
        return SHA256.HashData(streamA).AsSpan().SequenceEqual(SHA256.HashData(streamB));
    }
}
