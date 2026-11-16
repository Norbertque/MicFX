using System.Diagnostics;
using System.IO;
using System.Net.Http;
using MicFX.Audio;

namespace MicFX.AiVoice;

/// <summary>Delay settings: block length and compute allowance.</summary>
public enum AiVoiceDelay
{
    /// <summary>60 ms blocks, 40 ms allowance: about 150 ms delay; needs a fast GPU with headroom.</summary>
    Short,

    /// <summary>100 ms blocks, 60 ms allowance: about 210 ms delay.</summary>
    Normal,

    /// <summary>160 ms blocks, 100 ms allowance: about 310 ms delay; most room for a busy GPU.</summary>
    Safe,
}

public static class AiVoiceDelays
{
    public static VoiceTiming Timing(AiVoiceDelay delay) => delay switch
    {
        AiVoiceDelay.Short => VoiceTiming.FromMs(60, 40),
        AiVoiceDelay.Safe => VoiceTiming.FromMs(160, 100),
        _ => VoiceTiming.FromMs(100, 60),
    };

    /// <summary>Delay the voice gets, in ms.</summary>
    public static int Ms(AiVoiceDelay delay) => Timing(delay).Delay / 48;
}

public enum AiVoiceState { Off, Loading, Running, Failed }

/// <summary>Where the AI voice keeps its files, and what it downloads.</summary>
public sealed class AiVoiceHostOptions
{
    public required string Root { get; init; }
    public FileSet Runtime { get; init; } = AiVoiceFiles.Runtime;
    public FileSet Helpers { get; init; } = AiVoiceFiles.HelperModels;
    public FileSet Cuda { get; init; } = AiVoiceFiles.Cuda;

    /// <summary>Load ONNX Runtime from the downloaded runtime folder (false in tests, which bring their own).</summary>
    public bool ManageNativeRuntime { get; init; } = true;

    public HttpMessageHandler? Http { get; init; }
}

/// <summary>
/// Owns the AI voice outside the audio graph, so it survives the engine
/// restarting on device changes: downloads, the three model sessions, the
/// streaming worker. Loads off the UI thread, falls back from the GPU to the
/// CPU when CUDA cannot start, and disposes models only after the worker has
/// stopped using them. <see cref="Changed"/> is raised on any thread.
/// </summary>
public sealed class AiVoiceHost : IDisposable
{
    private const int WarmUpBlocks = 3;

    private readonly AiVoiceHostOptions options;
    private readonly SemaphoreSlim busy = new(1, 1);
    private readonly Timer statsTimer;
    private Loaded? current;
    private float pitch;
    private long loggedMisses;

    private sealed record Loaded(VoiceStream Stream, RvcPipeline Pipeline, OnnxContentEncoder Encoder,
        OnnxPitchExtractor? Pitch, OnnxVoiceGenerator Generator, AiBackend Backend);

