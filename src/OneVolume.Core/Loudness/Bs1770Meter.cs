namespace OneVolume.Core.Loudness;

/// <summary>
/// Streaming ITU-R BS.1770-4 loudness meter: K-weighting (shelf + high-pass biquads per
/// channel) into a sliding momentary window (400 ms). This is the same measurement
/// YouTube/Spotify/Netflix normalize with — it tracks *perceived* loudness, unlike a
/// peak meter which a heavily-compressed ad fools completely.
/// Fed from a capture thread; <see cref="MomentaryLufs"/> is safe to read from any thread.
/// </summary>
public sealed class Bs1770Meter
{
    private const double WindowSeconds = 0.4;

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly Biquad[] _shelf;
    private readonly Biquad[] _highPass;

    // Ring buffer of per-block mean squares (blocks of ~10 ms) covering the window.
    private readonly double[] _blockEnergy;
    private readonly int _samplesPerBlock;
    private int _blockIndex;
    private double _currentBlockSum;
    private int _currentBlockCount;
    private long _blocksWritten;

    private double _momentaryLufs = double.NaN;

    public Bs1770Meter(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _shelf = new Biquad[channels];
        _highPass = new Biquad[channels];
        for (int c = 0; c < channels; c++)
        {
            _shelf[c] = Biquad.KShelf(sampleRate);
            _highPass[c] = Biquad.KHighPass(sampleRate);
        }

        _samplesPerBlock = Math.Max(1, sampleRate / 100); // 10 ms blocks
        _blockEnergy = new double[(int)Math.Ceiling(WindowSeconds * 100)]; // 40 blocks
    }

    /// <summary>Loudness of the last 400 ms, in LUFS; NaN until a full window exists.</summary>
    public double MomentaryLufs => Volatile.Read(ref _momentaryLufs);

    /// <summary>Feeds interleaved float samples (any count) from the capture thread.</summary>
    public void Process(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        for (int i = 0; i < frames; i++)
        {
            double frameEnergy = 0;
            for (int c = 0; c < _channels; c++)
            {
                double y = _highPass[c].Process(_shelf[c].Process(interleaved[i * _channels + c]));
                frameEnergy += y * y; // channel weights are 1.0 for L/R (BS.1770 table)
            }

            _currentBlockSum += frameEnergy;
            if (++_currentBlockCount >= _samplesPerBlock)
            {
                _blockEnergy[_blockIndex] = _currentBlockSum / _currentBlockCount;
                _blockIndex = (_blockIndex + 1) % _blockEnergy.Length;
                _blocksWritten++;
                _currentBlockSum = 0;
                _currentBlockCount = 0;

                if (_blocksWritten >= _blockEnergy.Length)
                {
                    double mean = 0;
                    foreach (double e in _blockEnergy)
                    {
                        mean += e;
                    }

                    mean /= _blockEnergy.Length;
                    double lufs = mean <= 1e-12
                        ? double.NegativeInfinity
                        : -0.691 + 10 * Math.Log10(mean);
                    Volatile.Write(ref _momentaryLufs, lufs);
                }
            }
        }
    }

    /// <summary>One-shot measurement over a whole buffer (test/diagnostic path).</summary>
    public static double MeasureLufs(ReadOnlySpan<float> interleaved, int sampleRate, int channels)
    {
        if (interleaved.Length == 0)
        {
            return double.NegativeInfinity;
        }

        int frames = interleaved.Length / channels;
        double sum = 0;
        for (int c = 0; c < channels; c++)
        {
            Biquad shelf = Biquad.KShelf(sampleRate);
            Biquad highPass = Biquad.KHighPass(sampleRate);
            double ms = 0;
            for (int i = 0; i < frames; i++)
            {
                double y = highPass.Process(shelf.Process(interleaved[i * channels + c]));
                ms += y * y;
            }

            sum += ms / frames;
        }

        return sum <= 1e-15 ? double.NegativeInfinity : -0.691 + 10 * Math.Log10(sum);
    }
}

/// <summary>One biquad stage; BS.1770 pre-filter coefficients derived for any sample rate.</summary>
public sealed class Biquad
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    private Biquad(double b0, double b1, double b2, double a1, double a2)
        => (_b0, _b1, _b2, _a1, _a2) = (b0, b1, b2, a1, a2);

    public double Process(double x)
    {
        double y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    /// <summary>Stage 1: +4 dB high shelf (head acoustics), ITU-R BS.1770-4 reference design.</summary>
    public static Biquad KShelf(int sampleRate)
    {
        double f0 = 1681.974450955533;
        double gainDb = 3.999843853973347;
        double q = 0.7071752369554196;

        double k = Math.Tan(Math.PI * f0 / sampleRate);
        double vh = Math.Pow(10.0, gainDb / 20.0);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1.0 + k / q + k * k;
        return new Biquad(
            (vh + vb * k / q + k * k) / a0,
            2.0 * (k * k - vh) / a0,
            (vh - vb * k / q + k * k) / a0,
            2.0 * (k * k - 1.0) / a0,
            (1.0 - k / q + k * k) / a0);
    }

    /// <summary>Stage 2: high-pass (revised low-frequency B-curve), ITU-R BS.1770-4.</summary>
    public static Biquad KHighPass(int sampleRate)
    {
        double f0 = 38.13547087602444;
        double q = 0.5003270373238773;
        double k = Math.Tan(Math.PI * f0 / sampleRate);
        double a0 = 1.0 + k / q + k * k;
        return new Biquad(
            1.0,
            -2.0,
            1.0,
            2.0 * (k * k - 1.0) / a0,
            (1.0 - k / q + k * k) / a0);
    }
}
