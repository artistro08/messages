using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Holds the waveform and length of a voice message, from its decoded audio.
/// </summary>
/// <remarks>
/// The proto has no duration or waveform field, as the research doc found, so both come from the decoded samples. The
/// app decodes the voice message to 16-bit PCM WAV once, after its first download. It keeps the result only in memory,
/// because the spec says that it must not stay between launches.
/// </remarks>
/// <param name="Peaks">
/// The bars, each the loudest sample of its slice. The loudest bar is 1, and silence gives all 0.
/// </param>
/// <param name="Length">The time that the audio plays.</param>
public sealed record VoiceWave(float[] Peaks, TimeSpan Length)
{
    /// <summary>The number of bars in a waveform.</summary>
    public const int BarCount = 48;

    // The WAV format tags: integer PCM, and WAVE_FORMAT_EXTENSIBLE, whose sub-format says PCM.
    private const ushort PcmFormat        = 1;
    private const ushort ExtensibleFormat = 0xFFFE;

    // The quietest input that the live waveform draws, in decibels. Any input under it is silence.
    private const double QuietestLevelDb = -50;

    /// <summary>
    /// Reads the waveform and the length of a 16-bit PCM WAV file.
    /// </summary>
    /// <remarks>
    /// The method walks the RIFF chunks for <c>fmt </c> and <c>data</c>. An unfinished stream can have a data chunk
    /// whose size is not set or runs past the end. That chunk takes the rest of the file. The method
    /// mixes the channels by taking the loudest.
    /// </remarks>
    /// <param name="wav">The bytes of the WAV file.</param>
    /// <param name="bars">The number of bars to make.</param>
    /// <returns>The waveform and length.</returns>
    /// <exception cref="InvalidDataException">The file is not a 16-bit PCM WAV file.</exception>
    public static VoiceWave FromWav(ReadOnlySpan<byte> wav, int bars = BarCount)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a WAV file.");
        }

        int channels    = 0;
        int sampleRate  = 0;
        int at          = 12;
        while (at + 8 <= wav.Length)
        {
            ReadOnlySpan<byte> id = wav.Slice(at, 4);
            uint size             = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(at + 4, 4));
            int body              = at + 8;

            // The format must be PCM or extensible PCM, with 16 bits.
            if (id.SequenceEqual("fmt "u8) && body + 16 <= wav.Length)
            {
                ushort format = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body, 2));
                channels      = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 2, 2));
                sampleRate    = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(body + 4, 4));
                int bits      = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 14, 2));
                if (format is not (PcmFormat or ExtensibleFormat) || bits != 16 || channels == 0 || sampleRate == 0)
                {
                    throw new InvalidDataException("Not 16-bit PCM.");
                }
            }

            // The samples are the chunk, or the rest of a file that the writer did not finish.
            if (id.SequenceEqual("data"u8))
            {
                if (channels == 0)
                {
                    throw new InvalidDataException("No format before the samples.");
                }

                int length                = size == 0 || size > (uint)(wav.Length - body) ? wav.Length - body : (int)size;
                ReadOnlySpan<byte> pcm    = wav.Slice(body, length - (length % (2 * channels)));
                long frames               = pcm.Length / (2 * channels);
                return new VoiceWave(PeaksOf(pcm, channels, bars), TimeSpan.FromSeconds((double)frames / sampleRate));
            }

            // Chunks have padding to an even size.
            at = body + (int)Math.Min(size + (size & 1), (uint)(wav.Length - body));
        }

        throw new InvalidDataException("No samples.");
    }

    /// <summary>
    /// Splits 16-bit little-endian samples into bars. Each bar is the loudest sample of its slice, and the loudest bar
    /// is 1.
    /// </summary>
    /// <remarks>With fewer frames than bars, the bars past the frames stay at 0. Silence gives all 0.</remarks>
    /// <param name="pcm">Interleaved 16-bit little-endian samples.</param>
    /// <param name="channels">The number of interleaved channels.</param>
    /// <param name="bars">The number of bars to make.</param>
    /// <returns>The bars, from 0 to 1.</returns>
    public static float[] PeaksOf(ReadOnlySpan<byte> pcm, int channels, int bars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(bars, 1);

        float[] peaks = new float[bars];
        long frames   = pcm.Length / (2 * channels);
        if (frames == 0)
        {
            return peaks;
        }

        for (long frame = 0; frame < frames; frame++)
        {
            int bar = (int)(frame * bars / frames);
            for (int channel = 0; channel < channels; channel++)
            {
                int offset   = (int)(((frame * channels) + channel) * 2);
                float sample = Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(offset, 2)) / 32768f);
                peaks[bar]   = Math.Max(peaks[bar], sample);
            }
        }

        // The bars scale to the loudest, so a quiet recording still shows its shape.
        float loudest = peaks.Max();
        if (loudest > 0)
        {
            for (int i = 0; i < peaks.Length; i++)
            {
                peaks[i] /= loudest;
            }
        }

        return peaks;
    }

    /// <summary>
    /// Gives the live input level of a block of 32-bit float samples, which is 1 frame of an audio graph. The level is
    /// the loudest sample in decibels, from -50 dB as 0 to full scale as 1. Level meters show it the same way.
    /// </summary>
    /// <remarks>
    /// Speech mostly peaks at 5 to 30 percent of full scale. Thus the plain peak drew short bars that almost did not
    /// move. On the decibel scale, the level fills the bar and follows the voice. The method ignores a partial sample
    /// at the end. Silence or no samples gives 0, and clipping stops at 1.
    /// </remarks>
    /// <param name="floats">Interleaved 32-bit float samples, as bytes.</param>
    /// <returns>The level, from 0 to 1.</returns>
    public static float LevelOf(ReadOnlySpan<byte> floats)
    {
        float peak = 0;
        foreach (float sample in MemoryMarshal.Cast<byte, float>(floats))
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        if (peak <= 0)
        {
            return 0;
        }

        double db = 20 * Math.Log10(Math.Min(1, peak));
        return (float)Math.Clamp(1 + (db / -QuietestLevelDb), 0, 1);
    }

    /// <summary>
    /// Writes a length as the bubble shows it: minutes and seconds of 2 digits, such as <c>0:07</c> or <c>12:30</c>.
    /// </summary>
    /// <remarks>
    /// The method rounds to the nearest whole second, because decoded audio runs a few milliseconds long. A message
    /// under half a second reads 0:01, never 0:00.
    /// </remarks>
    /// <param name="length">The length.</param>
    /// <returns>The text.</returns>
    public static string LengthText(TimeSpan length)
    {
        double total = Math.Max(0, length.TotalSeconds);
        long seconds = total > 0 ? Math.Max(1, (long)Math.Round(total, MidpointRounding.AwayFromZero)) : 0;
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }
}
