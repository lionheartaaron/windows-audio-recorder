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
/// </summary>
public sealed class LoopbackRecorder : IDisposable
{
    private const int PadThresholdMs = 300;   // comfortably above the pipeline's own latency
    private const float ClipThreshold = 0.999f;

    private readonly object _sync = new();
    private readonly object _meterLock = new();
    private readonly Stopwatch _totalClock = new();
    private readonly Stopwatch _segmentClock = new();
    private readonly List<string> _pendingCompleted = [];

    private readonly MMDeviceEnumerator _deviceEnumerator = new();

    private WasapiLoopbackCapture? _capture;
    private MMDevice? _device;
    private TaskCompletionSource<Exception?>? _captureStopSignal;
    private System.Threading.Timer? _ticker;

    private BufferedWaveProvider? _buffered;
    private ISampleProvider? _chain;
    private VolumeSampleProvider? _volume;
    private Stream? _writer;
    private RecordingRequest? _request;
    private int _gainDb;

    private float[] _sampleBuffer = [];
    private byte[] _byteBuffer = [];
    private byte[] _silence = [];

    private bool _floatOutput;
    private int _bytesPerSample;
    private int _outBytesPerSecond;
    private int _outBlockAlign;
    private int _segmentIndex;
    private long _segmentBytes;
    private long _totalBytes;
    private bool _pendingAutoStop;

    private volatile bool _recording;
    private volatile bool _paused;
    private volatile bool _clipped;

    private float _peakLeft;
    private float _peakRight;

    /// <summary>Raised for every finalised file, including each piece of a split recording.</summary>
    public event EventHandler<string>? SegmentCompleted;

    /// <summary>Raised when capture ends without being asked to (endpoint unplugged, format change).</summary>
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
    public string? CurrentPath { get; private set; }

    /// <summary>Set when the requested settings had to be adjusted (an MP3 rate limit, say).</summary>
    public string? Notice { get; private set; }

    public TimeSpan Elapsed => _totalClock.Elapsed;
    public int OutputBytesPerSecond => _outBytesPerSecond;