    public AiVoiceHost(AiVoiceHostOptions options)
    {
        this.options = options;
        Downloader = new PinnedDownloader(options.Root, options.Http);
        Log = new AiVoiceLog(Path.Combine(options.Root, "log.txt"));
        statsTimer = new Timer(_ => LogStats(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action? Changed;

    public PinnedDownloader Downloader { get; }
    public AiVoiceLog Log { get; }
    public AiVoiceState State { get; private set; }

    /// <summary>One line for the UI: what is happening or what went wrong.</summary>
    public string Status { get; private set; } = "";

    /// <summary>The stream to play through while running, else null.</summary>
    public VoiceStream? Stream => current?.Stream;

    /// <summary>Where the models run now (may be the CPU after a GPU failure).</summary>
    public AiBackend? Backend => current?.Backend;

    /// <summary>Delay of the running stream in ms (0 when not running).</summary>
    public int DelayMs => current == null ? 0 : current.Stream.Timing.Delay / 48;

    /// <summary>Pitch shift in semitones; applies at once while running.</summary>
    public float Pitch
    {
        get => pitch;
        set
        {
            pitch = value;
            if (current != null) current.Pipeline.Transpose = value;
        }
    }

    /// <summary>File sets that must be downloaded before the voice can run on <paramref name="backend"/>.</summary>
    public IReadOnlyList<FileSet> MissingFiles(AiBackend backend)
    {
        var sets = new List<FileSet>();
        if (options.ManageNativeRuntime && !Downloader.IsInstalled(options.Runtime)) sets.Add(options.Runtime);
        if (!Downloader.IsInstalled(options.Helpers)) sets.Add(options.Helpers);
        if (backend == AiBackend.Cuda && !Downloader.IsInstalled(options.Cuda)) sets.Add(options.Cuda);
        return sets;
    }

    public async Task InstallAsync(IEnumerable<FileSet> sets, IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        foreach (var set in sets)
        {
            Log.Write($"Downloading {set.Title} ({PinnedDownloader.Mb(set.DownloadSize)})");
            try
            {
                await Downloader.InstallAsync(set, progress, cancel).ConfigureAwait(false);
                Log.Write($"Installed {set.Title} in {Downloader.FolderOf(set)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Write($"Download of {set.Title} failed: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// Loads a voice model and starts streaming. Returns false (with <see cref="Status"/>
    /// saying why) when it cannot run. Files must be installed first. <paramref name="note"/>
    /// (for example why the GPU is not used) is logged and kept as the status while running.
    /// </summary>
    public async Task<bool> LoadAsync(string modelPath, AiBackend backend, AiVoiceDelay delay, string? note = null)
    {
        await busy.WaitAsync().ConfigureAwait(false);
        try
        {
            await UnloadCoreAsync().ConfigureAwait(false);
            SetState(AiVoiceState.Loading, $"Loading {Path.GetFileNameWithoutExtension(modelPath)}...");
            var loaded = await Task.Run(() => Load(modelPath, backend, delay)).ConfigureAwait(false);
            current = loaded.Value;
            if (current == null)
            {
                SetState(AiVoiceState.Failed, loaded.Note);
                return false;
            }
            current.Stream.StartWorker();
            loggedMisses = 0;
            statsTimer.Change(10_000, 10_000);
            if (note != null) Log.Write(note);
            SetState(AiVoiceState.Running, loaded.Note.Length > 0 ? loaded.Note : note ?? "");
            return true;
        }
        finally
        {
            busy.Release();
        }
    }

    private (Loaded? Value, string Note) Load(string modelPath, AiBackend backend, AiVoiceDelay delay)
    {
        var timing = AiVoiceDelays.Timing(delay);
        Log.Write($"Loading {modelPath} on {(backend == AiBackend.Cuda ? "NVIDIA GPU" : "CPU")}, delay {timing.Delay / 48} ms");
        if (!File.Exists(modelPath)) return (null, $"The voice model file was not found: {modelPath}");
        var missing = MissingFiles(AiBackend.Cpu);
        if (missing.Count > 0) return (null, $"{missing[0].Title} is not downloaded yet.");

        try
        {
            if (options.ManageNativeRuntime) OrtNative.UseRuntime(Downloader.FolderOf(options.Runtime));
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            Log.Write($"ONNX Runtime could not be loaded: {ex.Message}");
            return (null, $"ONNX Runtime could not be loaded: {ex.Message}");
        }

        string note = "";
        if (backend == AiBackend.Cuda)
        {
            string? problem = !Downloader.IsInstalled(options.Cuda) ? "the NVIDIA libraries are not downloaded"
                : options.ManageNativeRuntime ? OrtNative.PrepareCuda(Downloader.FolderOf(options.Cuda)) : null;
            if (problem != null)
            {
                Log.Write($"NVIDIA GPU not used: {problem}");
                note = $"NVIDIA GPU not used: {problem} Running on the CPU.";
                backend = AiBackend.Cpu;
            }
        }

        var result = TryCreate(modelPath, backend, timing);
        if (result.Value == null && backend == AiBackend.Cuda && result.Recoverable)
        {
            Log.Write($"NVIDIA GPU could not start: {result.Error}");
            note = $"NVIDIA GPU could not start ({result.Error}). Running on the CPU.";
            backend = AiBackend.Cpu;
            result = TryCreate(modelPath, backend, timing);
        }
        if (result.Value == null) return (null, result.Error);
        return (result.Value, note);
    }

    private (Loaded? Value, string Error, bool Recoverable) TryCreate(string modelPath, AiBackend backend, VoiceTiming timing)
    {
        OnnxContentEncoder? encoder = null;
        OnnxPitchExtractor? pitchExtractor = null;
        OnnxVoiceGenerator? generator = null;
        string helpers = Downloader.FolderOf(options.Helpers);
        try
        {
            using var sessionOptions = OnnxSessions.Create(backend, OnnxSessions.DefaultCpuThreads);
            generator = new OnnxVoiceGenerator(modelPath, sessionOptions);
            Log.Write($"Voice model: {generator.Dialect} export, RVC {(generator.FeatureDim == 768 ? "v2" : "v1")}, {generator.SampleRate} Hz, " +
                      $"{(generator.UsesPitch ? "with" : "without")} pitch");
            encoder = new OnnxContentEncoder(Path.Combine(helpers, "contentvec.onnx"), sessionOptions);
            if (generator.UsesPitch) pitchExtractor = new OnnxPitchExtractor(Path.Combine(helpers, "fcpe.onnx"), sessionOptions);
            var pipeline = new RvcPipeline(timing, encoder, pitchExtractor, generator) { Transpose = pitch };

            // Warm up: the first runs allocate memory and pick kernels.
            var context = new float[pipeline.ContextSamples];
            for (int i = 0; i < context.Length; i++) context[i] = 0.1f * MathF.Sin(2 * MathF.PI * 150 * i / 48000f);
            var output = new float[timing.Output];
            double ms = 0;
            for (int k = 0; k < WarmUpBlocks; k++)
            {
                long t0 = Stopwatch.GetTimestamp();
                pipeline.Convert(context, output, k > 0);
                ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            Log.Write($"Running on {(backend == AiBackend.Cuda ? "NVIDIA GPU" : "CPU")}: {ms:F0} ms per block after warm-up, " +
                      $"allowance {timing.Allowance / 48} ms, block {timing.Block / 48} ms");
            return (new Loaded(new VoiceStream(timing, pipeline), pipeline, encoder, pitchExtractor, generator, backend), "", false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            generator?.Dispose();
            encoder?.Dispose();
            pitchExtractor?.Dispose();
            bool modelProblem = ex is InvalidDataException;
            Log.Write($"Could not start on {backend}: {ex.GetType().Name}: {ex.Message}");
            return (null, FirstLine(ex.Message), !modelProblem);
        }
    }

    /// <summary>Stops streaming and frees the models (and GPU memory).</summary>
    public async Task UnloadAsync()
    {
        await busy.WaitAsync().ConfigureAwait(false);
        try
        {
            await UnloadCoreAsync().ConfigureAwait(false);
            SetState(AiVoiceState.Off, "");
        }
        finally
        {
            busy.Release();
        }
    }

    private async Task UnloadCoreAsync()
    {
        var old = current;
        if (old == null) return;
        statsTimer.Change(Timeout.Infinite, Timeout.Infinite);
        current = null;
        Changed?.Invoke(); // the engine detaches the stream; the voice fades out
        old.Stream.Dispose();
        if (!old.Stream.WorkerExited.IsCompleted)
        {
            old.Generator.Cancel();
            old.Encoder.Cancel();
            old.Pitch?.Cancel();
            await Task.WhenAny(old.Stream.WorkerExited, Task.Delay(30_000)).ConfigureAwait(false);
        }
        if (old.Stream.WorkerExited.IsCompleted)
        {
            old.Generator.Dispose();
            old.Encoder.Dispose();
            old.Pitch?.Dispose();
        }
        else
        {
            Log.Write("The model did not stop within 30 s; its memory is freed when MicFX exits.");
        }
        Log.Write("Stopped");
    }

    private void LogStats()
    {
        var s = current?.Stream;
        if (s == null) return;
        long misses = s.MissedBlocks;
        Log.Write($"{s.LastComputeMs:F0} ms per block, least spare {s.MinRecentHeadroomMs:F0} ms, " +
                  $"{misses - loggedMisses} missed in the last 10 s{(s.IsLive ? "" : ", silent (waiting to catch up)")}" +
                  (s.Fault != null ? $", failed: {s.Fault}" : ""));
        loggedMisses = misses;
    }

    private void SetState(AiVoiceState state, string status)
    {
        State = state;
        Status = status;
        Changed?.Invoke();
    }

    private static string FirstLine(string text)
    {
        int i = text.IndexOfAny(new[] { '\r', '\n' });
        return i < 0 ? text : text[..i];
    }

    public void Dispose()
    {
        statsTimer.Dispose();
        var old = current;
        current = null;
        old?.Stream.Dispose();
    }
}
