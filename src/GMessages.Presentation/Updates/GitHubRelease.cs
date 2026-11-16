using System.Text.Json.Serialization;

namespace GMessages.Presentation.Updates;

/// <summary>
/// The parts of a GitHub release that the updater reads.
/// </summary>
/// <param name="TagName">The release tag, such as <c>v1.0.1</c>.</param>
/// <param name="HtmlUrl">The release page.</param>
/// <param name="Assets">The files of the release.</param>
/// <seealso href="https://docs.github.com/rest/releases/releases#get-the-latest-release"/>
public sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);

/// <summary>
/// A file of a GitHub release.
/// </summary>
/// <param name="Name">The file name, such as <c>Messages-1.0.1-x64.msi</c>.</param>
/// <param name="DownloadUrl">The download address.</param>
/// <param name="Digest">The checksum GitHub computed on upload, such as <c>sha256:ab12...</c>, else null.</param>
/// <param name="Size">The size in bytes.</param>
public sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string DownloadUrl,
    [property: JsonPropertyName("digest")] string? Digest,
    [property: JsonPropertyName("size")] long Size);

/// <summary>
/// The source-generated JSON reader of <see cref="GitHubRelease"/>, so Native AOT needs no reflection.
/// </summary>
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
