using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WindowAudioRecorder;

/// <summary>Everything the recorder needs to know to produce one take.</summary>
public sealed record RecordingRequest(
    string Folder,
    string Template,
    OutputFormat Format,
    int Mp3Bitrate,
    int SampleRate,          // 0 = whatever the endpoint runs at
    ChannelMode Channels,
    int GainDb,
    int SplitMinutes,        // 0 = single file
    int MaxMinutes);         // 0 = unlimited

/// <summary>
/// Captures a render endpoint's output through WASAPI loopback.
/// <para>
/// Capture and recording are separate: capture runs continuously so the level meters are live
/// before you ever press record (and so recording starts instantly), while recording just adds a
/// processing chain and a file on top of the running capture.
/// </para>
/// <para>
/// Audio reaches RAM before it reaches disk. The capture callback copies each packet into a
/// <see cref="CaptureQueue"/> and returns; a dedicated writer thread does the mixing, resampling,
/// conversion, encoding and file I/O. Nothing slow runs on the capture thread, and a disk that
/// stalls costs memory rather than samples.
/// </para>
/// </summary>
public sealed class LoopbackRecorder : IDisposable
{
    /// <summary>
    /// Allocation unit for buffered audio, and the most the writer takes on in one go. Blocks are
    /// consecutive slices of one byte stream rather than one per packet, so this sets the
    /// granularity of pooling and of the writer's steps, not the latency: the writer seals the
    /// part-filled block before each drain, so the newest packet never waits for a block to fill.
    /// </summary>
    private const int BlockMs = 50;

    /// <summary>
    /// A backstop on buffered audio, not a working limit — roughly 230 MB at a typical 48 kHz
    /// stereo float endpoint. In practice the writer keeps up and a handful of blocks circulate; a
    /// disk this far behind is not coming back, and stopping here keeps the file that exists rather
    /// than growing until the process dies and takes the whole take with it.
    /// </summary>
    private const int MemoryCeilingSeconds = 600;

    private const int WriterTickMs = 250;      // silence-padding and limit-checking cadence
    private const int PadThresholdMs = 300;    // comfortably above the pipeline's own latency
    private const int FinaliseTimeoutMs = 30_000;
    private const float ClipThreshold = 0.999f;

    private readonly object _sync = new();
    private readonly object _meterLock = new();
    private readonly Stopwatch _totalClock = new();
    private readonly Stopwatch _segmentClock = new();

    /// <summary>Raised whenever the writer has work, or state it needs to notice, waiting.</summary>
    private readonly ManualResetEventSlim _writerSignal = new(false);

    private readonly MMDeviceEnumerator _deviceEnumerator = new();

    private WasapiLoopbackCapture? _capture;
    private MMDevice? _device;
    private TaskCompletionSource<Exception?>? _captureStopSignal;

    // Handed to the writer thread by StartRecording, before it starts.
    private CaptureQueue? _queue;
    private Thread? _writerThread;
    private TaskCompletionSource<string?>? _writerDone;
    private RecordingRequest? _request;
    private int _sourceBytesPerSecond;

    // Writer-thread only once recording begins. No lock guards these because nothing else touches
    // them: the file is owned start to finish by one thread, so a stalled write blocks nobody.
    private BufferedWaveProvider? _buffered;
    private ISampleProvider? _chain;
    private Stream? _output;
    private float[] _sampleBuffer = [];
    private byte[] _byteBuffer = [];
    private byte[] _silence = [];
    private bool _floatOutput;
    private int _bytesPerSample;
    private int _outBlockAlign;
    private int _segmentIndex;

    // Read by the UI for display while the writer updates them.
    private long _segmentBytes;
    private string? _currentPath;

    private VolumeSampleProvider? _volume;
    private int _outBytesPerSecond;

    private volatile bool _recording;
    private volatile bool _paused;
    private volatile bool _accepting;        // the capture thread may add to the queue
    private volatile bool _stopRequested;
    private volatile bool _autoStopped;
    private volatile bool _clipped;

    private float _peakLeft;
    private float _peakRight;

    /// <summary>
    /// Raised for every finalised file, including each piece of a split recording.
    /// <para>
    /// Raised on the writer thread. Handlers must marshal to the UI without blocking on it —
    /// <c>BeginInvoke</c>, not <c>Invoke</c> — because a shutdown path may be waiting on this same
    /// thread to finish the file.
    /// </para>
    /// </summary>
    public event EventHandler<string>? SegmentCompleted;

