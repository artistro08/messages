using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using GMessages.Presentation.Messaging;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace GMessages.App.Media;

/// <summary>
/// Reads the waveform and the length of a voice message from its audio file, once for each downloaded file.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="MediaTranscoder"/> of Windows decodes the M4A/AAC file to 8 kHz mono 16-bit WAV in memory, faster
/// than real time. An AudioGraph file input runs only at playback speed. Then <see cref="VoiceWave.FromWav"/> takes
/// about 48 peak bars and the length from the samples. Nothing touches the disk.
/// </para>
/// <para>
/// The reader keeps the result in memory only, keyed to the decrypted bytes that the media store returns. When the
/// store releases the bytes at its memory cap or at sign-out, the table releases the waveform too. The log never gets
/// the bytes.
/// </para>
/// </remarks>
internal static class WaveformReader
{
    // Phone-call quality in 1 channel is enough to draw the bars, and it keeps the WAV small.
    private const uint SampleRate = 8000;

    // The waveforms that the reader has read or is reading, keyed by the bytes that they come from.
    private static readonly ConditionalWeakTable<byte[], Task<VoiceWave>> Read = [];

    /// <summary>
    /// Gets the waveform and the length of an audio file.
    /// </summary>
    /// <param name="audio">The bytes of the audio file, such as an M4A voice message.</param>
    /// <returns>The waveform and the length.</returns>
    /// <exception cref="InvalidDataException">Windows cannot decode the file.</exception>
    public static Task<VoiceWave> ReadAsync(byte[] audio)
    {
        ArgumentNullException.ThrowIfNull(audio);

        return Read.GetValue(audio, static a => DecodeAsync(a));
    }

    private static async Task<VoiceWave> DecodeAsync(byte[] audio)
    {
        using var source = new InMemoryRandomAccessStream();
        using var target = new InMemoryRandomAccessStream();
        await source.WriteAsync(audio.AsBuffer());
        source.Seek(0);

        MediaEncodingProfile profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
        profile.Audio                = AudioEncodingProperties.CreatePcm(SampleRate, 1, 16);

        var transcoder                  = new MediaTranscoder();
        PrepareTranscodeResult prepared = await transcoder.PrepareStreamTranscodeAsync(source, target, profile);
        if (!prepared.CanTranscode)
        {
            throw new InvalidDataException(prepared.FailureReason.ToString());
        }

        await prepared.TranscodeAsync();

        // The buffer holds the whole WAV file, with its header.
        target.Seek(0);
        IBuffer wav = await target.ReadAsync(new Windows.Storage.Streams.Buffer((uint)target.Size), (uint)target.Size, InputStreamOptions.None);
        return VoiceWave.FromWav(wav.ToArray());
    }
}
