using System.Buffers.Binary;
using GMessages.Presentation.Messaging;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class VoiceWaveTests
{
    // Expected Bars
    private static readonly float[] LoudThenHalf = [1f, 0.5f];
    private static readonly float[] HalfThenLoud = [0.5f, 1f];
    private static readonly float[] LoudThenFlat = [1f, 0f, 0f];

    /// <summary>
    /// Each bar is its slice's loudest sample, scaled so the loudest bar is 1; a stereo frame counts its louder channel.
    /// </summary>
    [TestMethod]
    public void Peaks_LoudestPerSlice_ScaledToLoudest()
    {
        // Four Mono Frames, Two Bars: |1000|-4000| Then |2000|500|
        float[] mono = VoiceWave.PeaksOf(Pcm(1000, -4000, 2000, 500), channels: 1, bars: 2);
        CollectionAssert.AreEqual(LoudThenHalf, mono);

        // Two Stereo Frames, Two Bars: The Louder Channel Of Each
        float[] stereo = VoiceWave.PeaksOf(Pcm(100, -800, 400, 0), channels: 2, bars: 2);
        CollectionAssert.AreEqual(LoudThenHalf, stereo);
    }

    /// <summary>
    /// Silence gives flat bars, and fewer frames than bars leaves the rest at 0, without dividing by zero.
    /// </summary>
    [TestMethod]
    public void Peaks_SilenceAndShortAudio()
    {
        CollectionAssert.AreEqual(new float[4], VoiceWave.PeaksOf(Pcm(0, 0, 0, 0), 1, 4));
        CollectionAssert.AreEqual(new float[3], VoiceWave.PeaksOf([], 1, 3));

        float[] short_audio = VoiceWave.PeaksOf(Pcm(1000), 1, 3);
        CollectionAssert.AreEqual(LoudThenFlat, short_audio);
    }

    /// <summary>
    /// The live level of a frame of 32-bit float samples is its loudest sample: a 440 Hz sine at 0.5 reads 0.5,
    /// clipping caps at 1, and silence, no samples or a partial sample read 0.
    /// </summary>
    [TestMethod]
    public void Level_LoudestFloatSample()
    {
        // A 440 Hz Sine At Half Volume, 48 kHz Stereo, 10 ms
        float[] sine = new float[480 * 2];
        for (int i = 0; i < sine.Length; i++)
        {
            sine[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * (i / 2) / 48000.0));
        }

        Assert.AreEqual(0.5f, VoiceWave.LevelOf(Floats(sine)), 0.01f);
        Assert.AreEqual(0.8f, VoiceWave.LevelOf(Floats(0.1f, -0.8f, 0.3f)), 0.0001f);
        Assert.AreEqual(1f, VoiceWave.LevelOf(Floats(1.7f, -0.2f)));
        Assert.AreEqual(0f, VoiceWave.LevelOf(Floats(0f, 0f)));
        Assert.AreEqual(0f, VoiceWave.LevelOf([]));
        Assert.AreEqual(0f, VoiceWave.LevelOf([1, 2, 3]));
    }

    /// <summary>
    /// A WAV file gives its bars and its length from the sample rate; an unfinished one (data size 0) reads to the end.
    /// </summary>
    [TestMethod]
    public void FromWav_BarsAndLength()
    {
        byte[] samples = Pcm([.. Enumerable.Repeat((short)1000, 8000), .. Enumerable.Repeat((short)-2000, 8000)]);

        VoiceWave wave = VoiceWave.FromWav(Wav(samples, 8000, dataSize: (uint)samples.Length), bars: 2);
        Assert.AreEqual(TimeSpan.FromSeconds(2), wave.Length);
        CollectionAssert.AreEqual(HalfThenLoud, wave.Peaks);

        VoiceWave unfinished = VoiceWave.FromWav(Wav(samples, 8000, dataSize: 0), bars: 2);
        Assert.AreEqual(TimeSpan.FromSeconds(2), unfinished.Length);
        Assert.HasCount(VoiceWave.BarCount, VoiceWave.FromWav(Wav(samples, 8000, (uint)samples.Length)).Peaks);
    }

    /// <summary>
    /// Anything but a 16-bit PCM WAV file is refused.
    /// </summary>
    [TestMethod]
    public void FromWav_NotWav_Throws()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => VoiceWave.FromWav("not a wav file"u8));
        byte[] eight_bit = Wav([1, 2], 8000, 2);
        eight_bit[34]    = 8;
        Assert.ThrowsExactly<InvalidDataException>(() => VoiceWave.FromWav(eight_bit));
    }

    /// <summary>
    /// A length reads as minutes and two-digit seconds, rounded, and never 0:00 for audio that plays.
    /// </summary>
    [TestMethod]
    public void LengthText_MinutesAndSeconds()
    {
        Assert.AreEqual("0:00", VoiceWave.LengthText(TimeSpan.Zero));
        Assert.AreEqual("0:01", VoiceWave.LengthText(TimeSpan.FromMilliseconds(300)));
        Assert.AreEqual("0:03", VoiceWave.LengthText(TimeSpan.FromMilliseconds(3020)));
        Assert.AreEqual("12:30", VoiceWave.LengthText(TimeSpan.FromSeconds(750)));
    }

    // 16-Bit Little-Endian Samples
    private static byte[] Pcm(params short[] samples)
    {
        byte[] bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        }

        return bytes;
    }

    // 32-Bit Float Samples, As An Audio Graph's Frame Holds Them
    private static byte[] Floats(params float[] samples) =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();

    // A Mono 16-Bit PCM WAV File, With A LIST Chunk Before The Samples As Some Writers Add
    private static byte[] Wav(byte[] samples, int rate, uint dataSize)
    {
        using var wav    = new MemoryStream();
        using var writer = new BinaryWriter(wav);
        writer.Write("RIFF"u8);
        writer.Write(0u);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((uint)rate);
        writer.Write((uint)(rate * 2));
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write("LIST"u8);
        writer.Write(3u);
        writer.Write(new byte[4]);
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(samples);
        return wav.ToArray();
    }
}