    /// <summary>Raised when capture or writing ends without being asked to.</summary>
    public event EventHandler<Exception?>? Aborted;

    /// <summary>Raised when recording ends because it hit the configured duration limit.</summary>
    public event EventHandler? AutoStopped;

    public bool IsCapturing => _capture is not null;
    public bool IsRecording => _recording;
    public bool IsPaused => _paused;

    public string? CaptureDeviceId { get; private set; }
    public string? DeviceName { get; private set; }
    public WaveFormat? SourceFormat { get; private set; }
    public WaveFormat? FileFormat { get; private set; }

    public string? CurrentPath => Volatile.Read(ref _currentPath);

    /// <summary>Set when the requested settings had to be adjusted (an MP3 rate limit, say).</summary>
    public string? Notice { get; private set; }

    public TimeSpan Elapsed { get { lock (_sync) { return _totalClock.Elapsed; } } }
    public int OutputBytesPerSecond => _outBytesPerSecond;

    /// <summary>Seconds of captured audio sitting in RAM waiting to be written. Normally near zero.</summary>
    public double BufferedSeconds => ToSeconds(_queue?.Pending ?? 0);

    /// <summary>Deepest the RAM backlog got during the current or most recent take.</summary>
    public double PeakBufferedSeconds => ToSeconds(_queue?.Peak ?? 0);

    /// <summary>Memory the buffer pool is holding, queued blocks and spares together.</summary>
    public long BufferedPoolBytes => _queue?.AllocatedBytes ?? 0;

    public long EstimatedFileBytes
    {
        get
        {
            if (_request?.Format != OutputFormat.Mp3) return 44 + Volatile.Read(ref _segmentBytes);
            lock (_sync) { return (long)(_segmentClock.Elapsed.TotalSeconds * _request.Mp3Bitrate * 125); }
        }
    }

    private double ToSeconds(long bytes) =>
        _sourceBytesPerSecond > 0 ? (double)bytes / _sourceBytesPerSecond : 0;

    // ---- capture -------------------------------------------------------------------------

    /// <summary>
    /// Opens the endpoint and starts streaming. The recorder resolves and owns its own
    /// <see cref="MMDevice"/> deliberately: NAudio hands a capture the device's cached
    /// AudioClient, so a device object disposed elsewhere (a UI list being rebuilt, say) would
    /// tear the running capture down with it.
    /// </summary>
    public void StartCapture(string deviceId)
    {
        if (_capture is not null) throw new InvalidOperationException("Capture is already running.");

        var device = _deviceEnumerator.GetDevice(deviceId);
        try
        {
            // Created on the calling thread on purpose: NAudio posts RecordingStopped back to
            // whichever thread constructed it, which is what StopCaptureAsync awaits.
            var capture = new WasapiLoopbackCapture(device);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnCaptureStopped;

            _capture = capture;
            _device = device;
            SourceFormat = capture.WaveFormat;
            CaptureDeviceId = device.ID;
            DeviceName = SafeDeviceName(device);

            capture.StartRecording();
        }
        catch
        {
            device.Dispose();
            _device = null;
            throw;
        }
    }

    public async Task<Exception?> StopCaptureAsync()
    {
        var capture = _capture;
        if (capture is null) return null;

        var signal = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _captureStopSignal = signal;
        capture.StopRecording();
        return await signal.Task.ConfigureAwait(true);
    }

    // ---- recording -----------------------------------------------------------------------

