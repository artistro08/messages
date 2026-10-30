using System.Collections.Frozen;
using GMessages.Protocol.Proto;

namespace GMessages.Protocol;

/// <summary>
/// LibGM's table between mime types and <see cref="MediaFormats"/>.
/// </summary>
/// <remarks>
/// Ports <c>MimeToMediaType</c> and <c>FormatToMediaType</c> from
/// <see href="https://github.com/mautrix/gmessages/blob/ba33f4875a491f5cb47db8a6baf42a8869f5f383/pkg/libgm/media.go">media.go</see>.
/// Go builds the reverse table by walking a map, so formats with two mime types (BMP, plain text) come out in random
/// order; here the full mime type always wins and BMP is <c>image/bmp</c>.
/// </remarks>
public static class MediaFormatsMap
{
    // Port Of MimeToMediaType (The Bare Prefixes Are The Fallbacks)
    private static readonly FrozenDictionary<string, MediaFormats> FromMimeTable = new Dictionary<string, MediaFormats>
    {
        ["image/jpeg"]     = MediaFormats.ImageJpeg,
        ["image/jpg"]      = MediaFormats.ImageJpg,
        ["image/png"]      = MediaFormats.ImagePng,
        ["image/gif"]      = MediaFormats.ImageGif,
        ["image/wbmp"]     = MediaFormats.ImageWbmp,
        ["image/bmp"]      = MediaFormats.ImageXMsBmp,
        ["image/x-ms-bmp"] = MediaFormats.ImageXMsBmp,

        ["video/mp4"]        = MediaFormats.VideoMp4,
        ["video/3gpp2"]      = MediaFormats.Video3G2,
        ["video/3gpp"]       = MediaFormats.Video3Gpp,
        ["video/webm"]       = MediaFormats.VideoWebm,
        ["video/x-matroska"] = MediaFormats.VideoMkv,

        ["audio/aac"]      = MediaFormats.AudioAac,
        ["audio/amr"]      = MediaFormats.AudioAmr,
        ["audio/mp3"]      = MediaFormats.AudioMp3,
        ["audio/mpeg"]     = MediaFormats.AudioMpeg,
        ["audio/mpg"]      = MediaFormats.AudioMpg,
        ["audio/mp4"]      = MediaFormats.AudioMp4,
        ["audio/mp4-latm"] = MediaFormats.AudioMp4Latm,
        ["audio/3gpp"]     = MediaFormats.Audio3Gpp,
        ["audio/ogg"]      = MediaFormats.AudioOgg,

        ["text/vcard"]                                                                = MediaFormats.TextVcard,
        ["application/pdf"]                                                           = MediaFormats.AppPdf,
        ["text/plain"]                                                                = MediaFormats.AppTxt,
        ["text/html"]                                                                 = MediaFormats.AppHtml,
        ["application/msword"]                                                        = MediaFormats.AppDoc,
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"]   = MediaFormats.AppDocx,
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = MediaFormats.AppPptx,
        ["application/vnd.ms-powerpoint"]                                             = MediaFormats.AppPpt,
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"]         = MediaFormats.AppXlsx,
        ["application/vnd.ms-excel"]                                                  = MediaFormats.AppXls,
        ["application/vnd.android.package-archive"]                                   = MediaFormats.AppApk,
        ["application/zip"]                                                           = MediaFormats.AppZip,
        ["application/java-archive"]                                                  = MediaFormats.AppJar,
        ["text/x-calendar"]                                                           = MediaFormats.CalTextVcalendar,
        ["text/calendar"]                                                             = MediaFormats.CalTextCalendar,

        ["image"]       = MediaFormats.ImageUnspecified,
        ["video"]       = MediaFormats.VideoUnspecified,
        ["audio"]       = MediaFormats.AudioUnspecified,
        ["application"] = MediaFormats.AppUnspecified,
        ["text"]        = MediaFormats.AppTxt,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // Port Of FormatToMediaType: Its Calendar Extras, Then The First Full Mime Type Of Each Format
    private static readonly FrozenDictionary<MediaFormats, string> ToMimeTable = BuildToMime();

    /// <summary>
    /// Gets the format to send for a mime type.
    /// </summary>
    /// <remarks>
    /// As in Go's <c>UploadMedia</c>: the exact mime type, else its part before the slash (<c>image</c>, <c>video</c>,
    /// <c>audio</c>, <c>application</c>, <c>text</c>), else <see cref="MediaFormats.UnspecifiedType"/>. Case-sensitive, like Go.
    /// </remarks>
    /// <param name="mime">The mime type, such as <c>image/png</c>.</param>
    /// <returns>The format.</returns>
    public static MediaFormats FromMime(string mime)
    {
        ArgumentNullException.ThrowIfNull(mime);

        if (FromMimeTable.TryGetValue(mime, out MediaFormats format))
        {
            return format;
        }

        return FromMimeTable.GetValueOrDefault(mime.Split('/')[0], MediaFormats.UnspecifiedType);
    }

    /// <summary>
    /// Gets the mime type of a received format.
    /// </summary>
    /// <param name="format">The format from a message's <c>MediaContent</c>.</param>
    /// <returns>The mime type, or null when the format has no full mime type (the unspecified ones).</returns>
    public static string? ToMime(MediaFormats format) => ToMimeTable.GetValueOrDefault(format);

    private static FrozenDictionary<MediaFormats, string> BuildToMime()
    {
        var table = new Dictionary<MediaFormats, string>
        {
            [MediaFormats.CalTextXvcalendar] = "text/x-calendar",
            [MediaFormats.CalApplicationVcs] = "text/x-calendar",
            [MediaFormats.CalApplicationIcs] = "text/calendar",
        };

        foreach ((string mime, MediaFormats format) in FromMimeTable.Where(e => e.Key.Contains('/', StringComparison.Ordinal)).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            table.TryAdd(format, mime);
        }

        return table.ToFrozenDictionary();
    }
}