    public long EstimatedFileBytes => _request?.Format == OutputFormat.Mp3
        ? (long)(_segmentClock.Elapsed.TotalSeconds * _request.Mp3Bitrate * 125)
        : 44 + _segmentBytes;

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
                    Notice = "MP3 is stereo at most — downmixed to stereo.";
                }

                int mp3Rate = AppSettings.NearestMp3Rate(outRate);
                if (mp3Rate != outRate)
                {
                    Notice = $"MP3 cannot encode {outRate:n0} Hz — recording at {mp3Rate:n0} Hz.";
                    outRate = mp3Rate;
                }
            }

            _buffered = new BufferedWaveProvider(source)
            {
                ReadFully = false,               // must not manufacture silence; padding is our job
                DiscardOnBufferOverflow = true,  // a stalled disk drops audio rather than throwing
                BufferDuration = TimeSpan.FromSeconds(5)
            };

            ISampleProvider chain = _buffered.ToSampleProvider();
            if (outChannels != source.Channels) chain = new ChannelMixSampleProvider(chain, outChannels);
            if (outRate != source.SampleRate) chain = new WdlResamplingSampleProvider(chain, outRate);

            // Always in the chain, even at 0 dB, so the slider can be moved mid-take.
            _gainDb = request.GainDb;
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
            _totalBytes = 0;
            _clipped = false;
            ResetPeaks();

            Directory.CreateDirectory(request.Folder);
            OpenSegment();

            _paused = false;
            _recording = true;
            _totalClock.Restart();

            // Silence must not stall the limits: WASAPI delivers no packets at all while the
            // audio engine is idle, so anything driven only by DataAvailable would overrun its
            // split point and keep the file short. This ticks regardless.
            _ticker = new System.Threading.Timer(OnTick, null, 250, 250);
        }
    }

    private void OnTick(object? state)
    {
        lock (_sync)
        {
            if (_recording && !_paused)
            {
                try
                {
                    PadSilentGap();
                    EnforceLimits();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Tick failed: {ex}");
                }
            }
        }

        DrainNotifications();
    }

    /// <summary>Finalises the current file. Returns its path, or null if nothing was recording.</summary>
    public string? StopRecording()
    {
        string? path;
        lock (_sync)
        {
            path = FinishRecording();
        }

        if (path is not null) SegmentCompleted?.Invoke(this, path);
        return path;
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (!_recording || _paused) return;
            _paused = true;
            _totalClock.Stop();
            _segmentClock.Stop();
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (!_recording || !_paused) return;
            // Drop whatever queued up while paused so the take resumes cleanly.
            _buffered?.ClearBuffer();
            _paused = false;
            _totalClock.Start();
            _segmentClock.Start();
        }
    }

    /// <summary>Applies a gain in dB, taking effect immediately even mid-recording.</summary>
    public void SetGain(int decibels)
    {
        lock (_sync)
        {
            _gainDb = decibels;
            if (_volume is not null) _volume.Volume = GainToVolume(decibels);
        }
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

        if (!_recording || _paused)
        {
            MeterSource(e.Buffer, e.BytesRecorded);
            return;
        }

        Exception? failure = null;
        lock (_sync)
        {
            if (_recording && _buffered is not null && _chain is not null)
            {
                try
                {
                    _buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    Pump();
                }
                catch (Exception ex)
                {
                    failure = ex;
                    FinishRecording();
                }
            }
        }

        // Events go out after the lock so a handler can call back in without deadlocking.
        DrainNotifications();
        if (failure is not null) Aborted?.Invoke(this, failure);
    }

    private void Pump()
    {
        PadSilentGap();

        for (int guard = 0; guard < 64 && _recording; guard++)
        {
            int read = _chain!.Read(_sampleBuffer, 0, _sampleBuffer.Length);
            if (read <= 0) break;

            MeterOutput(_sampleBuffer, read);
            int bytes = ConvertSamples(_sampleBuffer, read);
            _writer!.Write(_byteBuffer, 0, bytes);
            _segmentBytes += bytes;
            _totalBytes += bytes;

            EnforceLimits();
            if (read < _sampleBuffer.Length) break;   // source drained
        }
    }

    private void EnforceLimits()
    {
        var request = _request;
        if (request is null || !_recording) return;

        if (request.MaxMinutes > 0 && _totalClock.Elapsed.TotalMinutes >= request.MaxMinutes)
        {
            string? path = FinishRecording();
            if (path is not null) _pendingCompleted.Add(path);
            _pendingAutoStop = true;
            return;
        }

        if (request.SplitMinutes > 0 && _segmentClock.Elapsed.TotalMinutes >= request.SplitMinutes)
        {
            RollSegment();
        }
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        string? finalized;
        lock (_sync)
        {
            finalized = FinishRecording();
        }

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnCaptureStopped;
            _capture.Dispose();
            _capture = null;
        }

        _device?.Dispose();
        _device = null;
        SourceFormat = null;
        CaptureDeviceId = null;

        if (finalized is not null) SegmentCompleted?.Invoke(this, finalized);

        var signal = _captureStopSignal;
        _captureStopSignal = null;
        if (signal is not null) signal.TrySetResult(e.Exception);
        else Aborted?.Invoke(this, e.Exception);
    }

    // ---- file plumbing -------------------------------------------------------------------

    /// <summary>Opens the next output file. Caller holds <see cref="_sync"/>.</summary>
    private void OpenSegment()
    {
        var request = _request!;
        string path = BuildPath(request, _segmentIndex);

        _writer = request.Format == OutputFormat.Mp3
            ? new LameMP3FileWriter(path, FileFormat, request.Mp3Bitrate)
            : new WaveFileWriter(path, FileFormat);

        CurrentPath = path;
        _segmentBytes = 0;
        _segmentClock.Restart();
    }

    /// <summary>Closes the current file and starts the next one. Caller holds <see cref="_sync"/>.</summary>
    private void RollSegment()
    {
        PadSilentGap();
        _writer?.Dispose();
        _writer = null;

        if (CurrentPath is not null) _pendingCompleted.Add(CurrentPath);

        _segmentIndex++;
        OpenSegment();
    }

    /// <summary>Finalises the file if one is open. Caller holds <see cref="_sync"/>.</summary>
    private string? FinishRecording()
    {
        if (!_recording) return null;

        _recording = false;
        _paused = false;

        _ticker?.Dispose();   // safe even when called from the ticker's own callback
        _ticker = null;

        try
        {
            if (_writer is not null)
            {
                PadSilentGap();
                _writer.Flush();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Finalising failed: {ex}");
        }

        _writer?.Dispose();
        _writer = null;
        _chain = null;
        _buffered = null;

        _totalClock.Stop();
        _segmentClock.Stop();

        return CurrentPath;
    }

    private void DrainNotifications()
    {
        string[] completed;
        bool autoStopped;

        lock (_sync)
        {
            if (_pendingCompleted.Count == 0 && !_pendingAutoStop) return;
            completed = [.. _pendingCompleted];
            _pendingCompleted.Clear();
            autoStopped = _pendingAutoStop;
            _pendingAutoStop = false;
        }

        foreach (string path in completed) SegmentCompleted?.Invoke(this, path);
        if (autoStopped) AutoStopped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// WASAPI stops delivering packets entirely while the audio engine is idle, which would
    /// silently shorten the file. Top it up with real silence so its length keeps matching the
    /// clock. Caller holds <see cref="_sync"/>.
    /// </summary>
    private void PadSilentGap()
    {
        if (_writer is null || _outBytesPerSecond == 0) return;

        long expected = (long)(_segmentClock.Elapsed.TotalSeconds * _outBytesPerSecond);
        long deficit = expected - _segmentBytes;
        if (deficit < _outBytesPerSecond * PadThresholdMs / 1000) return;

        deficit -= deficit % _outBlockAlign;
        while (deficit > 0)
        {
            int chunk = (int)Math.Min(deficit, _silence.Length);
            _writer.Write(_silence, 0, chunk);
            _segmentBytes += chunk;
            _totalBytes += chunk;
            deficit -= chunk;
        }
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
        lock (_sync)
        {
            FinishRecording();
        }

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnCaptureStopped;
            try { _capture.StopRecording(); } catch { /* shutting down */ }
            _capture.Dispose();
            _capture = null;
        }

        _device?.Dispose();
        _device = null;
        _deviceEnumerator.Dispose();
    }
}