    public void StartRecording(RecordingRequest request)
    {
        lock (_sync)
        {
            if (_recording) throw new InvalidOperationException("Already recording.");
            var source = SourceFormat ?? throw new InvalidOperationException("Capture is not running.");

            Notice = null;

            int outChannels = request.Channels switch
            {
                ChannelMode.Mono => 1,
                ChannelMode.Stereo => Math.Min(2, source.Channels),
                _ => source.Channels
            };
            int outRate = request.SampleRate > 0 ? request.SampleRate : source.SampleRate;
            bool float32 = request.Format == OutputFormat.Wav32Float;
            int bits = request.Format switch
            {
                OutputFormat.Wav24 => 24,
                OutputFormat.Wav32Float => 32,
                _ => 16
            };

            if (request.Format == OutputFormat.Mp3)
            {
                // LAME only speaks mono/stereo at MPEG sample rates; fold rather than fail.
                if (outChannels > 2)
                {
                    outChannels = 2;
                    Notice = "MP3 is stereo at most; downmixed to stereo.";
                }

                int mp3Rate = AppSettings.NearestMp3Rate(outRate);
                if (mp3Rate != outRate)
                {
                    Notice = $"MP3 cannot encode {outRate:n0} Hz; recording at {mp3Rate:n0} Hz.";
                    outRate = mp3Rate;
                }
            }

            // The queue is where a stalled disk is absorbed, so it is measured in the endpoint's own
            // format. Whole frames per block: blocks are consecutive slices of one byte stream, and
            // a boundary that fell mid-frame would misalign every sample after it.
            _sourceBytesPerSecond = source.AverageBytesPerSecond;
            int blockSize = Math.Max(source.AverageBytesPerSecond * BlockMs / 1000, source.BlockAlign);
            blockSize -= blockSize % source.BlockAlign;

            // Reused between takes so the pool keeps its blocks and the first take is not the only
            // one that has to allocate them.
            if (_queue is null || _queue.BlockSize != blockSize)
            {
                _queue = new CaptureQueue(blockSize, (long)source.AverageBytesPerSecond * MemoryCeilingSeconds);
            }
            else
            {
                _queue.Reset();
            }

            _buffered = new BufferedWaveProvider(source)
            {
                ReadFully = false,                // must not manufacture silence; padding is our job
                DiscardOnBufferOverflow = false,  // the queue is the buffer; this filling is a bug
                BufferDuration = TimeSpan.FromSeconds(2)
            };

            ISampleProvider chain = _buffered.ToSampleProvider();
            if (outChannels != source.Channels) chain = new ChannelMixSampleProvider(chain, outChannels);
            if (outRate != source.SampleRate) chain = new WdlResamplingSampleProvider(chain, outRate);

            // Always in the chain, even at 0 dB, so the slider can be moved mid-take.
            _volume = new VolumeSampleProvider(chain) { Volume = GainToVolume(request.GainDb) };
            _chain = _volume;

            FileFormat = float32
                ? WaveFormat.CreateIeeeFloatWaveFormat(outRate, outChannels)
                : new WaveFormat(outRate, bits, outChannels);

            _floatOutput = float32;
            _bytesPerSample = bits / 8;
            _outBlockAlign = FileFormat.BlockAlign;
            _outBytesPerSecond = FileFormat.AverageBytesPerSecond;
            _silence = new byte[_outBytesPerSecond];

            int frames = Math.Max(1024, outRate / 10);
            _sampleBuffer = new float[frames * outChannels];
            _byteBuffer = new byte[_sampleBuffer.Length * 4];

            _request = request;
            _segmentIndex = 1;
            _clipped = false;
            _stopRequested = false;
            _autoStopped = false;
            ResetPeaks();

            Directory.CreateDirectory(request.Folder);
            OpenSegment();

            _paused = false;
            _recording = true;
            _totalClock.Restart();

            _writerDone = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _writerSignal.Reset();
            _writerThread = new Thread(WriterLoop)
            {
                Name = "audio-writer",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _writerThread.Start();

            // Last, so the capture thread cannot reach a half-built pipeline.
            _accepting = true;
        }
    }

    /// <summary>
    /// Finalises the current file once the writer has flushed everything RAM was holding. Resolves
    /// to its path, or null if nothing was recording.
    /// </summary>
    public Task<string?> StopRecordingAsync()
    {
        var done = _writerDone;
        if (!_recording || done is null) return Task.FromResult<string?>(null);

        _accepting = false;
        _stopRequested = true;
        _writerSignal.Set();
        return done.Task;
    }

    /// <summary>
    /// Blocking form of <see cref="StopRecordingAsync"/>, for shutdown paths that have to see the
    /// file closed before the process goes. Elsewhere prefer the async form, which leaves the UI
    /// responsive while a backlog drains.
    /// </summary>
    public string? StopRecording()
    {
        var pending = StopRecordingAsync();
        if (pending.Wait(FinaliseTimeoutMs)) return pending.Result;

        // A disk this unresponsive is not about to recover, and hanging on exit is worse than
        // giving up on the tail. The writer is a background thread, so it goes with the process.
        Debug.WriteLine("Timed out waiting for the writer to finalise.");
        return null;
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (!_recording || _paused || _stopRequested) return;
            _paused = true;
            _accepting = false;
            _totalClock.Stop();
            _segmentClock.Stop();
        }

        // Whatever is already buffered was captured before the pause, so it is left to drain into
        // the file rather than discarded.
        _writerSignal.Set();
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (!_recording || !_paused || _stopRequested) return;
            _paused = false;
            _accepting = true;
            _totalClock.Start();
            _segmentClock.Start();
        }

