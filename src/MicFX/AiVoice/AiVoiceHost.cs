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

/// <summary>How much the models see around each block: more GPU time and delay for a cleaner voice.</summary>
public enum AiVoiceQuality
{
    /// <summary>What 1.12 did.</summary>
    Standard,

    /// <summary>80 ms of decoder context, 30 ms lookahead (+30 ms delay).</summary>
    High,

    /// <summary>2.5 s content context, 160 ms decoder context, 60 ms lookahead (+60 ms delay).</summary>
    Best,
}

public static class AiVoiceDelays
{
    public static RvcQuality Quality(AiVoiceQuality quality) => quality switch
    {
        AiVoiceQuality.High => new RvcQuality(0, 8, 3),
        AiVoiceQuality.Best => new RvcQuality(2500, 16, 6),
        _ => new RvcQuality(0, 0, 0),
    };

    public static VoiceTiming Timing(AiVoiceDelay delay, AiVoiceQuality quality = AiVoiceQuality.Standard)
    {
        int lookaheadMs = Quality(quality).LookaheadFrames * 10;
        return delay switch
        {
            AiVoiceDelay.Short => VoiceTiming.FromMs(60, 40, lookaheadMs: lookaheadMs),
            AiVoiceDelay.Safe => VoiceTiming.FromMs(160, 100, lookaheadMs: lookaheadMs),
            _ => VoiceTiming.FromMs(100, 60, lookaheadMs: lookaheadMs),
        };
    }

    /// <summary>Delay the voice gets, in ms.</summary>
    public static int Ms(AiVoiceDelay delay, AiVoiceQuality quality = AiVoiceQuality.Standard) => Timing(delay, quality).Delay / 48;
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

    private float indexRate = 0.75f;

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
    /// <summary>RVC's index rate (0..1); applies at once while running.</summary>
    public float IndexRate
    {
        get => indexRate;
        set
        {
            indexRate = value;
            if (current != null) current.Pipeline.IndexRate = value;
        }
    }

    /// <summary>The index file in use while running, or null.</summary>
    public string? IndexPath { get; private set; }

    /// <summary>
    /// The voice model's .index file: <paramref name="chosen"/> if it exists, else one
    /// with the model's name, else the only .index file in the model's folder.
    /// </summary>
    public static string? FindIndex(string modelPath, string? chosen = null)
    {
        if (!string.IsNullOrEmpty(chosen) && File.Exists(chosen)) return chosen;
        string? folder = Path.GetDirectoryName(modelPath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        string same = Path.ChangeExtension(modelPath, ".index");
        if (File.Exists(same)) return same;
        var all = Directory.GetFiles(folder, "*.index");
        return all.Length == 1 ? all[0] : null;
    }

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
    public async Task<bool> LoadAsync(string modelPath, AiBackend backend, AiVoiceDelay delay, string? note = null,
        AiVoiceQuality quality = AiVoiceQuality.Standard, string? indexPath = null)
    {
        await busy.WaitAsync().ConfigureAwait(false);
        try
        {
            await UnloadCoreAsync().ConfigureAwait(false);
            SetState(AiVoiceState.Loading, $"Loading {Path.GetFileNameWithoutExtension(modelPath)}...");
            var loaded = await Task.Run(() => Load(modelPath, backend, delay, quality, indexPath)).ConfigureAwait(false);
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

    private (Loaded? Value, string Note) Load(string modelPath, AiBackend backend, AiVoiceDelay delay, AiVoiceQuality quality, string? indexPath)
    {
        var timing = AiVoiceDelays.Timing(delay, quality);
        Log.Write($"Loading {modelPath} on {(backend == AiBackend.Cuda ? "NVIDIA GPU" : "CPU")}, delay {timing.Delay / 48} ms, quality {quality}");
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

        FeatureIndex? index = null;
        IndexPath = null;
        if (indexPath != null)
        {
            try
            {
                long t0 = Stopwatch.GetTimestamp();
                index = FeatureIndex.Load(indexPath);
                Log.Write($"Index {indexPath}: {index.Count} recorded frames, {index.Dim} features, " +
                          $"loaded in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log.Write($"Index not used: {ex.Message}");
                note = $"Index not used: {ex.Message}";
            }
        }

        var result = TryCreate(modelPath, backend, delay, quality, index);
        if (result.Value == null && backend == AiBackend.Cuda && result.Recoverable)
        {
            Log.Write($"NVIDIA GPU could not start: {result.Error}");
            note = $"NVIDIA GPU could not start ({result.Error}). Running on the CPU.";
            backend = AiBackend.Cpu;
            result = TryCreate(modelPath, backend, delay, quality, index);
        }
        if (result.Value?.Pipeline is { } p && index != null && !p.HasIndex)
            note = (note.Length > 0 ? note + " " : "") + $"Index not used: it has {index.Dim} features per frame, the model {result.Value.Generator.FeatureDim}.";
        if (result.Value?.Pipeline.HasIndex == true) IndexPath = indexPath;
        if (result.Value == null) return (null, result.Error);
        if (result.Note.Length > 0) note = (note.Length > 0 ? note + " " : "") + result.Note;
        return (result.Value, note);
    }

    private (Loaded? Value, string Error, bool Recoverable, string Note) TryCreate(string modelPath, AiBackend backend,
        AiVoiceDelay delay, AiVoiceQuality wanted, FeatureIndex? index)
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
            if (index != null && index.Dim != generator.FeatureDim) index = null; // reported by the caller

            // A model converted with a short frame count may not have room for the higher settings.
            var quality = wanted;
            while (quality > AiVoiceQuality.Standard &&
                   !RvcPipeline.Fits(generator, AiVoiceDelays.Timing(delay, quality), AiVoiceDelays.Quality(quality)))
                quality--;
            string qualityNote = quality == wanted ? "" : $"Quality {wanted} does not fit this voice model; using {quality}.";
            if (qualityNote.Length > 0) Log.Write(qualityNote);
            var timing = AiVoiceDelays.Timing(delay, quality);
            var pipeline = new RvcPipeline(timing, encoder, pitchExtractor, generator, AiVoiceDelays.Quality(quality), index)
            {
                Transpose = pitch,
                IndexRate = indexRate,
            };

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
            return (new Loaded(new VoiceStream(timing, pipeline), pipeline, encoder, pitchExtractor, generator, backend), "", false, qualityNote);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            generator?.Dispose();
            encoder?.Dispose();
            pitchExtractor?.Dispose();
            bool modelProblem = ex is InvalidDataException;
            Log.Write($"Could not start on {backend}: {ex.GetType().Name}: {ex.Message}");
            return (null, FirstLine(ex.Message), !modelProblem, "");
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
