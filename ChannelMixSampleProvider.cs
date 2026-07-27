using NAudio.Wave;

namespace WindowAudioRecorder;

/// <summary>
/// Mixes an arbitrary multichannel stream down to mono or stereo using the usual ITU-R BS.775
/// coefficients, so a 5.1 or 7.1 endpoint folds down properly instead of losing its centre and
/// surround channels.
/// </summary>
public sealed class ChannelMixSampleProvider : ISampleProvider
{
    private enum Role { FrontLeft, FrontRight, Center, Lfe, BackLeft, BackRight, SideLeft, SideRight, BackCenter, Other }

    private readonly ISampleProvider _source;
    private readonly int _inChannels;
    private readonly int _outChannels;
    private readonly float[][] _matrix;   // [outChannel][inChannel]
    private float[] _sourceBuffer = [];

    public ChannelMixSampleProvider(ISampleProvider source, int outChannels)
    {
        if (outChannels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(outChannels), "Downmix targets mono or stereo.");

        _source = source;
        _inChannels = source.WaveFormat.Channels;
        _outChannels = outChannels;
        _matrix = BuildMatrix(_inChannels, outChannels);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _outChannels;
        int needed = frames * _inChannels;
        if (_sourceBuffer.Length < needed) _sourceBuffer = new float[needed];

        int read = _source.Read(_sourceBuffer, 0, needed);
        int fullFrames = read / _inChannels;

        for (int frame = 0; frame < fullFrames; frame++)
        {
            int inBase = frame * _inChannels;
            int outBase = offset + frame * _outChannels;

            for (int outCh = 0; outCh < _outChannels; outCh++)
            {
                float[] weights = _matrix[outCh];
                float sum = 0f;
                for (int inCh = 0; inCh < _inChannels; inCh++)
                {
                    float weight = weights[inCh];
                    if (weight != 0f) sum += _sourceBuffer[inBase + inCh] * weight;
                }
                buffer[outBase + outCh] = sum;
            }
        }

        return fullFrames * _outChannels;
    }

    private static float[][] BuildMatrix(int inChannels, int outChannels)
    {
        var roles = LayoutFor(inChannels);

        // Build the stereo fold first; mono is the average of the two stereo legs.
        var left = new float[inChannels];
        var right = new float[inChannels];

        for (int i = 0; i < inChannels; i++)
        {
            (left[i], right[i]) = roles[i] switch
            {
                Role.FrontLeft => (1.000f, 0.000f),
                Role.FrontRight => (0.000f, 1.000f),
                Role.Center => (0.707f, 0.707f),
                Role.Lfe => (0.500f, 0.500f),
                Role.BackLeft => (0.707f, 0.000f),
                Role.BackRight => (0.000f, 0.707f),
                Role.SideLeft => (0.707f, 0.000f),
                Role.SideRight => (0.000f, 0.707f),
                Role.BackCenter => (0.500f, 0.500f),
                // Unknown layout: fold odd/even channels left/right so nothing is simply dropped.
                _ => i % 2 == 0 ? (0.707f, 0.000f) : (0.000f, 0.707f)
            };
        }

        if (outChannels == 2) return [left, right];

        var mono = new float[inChannels];
        for (int i = 0; i < inChannels; i++) mono[i] = (left[i] + right[i]) * 0.5f;
        return [mono];
    }

    /// <summary>Channel roles in WAVE channel order for the common speaker layouts.</summary>
    private static Role[] LayoutFor(int channels) => channels switch
    {
        1 => [Role.Center],
        2 => [Role.FrontLeft, Role.FrontRight],
        3 => [Role.FrontLeft, Role.FrontRight, Role.Lfe],
        4 => [Role.FrontLeft, Role.FrontRight, Role.BackLeft, Role.BackRight],
        5 => [Role.FrontLeft, Role.FrontRight, Role.Center, Role.BackLeft, Role.BackRight],
        6 => [Role.FrontLeft, Role.FrontRight, Role.Center, Role.Lfe, Role.BackLeft, Role.BackRight],
        7 => [Role.FrontLeft, Role.FrontRight, Role.Center, Role.Lfe, Role.BackCenter, Role.SideLeft, Role.SideRight],
        8 => [Role.FrontLeft, Role.FrontRight, Role.Center, Role.Lfe, Role.BackLeft, Role.BackRight, Role.SideLeft, Role.SideRight],
        _ => Enumerable.Repeat(Role.Other, channels).ToArray()
    };
}
