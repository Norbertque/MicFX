using NAudio.Wave;

namespace MicFX.Audio;

/// <summary>
/// The AI voice stage: sends the (mono-mixed) voice through a <see cref="VoiceStream"/>
/// and plays the converted voice on every channel.
///
/// Off, it is a plain pass-through: no work, no delay, bit-exact. Switching on
/// or off is a 30 ms equal-power crossfade between the dry and converted
/// voice. While no stream is attached (the model is still loading) the voice
/// is silent rather than dry, so the real voice never leaks. Attaching a
/// different stream fades the old one out over 5 ms first.
/// </summary>
public sealed class AiVoiceSampleProvider : ISampleProvider
{
    private const int MaxChunk = 1024;
    private const int MixRamp = 1440;

    private readonly ISampleProvider source;
    private readonly int channels;
    private readonly float[] monoIn = new float[MaxChunk];
    private readonly float[] monoOut = new float[MaxChunk];
    private readonly float[] swapFade = new float[VoiceStream.FadeSamples];
    private volatile bool enabled;
    private volatile VoiceStream? requested;
    private VoiceStream? active;
    private bool running;
    private int mix;      // 0 = dry .. MixRamp = converted
    private int swapLeft; // > 0 while the old stream fades out before a swap

    public AiVoiceSampleProvider(ISampleProvider source)
    {
        this.source = source;
        channels = source.WaveFormat.Channels;
        for (int i = 0; i < swapFade.Length; i++)
        {
            float s = MathF.Sin(0.5f * MathF.PI * i / (swapFade.Length - 1));
            swapFade[i] = s * s;
        }
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    /// <summary>Plays the converted voice instead of the dry one. Safe to set while audio runs.</summary>
    public bool Enabled
    {
        get => enabled;
        set => enabled = value;
    }

    /// <summary>The stream to play through; null while the model loads (silence). Safe to set while audio runs.</summary>
    public VoiceStream? Stream
    {
        get => requested;
        set => requested = value;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (!running)
        {
            if (!enabled) return source.Read(buffer, offset, count);
            running = true;
            mix = 0;
            swapLeft = 0;
            active = requested;
            active?.BeginGeneration();
        }

        int n = source.Read(buffer, offset, count);
        int frames = n / channels;
        for (int done = 0; done < frames;)
        {
            int m = Math.Min(MaxChunk, frames - done);
            ProcessChunk(buffer, offset + done * channels, m);
            done += m;
        }

        if (!enabled && mix == 0) running = false;
        return n;
    }

    private void ProcessChunk(float[] buffer, int offset, int frames)
    {
        if (channels == 1)
        {
            Array.Copy(buffer, offset, monoIn, 0, frames);
        }
        else
        {
            float scale = 1f / channels;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                int b = offset + f * channels;
                for (int c = 0; c < channels; c++) sum += buffer[b + c];
                monoIn[f] = sum * scale;
            }
        }

        var want = requested;
        if (want != active && swapLeft == 0)
        {
            if (active == null)
            {
                active = want;
                active?.BeginGeneration();
            }
            else
            {
                swapLeft = swapFade.Length;
            }
        }

        var wet = monoOut.AsSpan(0, frames);
        if (active != null) active.Process(monoIn.AsSpan(0, frames), wet);
        else wet.Clear();

        if (swapLeft > 0)
        {
            for (int f = 0; f < frames; f++)
            {
                if (swapLeft > 0)
                {
                    swapLeft--;
                    wet[f] *= swapFade[swapLeft];
                    if (swapLeft == 0)
                    {
                        // Faded out: switch at the next chunk so the new stream starts on a boundary.
                        active = null;
                        swapLeft = 0;
                    }
                }
                else
                {
                    wet[f] = 0f;
                }
            }
        }

        int target = enabled ? MixRamp : 0;
        for (int f = 0; f < frames; f++)
        {
            if (mix != target) mix += mix < target ? 1 : -1;
            int b = offset + f * channels;
            if (mix == MixRamp)
            {
                for (int c = 0; c < channels; c++) buffer[b + c] = wet[f];
            }
            else if (mix > 0)
            {
                float t = 0.5f * MathF.PI * mix / MixRamp;
                float gd = MathF.Cos(t), gw = MathF.Sin(t);
                for (int c = 0; c < channels; c++) buffer[b + c] = buffer[b + c] * gd + wet[f] * gw;
            }
        }
    }
}
