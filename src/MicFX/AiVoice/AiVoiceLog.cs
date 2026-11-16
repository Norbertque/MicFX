using System.IO;

namespace MicFX.AiVoice;

/// <summary>
/// Plain text log of the AI voice (%APPDATA%\MicFX\ai\log.txt): start-up,
/// file checks, which backend runs, errors, and a timing summary every few
/// seconds while it runs. Kept under 1 MB; the older half moves to log.old.txt.
/// </summary>
public sealed class AiVoiceLog
{
    private const long MaxSize = 1024 * 1024;
    private readonly object gate = new();

    public AiVoiceLog(string? path) => Path = path;

    public string? Path { get; }

    public void Write(string message)
    {
        if (Path == null) return;
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > MaxSize)
                    File.Move(Path, System.IO.Path.ChangeExtension(Path, ".old.txt"), overwrite: true);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // The log is a help, never a reason to fail.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
