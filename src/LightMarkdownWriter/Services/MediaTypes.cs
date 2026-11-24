using System.IO;

namespace LightMarkdownWriter.Services;

public static class MediaTypes
{
    /// <summary>Content type for a local file referenced from markdown (images, media).</summary>
    public static string ForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".avif" => "image/avif",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        _ => "application/octet-stream",
    };
}