        _writerSignal.Set();
    }

    /// <summary>Applies a gain in dB, taking effect immediately even mid-recording.</summary>
    public void SetGain(int decibels)
    {
        // A lone float store: the writer picks it up on its next block, which is what "immediately"
        // means for audio already on its way to the file.
        if (_volume is { } volume) volume.Volume = GainToVolume(decibels);
    }

    private static float GainToVolume(int decibels) => (float)Math.Pow(10, decibels / 20.0);

    /// <summary>Peak level per channel since the previous call, then resets.</summary>
    public (float Left, float Right) ReadPeaks()
    {
        lock (_meterLock)
        {
            var peaks = (_peakLeft, _peakRight);
            _peakLeft = 0f;
            _peakRight = 0f;
            return peaks;
        }
    }

    /// <summary>True if anything clipped since the previous call, then resets.</summary>
    public bool ReadClip()
    {
        bool clipped = _clipped;
        _clipped = false;
        return clipped;
    }

    // ---- capture callbacks ---------------------------------------------------------------

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        if (!_accepting)
        {
            MeterSource(e.Buffer, e.BytesRecorded);
            return;
        }

        // The only work this thread does for a take: one copy into a pooled block.
        if (!_queue!.Append(e.Buffer.AsSpan(0, e.BytesRecorded)))
        {
            // At the ceiling. Stop feeding a queue that cannot take it and let the writer end the
            // take with an error, rather than dropping this packet and every one after it in silence.
            _accepting = false;
        }

        _writerSignal.Set();
    }

    private async void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        var capture = _capture;
        _capture = null;

        // Whatever is still in RAM was captured before the endpoint went away, so the writer is
        // given the chance to finish it instead of the tail of the take being thrown out.
        await StopRecordingAsync().ConfigureAwait(true);

        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnCaptureStopped;
            try { capture.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"Disposing the capture failed: {ex.Message}"); }
        }

        _device?.Dispose();
        _device = null;
        SourceFormat = null;
        CaptureDeviceId = null;

        var signal = _captureStopSignal;
        _captureStopSignal = null;
        if (signal is not null) signal.TrySetResult(e.Exception);
        else Aborted?.Invoke(this, e.Exception);
    }

    // ---- the writer thread ---------------------------------------------------------------

    /// <summary>
    /// Owns the output file for the whole take: everything expensive happens here, so a slow disk
    /// delays this thread alone. Runs until the take is stopped, hits a limit, or fails.
    /// </summary>
    private void WriterLoop()
    {
        Exception? failure = null;

        try
        {
            while (true)
            {
                AwaitWork();

                if (_queue!.Exhausted && failure is null)
                {
                    // At the memory ceiling. Take the ordinary stop path from here, so everything
                    // RAM did hold still reaches the file, and report why the take ended.
                    failure = new IOException(
                        $"Writing fell more than {MemoryCeilingSeconds / 60} minutes behind the " +
                        "capture, so recording stopped rather than quietly losing audio. " +
                        "Everything up to that point is in the file.");
                    _accepting = false;
                    _stopRequested = true;
                }

                DrainQueue();

                if (_stopRequested)
                {
                    if (_queue.Pending == 0) break;
                    continue;                        // flush the backlog before finalising
                }

                // Silence may only be synthesised, and a segment may only be closed, once the file
                // has caught up with the queue. Doing either while audio is still buffered would
                // splice it in ahead of samples that were captured first.
                if (!_paused && _queue.Pending == 0)
                {
                    PadSilentGap();
                    EnforceLimits();
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            Debug.WriteLine($"Writing failed: {ex}");
        }

        string? path = Finalise(ref failure);
        bool autoStopped = _autoStopped;

        _accepting = false;
        _paused = false;
        _recording = false;

        if (path is not null) SegmentCompleted?.Invoke(this, path);
        if (autoStopped) AutoStopped?.Invoke(this, EventArgs.Empty);
        if (failure is not null) Aborted?.Invoke(this, failure);

        // Last, so a caller resuming from StopRecordingAsync sees the events first.
        _writerDone!.TrySetResult(path);
    }

    /// <summary>
    /// Waits for something to do, or for the padding cadence to come round.
    /// <para>
    /// The signal is cleared before the state is tested so no wake-up can be missed: everything
    /// that raises it mutates the state it reports first, and a sticky signal raised between the
    /// test and the wait makes the wait return at once.
    /// </para>
    /// </summary>
    private void AwaitWork()
    {
        _writerSignal.Reset();
        if (_queue!.Pending > 0 || _stopRequested || _queue.Exhausted) return;
        _writerSignal.Wait(WriterTickMs);
    }

    /// <summary>Moves everything RAM is holding through the pipeline and into the file.</summary>
    private void DrainQueue()
    {
        // Seal first, so the block still being packed is taken too. Without this the newest packet
        // would wait for a block to fill, and an idle endpoint would leave it waiting indefinitely.
        _queue!.Seal();

        while (_queue.TryTake(out byte[] block, out int count))
        {
            try
            {
                _buffered!.AddSamples(block, 0, count);
                Pump();
            }
            finally
            {
                _queue.Recycle(block);
            }
        }
    }

    private void Pump()
    {
        while (true)
        {
            int read = _chain!.Read(_sampleBuffer, 0, _sampleBuffer.Length);
            if (read <= 0) return;

            MeterOutput(_sampleBuffer, read);
            int bytes = ConvertSamples(_sampleBuffer, read);
            _output!.Write(_byteBuffer, 0, bytes);
            Volatile.Write(ref _segmentBytes, _segmentBytes + bytes);

            if (read < _sampleBuffer.Length) return;   // the buffered source is dry
        }
    }

    private void EnforceLimits()
    {
        var request = _request;
        if (request is null) return;

        if (request.MaxMinutes > 0 && Elapsed.TotalMinutes >= request.MaxMinutes)
        {
            // Stop taking input and let the loop finalise, the same path a manual stop takes.
            _accepting = false;
            _autoStopped = true;
            _stopRequested = true;
            return;
        }

        TimeSpan segment;
        lock (_sync) { segment = _segmentClock.Elapsed; }
        if (request.SplitMinutes > 0 && segment.TotalMinutes >= request.SplitMinutes) RollSegment();
    }

    // ---- file plumbing -------------------------------------------------------------------

    /// <summary>
    /// Opens the next output file. NAudio owns the underlying <see cref="FileStream"/>, buffering
    /// included; there is nothing to gain from a bigger one now that no audio thread waits on it.
    /// </summary>
    private void OpenSegment()
    {
        var request = _request!;
        string path = BuildPath(request, _segmentIndex);

        _output = request.Format == OutputFormat.Mp3
            ? new LameMP3FileWriter(path, FileFormat, request.Mp3Bitrate)
            : new WaveFileWriter(path, FileFormat);

        Volatile.Write(ref _currentPath, path);
        Volatile.Write(ref _segmentBytes, 0);
        lock (_sync) { _segmentClock.Restart(); }
    }

    /// <summary>Closes the current file and starts the next one. Writer thread only.</summary>
    private void RollSegment()
    {
        PadSilentGap();

        string? finished = CurrentPath;
        _output?.Dispose();
        _output = null;

        _segmentIndex++;
        OpenSegment();

        if (finished is not null) SegmentCompleted?.Invoke(this, finished);
    }

    /// <summary>Closes the file at the end of a take. Writer thread only.</summary>
    private string? Finalise(ref Exception? failure)
    {
        try
        {
            if (_output is not null)
            {
                PadSilentGap();
                _output.Flush();
            }
        }
        catch (Exception ex)
        {
            // Worth surfacing rather than swallowing: a disk that fails on the closing flush has
            // probably truncated the file, and only the recorder is in a position to say so.
            Debug.WriteLine($"Finalising failed: {ex}");
            failure ??= ex;
        }

        string? path = CurrentPath;

        try { _output?.Dispose(); }
        catch (Exception ex)
        {
            Debug.WriteLine($"Closing the file failed: {ex}");
            failure ??= ex;
        }

        _output = null;
        _chain = null;
        _buffered = null;

        lock (_sync)
        {
            _totalClock.Stop();
            _segmentClock.Stop();
        }

        return path;
    }

    /// <summary>
    /// WASAPI stops delivering packets entirely while the audio engine is idle, which would
    /// silently shorten the file. Top it up with real silence so its length keeps matching the
    /// clock. Writer thread only, and only ever called with the queue already drained.
    /// </summary>
    private void PadSilentGap()
    {
        if (_output is null || _outBytesPerSecond == 0) return;

        TimeSpan segment;
        lock (_sync) { segment = _segmentClock.Elapsed; }

        long expected = (long)(segment.TotalSeconds * _outBytesPerSecond);
        long written = Volatile.Read(ref _segmentBytes);
        long deficit = expected - written;
        if (deficit < _outBytesPerSecond * PadThresholdMs / 1000) return;

        deficit -= deficit % _outBlockAlign;
        while (deficit > 0)
        {
            int chunk = (int)Math.Min(deficit, _silence.Length);
            _output.Write(_silence, 0, chunk);
            written += chunk;
            deficit -= chunk;
        }

        Volatile.Write(ref _segmentBytes, written);
    }

    private string BuildPath(RecordingRequest request, int index)
    {
        var now = DateTime.Now;
        string name = request.Template
            .Replace("{date}", now.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HHmmss"))
            .Replace("{datetime}", now.ToString("yyyy-MM-dd_HHmmss"))
            .Replace("{device}", DeviceName ?? "device")
            .Replace("{n}", index.ToString("000"));

        if (request.SplitMinutes > 0 && !request.Template.Contains("{n}")) name += $"_part{index:000}";

        name = Sanitize(name);
        if (name.Length == 0) name = "recording";

        string extension = request.Format == OutputFormat.Mp3 ? ".mp3" : ".wav";
        string path = Path.Combine(request.Folder, name + extension);
        for (int suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(request.Folder, $"{name} ({suffix}){extension}");
        }

        return path;
    }

    private static string Sanitize(string name)
    {
        foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        return name.Trim();
    }

    private static string SafeDeviceName(MMDevice device)
    {
        try { return device.FriendlyName; }
        catch { return device.ID; }
    }

    // ---- sample conversion & metering -----------------------------------------------------

    private int ConvertSamples(float[] samples, int count)
    {
        if (_floatOutput)
        {
            MemoryMarshal.AsBytes(samples.AsSpan(0, count)).CopyTo(_byteBuffer);
            return count * 4;
        }

        if (_bytesPerSample == 2)
        {
            var target = MemoryMarshal.Cast<byte, short>(_byteBuffer.AsSpan(0, count * 2));
            for (int i = 0; i < count; i++)
            {
                target[i] = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            }
            return count * 2;
        }

        for (int i = 0, offset = 0; i < count; i++, offset += 3)
        {
            int value = (int)(Math.Clamp(samples[i], -1f, 1f) * 8388607f);
            _byteBuffer[offset] = (byte)value;
            _byteBuffer[offset + 1] = (byte)(value >> 8);
            _byteBuffer[offset + 2] = (byte)(value >> 16);
        }
        return count * 3;
    }

    /// <summary>Meters the raw endpoint stream (used while monitoring, before recording).</summary>
    private void MeterSource(byte[] buffer, int bytes)
    {
        var format = SourceFormat;
        if (format is null || format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32) return;

        var samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytes / 4 * 4));
        Meter(samples, format.Channels, detectClipping: false);
    }

    /// <summary>Meters what is actually being written, so the reading follows gain and downmix.</summary>
    private void MeterOutput(float[] samples, int count)
    {
        Meter(samples.AsSpan(0, count), FileFormat?.Channels ?? 2, detectClipping: true);
    }

    private void Meter(ReadOnlySpan<float> samples, int channels, bool detectClipping)
    {
        if (channels <= 0) return;

        float left = 0f, right = 0f;
        bool clipped = false;

        for (int i = 0; i + channels <= samples.Length; i += channels)
        {
            float a = Math.Abs(samples[i]);
            float b = Math.Abs(samples[channels > 1 ? i + 1 : i]);
            if (a > left) left = a;
            if (b > right) right = b;
            if (detectClipping && (a >= ClipThreshold || b >= ClipThreshold)) clipped = true;
        }

        if (clipped) _clipped = true;

        lock (_meterLock)
        {
            if (left > _peakLeft) _peakLeft = left;
            if (right > _peakRight) _peakRight = right;
        }
    }

    private void ResetPeaks()
    {
        lock (_meterLock)
        {
            _peakLeft = 0f;
            _peakRight = 0f;
        }
    }

    public void Dispose()
    {
        // Unhook before anything else: NAudio posts RecordingStopped to the thread that built the
        // capture, and by now nothing is pumping that thread's message queue.
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnCaptureStopped;
        }

        StopRecording();

        if (capture is not null)
        {
            try { capture.StopRecording(); } catch { /* shutting down */ }
            capture.Dispose();
        }

        _device?.Dispose();
        _device = null;
        _deviceEnumerator.Dispose();

        // Only safe once the writer has actually gone; it waits on this signal.
        if (!_recording) _writerSignal.Dispose();
    }
}
