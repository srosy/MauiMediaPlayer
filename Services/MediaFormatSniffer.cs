using MauiMediaPlayer.Models;

namespace MauiMediaPlayer.Services;

public sealed record MediaSniffResult(MediaKind Kind, string Extension);

public static class MediaFormatSniffer
{
    private const int HeaderReadSize = 64;
    private const string LogPrefix = "FilenameRepair:";

    public static bool TrySniff(string filePath, DebugLogService? debugLog, out MediaSniffResult? result)
    {
        result = null;

        try
        {
            if (TrySniffMagicBytes(filePath, out result) && result is not null)
            {
                debugLog?.Debug($"{LogPrefix} sniff {Path.GetFileName(filePath)} → {result.Kind}/{result.Extension} (magic).");
                return true;
            }

            if (TrySniffWithTagLib(filePath, out result) && result is not null)
            {
                debugLog?.Debug($"{LogPrefix} sniff {Path.GetFileName(filePath)} → {result.Kind}/{result.Extension} (TagLib).");
                return true;
            }

            if (TrySniffWithImageSharp(filePath, out result) && result is not null)
            {
                debugLog?.Debug($"{LogPrefix} sniff {Path.GetFileName(filePath)} → {result.Kind}/{result.Extension} (ImageSharp).");
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            debugLog?.Trace($"{LogPrefix} sniff failed for {Path.GetFileName(filePath)}: {exception.Message}");
        }

        return false;
    }

    private static bool TrySniffMagicBytes(string filePath, out MediaSniffResult? result)
    {
        result = null;
        var header = ReadHeader(filePath);

        if (header.Length < 12)
        {
            return false;
        }

        if (header.Length >= 8 && Matches(header, 0, "RIFF") && Matches(header, 8, "WEBP"))
        {
            result = new MediaSniffResult(MediaKind.Image, ".webp");
            return true;
        }

        if (Matches(header, 0, "RIFF") && header.Length >= 12 && Matches(header, 8, "AVI "))
        {
            result = new MediaSniffResult(MediaKind.Video, ".avi");
            return true;
        }

        if (header.Length >= 8 && Matches(header, 4, "ftyp"))
        {
            result = new MediaSniffResult(MediaKind.Video, ".mp4");
            return true;
        }

        if (header.Length >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3)
        {
            result = new MediaSniffResult(MediaKind.Video, ".webm");
            return true;
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            result = new MediaSniffResult(MediaKind.Image, ".jpg");
            return true;
        }

        if (header.Length >= 4 && Matches(header, 0, "\x89PNG"))
        {
            result = new MediaSniffResult(MediaKind.Image, ".png");
            return true;
        }

        if (header.Length >= 6 && (Matches(header, 0, "GIF87a") || Matches(header, 0, "GIF89a")))
        {
            result = new MediaSniffResult(MediaKind.Gif, ".gif");
            return true;
        }

        return false;
    }

    private static bool TrySniffWithTagLib(string filePath, out MediaSniffResult? result)
    {
        result = null;

        try
        {
            using var file = TagLib.File.Create(filePath);

            if (file.Properties.Duration <= TimeSpan.Zero && file.Properties.BitsPerSample == 0)
            {
                return false;
            }

            var extension = SupportedMediaTypes.GetFinalExtension(filePath);
            result = SupportedMediaTypes.GetKind(filePath) == MediaKind.Video
                ? new MediaSniffResult(MediaKind.Video, extension)
                : new MediaSniffResult(MediaKind.Video, ".mp4");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TrySniffWithImageSharp(string filePath, out MediaSniffResult? result)
    {
        result = null;

        try
        {
            using var stream = File.OpenRead(filePath);
            var format = SixLabors.ImageSharp.Image.DetectFormat(stream);

            if (format is null)
            {
                return false;
            }

            var extension = format.FileExtensions.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            if (!extension.StartsWith('.'))
            {
                extension = "." + extension;
            }

            extension = extension.ToLowerInvariant();

            if (SupportedMediaTypes.GifExtensions.Contains(extension))
            {
                result = new MediaSniffResult(MediaKind.Gif, extension);
                return true;
            }

            if (SupportedMediaTypes.ImageExtensions.Contains(extension))
            {
                result = new MediaSniffResult(MediaKind.Image, extension);
                return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[] ReadHeader(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var buffer = new byte[HeaderReadSize];
        var read = stream.Read(buffer, 0, buffer.Length);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static bool Matches(byte[] buffer, int offset, string value)
    {
        if (offset + value.Length > buffer.Length)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (buffer[offset + index] != value[index])
            {
                return false;
            }
        }

        return true;
    }
}
