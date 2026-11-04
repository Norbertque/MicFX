using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MicFX.Audio;

/// <summary>
/// Puts the calling thread into Windows' "Pro Audio" scheduling class (MMCSS),
/// once per thread. NAudio's WASAPI threads run at normal priority, so while a
/// game keeps every core busy the thread that runs the whole effect chain can
/// be held back past its buffer deadline, which is heard as a crackle or
/// dropout. MMCSS gives these short periodic tasks priority, while still
/// reserving a share of each period for everything else. The registration ends
/// with the thread (NAudio starts a new one on every Play).
/// </summary>
public static class AudioThreadPriority
{
    [ThreadStatic] private static bool attempted;

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    /// <summary>Call from an audio callback; after the first call on a thread it costs a flag check.</summary>
    public static void Promote()
    {
        if (attempted) return;
        attempted = true;
        try
        {
            uint taskIndex = 0;
            AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        }
        catch
        {
            // avrt.dll missing (not Windows, e.g. the test run): stay at normal priority.
        }
    }
}

/// <summary>Pass-through that promotes whichever thread pulls audio through it.</summary>
public sealed class RealtimeThreadSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;

    public RealtimeThreadSampleProvider(ISampleProvider source) => this.source = source;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        AudioThreadPriority.Promote();
        return source.Read(buffer, offset, count);
    }
}
