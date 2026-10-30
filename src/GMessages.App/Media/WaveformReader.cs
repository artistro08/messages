using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using GMessages.Presentation.Messaging;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace GMessages.App.Media;

/// <summary>
/// Reads a voice message's waveform and length from its audio file, once per downloaded file.
/// </summary>
/// <remarks>
/// <para>
/// Windows' <see cref="MediaTranscoder"/> decodes the M4A/AAC file to 8 kHz mono 16-bit WAV in memory, faster than
/// real time (an AudioGraph file input runs at playback speed), then <see cref="VoiceWave.FromWav"/> takes about 48
/// peak bars and the length from the samples. Nothing touches the disk.
/// </para>
/// <para>
/// The result is kept in memory only, keyed to the decrypted bytes the media store hands out: when the store lets go of
/// the bytes (its memory cap, or a sign-out), the waveform goes with them. The bytes are never logged.
/// </para>
/// </remarks>
internal static class WaveformReader
{
    // Enough To Draw Bars From, And Small: Phone-Call Quality, One Channel
    private const uint SampleRate = 8000;

    // Waveforms Already Read (Or Being Read), By The Bytes They Came From
    private static readonly ConditionalWeakTable<byte[], Task<VoiceWave>> Read = [];

    /// <summary>
    /// Gets the waveform and length of an audio file.
    /// </summary>
    /// <param name="audio">The audio file's bytes, such as an M4A voice message.</param>
    /// <returns>The waveform and length.</returns>
    /// <exception cref="InvalidDataException">Windows can't decode the file.</exception>
    public static Task<VoiceWave> ReadAsync(byte[] audio)
    {
        ArgumentNullException.ThrowIfNull(audio);

        return Read.GetValue(audio, static a => DecodeAsync(a));
    }

    // Decodes To WAV In Memory, Then Reads The Bars And Length From The Samples
    private static async Task<VoiceWave> DecodeAsync(byte[] audio)
    {
        using var source = new InMemoryRandomAccessStream();
        using var target = new InMemoryRandomAccessStream();
        await source.WriteAsync(audio.AsBuffer());
        source.Seek(0);

        MediaEncodingProfile profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
        profile.Audio                = AudioEncodingProperties.CreatePcm(SampleRate, 1, 16);

        var transcoder                   = new MediaTranscoder();
        PrepareTranscodeResult prepared = await transcoder.PrepareStreamTranscodeAsync(source, target, profile);
        if (!prepared.CanTranscode)
        {
            throw new InvalidDataException(prepared.FailureReason.ToString());
        }

        await prepared.TranscodeAsync();

        // The Whole WAV File, Header Included
        target.Seek(0);
        IBuffer wav = await target.ReadAsync(new Windows.Storage.Streams.Buffer((uint)target.Size), (uint)target.Size, InputStreamOptions.None);
        return VoiceWave.FromWav(wav.ToArray());
    }
}
