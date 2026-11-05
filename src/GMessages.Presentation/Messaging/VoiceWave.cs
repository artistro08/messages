using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// A voice message's waveform and length, from its decoded audio.
/// </summary>
/// <remarks>
/// The proto has no duration or waveform field (research doc), so both come from the decoded samples: the app decodes
/// the voice message to 16-bit PCM WAV once, after its first download, and keeps the result in memory only (spec: not
/// cached between launches).
/// </remarks>
/// <param name="Peaks">The bars, each the loudest sample of its slice, scaled so the loudest bar is 1 (all 0 for silence).</param>
/// <param name="Length">How long the audio plays.</param>
public sealed record VoiceWave(float[] Peaks, TimeSpan Length)
{
    /// <summary>How many bars a waveform has.</summary>
    public const int BarCount = 48;

    // WAV Format Tags: Integer PCM, And WAVE_FORMAT_EXTENSIBLE (Its Sub-Format Says PCM)
    private const ushort PcmFormat        = 1;
    private const ushort ExtensibleFormat = 0xFFFE;

    // The Quietest Input The Live Waveform Draws, In Decibels; Anything Under It Is Silence
    private const double QuietestLevelDb = -50;

    /// <summary>
    /// Reads a 16-bit PCM WAV file: its waveform and its length.
    /// </summary>
    /// <remarks>
    /// Walks the RIFF chunks for <c>fmt </c> and <c>data</c>. A data chunk whose size is unset or runs past the end
    /// (a stream the writer never finished) takes the rest of the file. Channels are mixed by taking the loudest.
    /// </remarks>
    /// <param name="wav">The WAV file's bytes.</param>
    /// <param name="bars">How many bars to make.</param>
    /// <returns>The waveform and length.</returns>
    /// <exception cref="InvalidDataException">Not a 16-bit PCM WAV file.</exception>
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

            // Format: PCM (Or Extensible PCM), 16 Bits
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

            // Samples: The Chunk, Or The Rest Of An Unfinished File
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

            // Chunks Are Padded To An Even Size
            at = body + (int)Math.Min(size + (size & 1), (uint)(wav.Length - body));
        }

        throw new InvalidDataException("No samples.");
    }

    /// <summary>
    /// Splits 16-bit little-endian samples into bars: each the loudest sample of its slice, scaled so the loudest bar
    /// is 1.
    /// </summary>
    /// <remarks>Fewer frames than bars leaves the bars past them at 0; silence gives all 0.</remarks>
    /// <param name="pcm">Interleaved 16-bit little-endian samples.</param>
    /// <param name="channels">How many channels are interleaved.</param>
    /// <param name="bars">How many bars to make.</param>
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

        // Each Bar's Slice Of Frames: The Loudest Sample In It
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

        // Scaled To The Loudest, So A Quiet Recording Still Shows Its Shape
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
    /// Gives the live input level of a block of 32-bit float samples (an audio graph's frame): the loudest sample, in
    /// decibels from -50 dB (0) to full scale (1), as level meters show it.
    /// </summary>
    /// <remarks>
    /// Speech mostly peaks at 5 to 30 percent of full scale, so the plain peak drew short bars that barely moved; on
    /// the decibel scale it fills the bar and follows the voice. Any trailing partial sample is ignored; silence or no
    /// samples gives 0, and clipping caps at 1.
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
    /// Writes a length the way the bubble shows it: minutes and two-digit seconds, such as <c>0:07</c> or <c>12:30</c>.
    /// </summary>
    /// <remarks>
    /// Rounded to the nearest whole second (decoded audio runs a few milliseconds long), but a message under half a
    /// second reads 0:01, never 0:00.
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
