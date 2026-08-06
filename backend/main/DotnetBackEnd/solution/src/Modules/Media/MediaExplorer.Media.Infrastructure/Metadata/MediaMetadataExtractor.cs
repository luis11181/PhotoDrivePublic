using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Infrastructure;

internal sealed record ExtractedMediaMetadata(
    string DetectedMimeType,
    string? MediaFormat,
    int? Width,
    int? Height,
    int? Orientation,
    DateTime? ExifDateTimeOriginal,
    DateTime? ExifDateTimeDigitized,
    DateTime? EmbeddedCreatedAt,
    string RawMetadataJson);

internal interface IMediaMetadataExtractor
{
    Task<ExtractedMediaMetadata> ExtractAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Reads immutable byte metadata without consulting local filesystem timestamps or filename extensions.</summary>
internal sealed class ExternalMediaMetadataExtractor(IConfiguration configuration) : IMediaMetadataExtractor
{
    internal const string ExtractorVersion = "external-v1";
    private readonly string _imageIdentifyToolPath = configuration["MediaStorage:ImageIdentifyToolPath"] ?? "identify";
    private readonly string _videoToolPath = configuration["MediaStorage:VideoMetadataToolPath"] ?? "ffprobe";
    private readonly string _fileTypeToolPath = configuration["MediaStorage:FileTypeToolPath"] ?? "file";

    public async Task<ExtractedMediaMetadata> ExtractAsync(string path, CancellationToken cancellationToken)
    {
        var image = await TryExtractImageAsync(path, cancellationToken);
        if (image is not null) return image;

        var video = await TryExtractVideoAsync(path, cancellationToken);
        if (video is not null) return video;

        var detectedMimeType = await DetectGenericMimeTypeAsync(path, cancellationToken);
        return new ExtractedMediaMetadata(
            detectedMimeType,
            MediaFormat: null,
            Width: null,
            Height: null,
            Orientation: null,
            ExifDateTimeOriginal: null,
            ExifDateTimeDigitized: null,
            EmbeddedCreatedAt: null,
            JsonSerializer.Serialize(new { tool = "file", mimeType = detectedMimeType }));
    }

    private async Task<ExtractedMediaMetadata?> TryExtractImageAsync(string path, CancellationToken cancellationToken)
    {
        const string rawMarker = "RAW-EXIF-BEGIN";
        var format = $"FORMAT=%m\nWIDTH=%w\nHEIGHT=%h\nORIENTATION=%[EXIF:Orientation]\nORIENTATION_NAME=%[orientation]\n{rawMarker}\n%[EXIF:*]";
        var result = await RunAsync(_imageIdentifyToolPath, ["-quiet", "-format", format, $"{path}[0]"], cancellationToken);
        if (result.ExitCode != 0) return null;

        var markerIndex = result.StandardOutput.IndexOf(rawMarker, StringComparison.Ordinal);
        if (markerIndex < 0) return null;
        var header = ParseKeyValues(result.StandardOutput[..markerIndex]);
        var exif = ParseKeyValues(result.StandardOutput[(markerIndex + rawMarker.Length)..]);
        if (!header.TryGetValue("FORMAT", out var mediaFormat) || string.IsNullOrWhiteSpace(mediaFormat)) return null;

        var detectedMimeType = MimeForImageFormat(mediaFormat);
        if (detectedMimeType is null) return null;
        var orientation = ParseOrientation(header.GetValueOrDefault("ORIENTATION"), header.GetValueOrDefault("ORIENTATION_NAME"));
        var original = ParseExifDate(exif.GetValueOrDefault("exif:DateTimeOriginal"), exif.GetValueOrDefault("exif:OffsetTimeOriginal"));
        var digitized = ParseExifDate(exif.GetValueOrDefault("exif:DateTimeDigitized"), exif.GetValueOrDefault("exif:OffsetTimeDigitized"));
        var otherEmbedded = ParseExifDate(exif.GetValueOrDefault("exif:DateTime"), exif.GetValueOrDefault("exif:OffsetTime"));

        return new ExtractedMediaMetadata(
            detectedMimeType,
            mediaFormat.ToUpperInvariant(),
            ParsePositiveInt(header.GetValueOrDefault("WIDTH")),
            ParsePositiveInt(header.GetValueOrDefault("HEIGHT")),
            orientation,
            original,
            digitized,
            otherEmbedded,
            JsonSerializer.Serialize(new { tool = "imagemagick", format = mediaFormat, exif }));
    }

    private async Task<ExtractedMediaMetadata?> TryExtractVideoAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunAsync(_videoToolPath, ["-v", "error", "-show_format", "-show_streams", "-of", "json", path], cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput)) return null;

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var formatElement)) return null;
            var formatName = StringProperty(formatElement, "format_name");
            if (string.IsNullOrWhiteSpace(formatName)) return null;

            JsonElement? videoStream = null;
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (string.Equals(StringProperty(stream, "codec_type"), "video", StringComparison.OrdinalIgnoreCase))
                    {
                        videoStream = stream;
                        break;
                    }
                }
            }

            var majorBrand = TagValue(formatElement, "major_brand");
            var mimeType = MimeForVideoFormat(formatName, majorBrand);
            if (mimeType is null || videoStream is null) return null;
            var creationTime = FirstValidEmbeddedDate(
                TagValue(formatElement, "creation_time"),
                TagValue(formatElement, "com.apple.quicktime.creationdate"),
                TagValue(videoStream.Value, "creation_time"));

            return new ExtractedMediaMetadata(
                mimeType,
                formatName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()?.ToUpperInvariant(),
                IntProperty(videoStream.Value, "width"),
                IntProperty(videoStream.Value, "height"),
                Orientation: 1,
                ExifDateTimeOriginal: null,
                ExifDateTimeDigitized: null,
                EmbeddedCreatedAt: creationTime,
                RawMetadataJson: result.StandardOutput.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> DetectGenericMimeTypeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunAsync(_fileTypeToolPath, ["--brief", "--mime-type", "--", path], cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new InvalidOperationException("The file-type metadata tool could not identify the committed original.");
        }
        return result.StandardOutput.Trim().Split('\n', 2)[0].Trim();
    }

    private static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start metadata tool '{Path.GetFileName(executable)}'.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new InvalidOperationException($"Metadata tool '{Path.GetFileName(executable)}' exceeded its 60-second timeout.");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException($"Metadata tool '{Path.GetFileName(executable)}' is not available.", exception);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and kill request.
        }
    }

    private static Dictionary<string, string> ParseKeyValues(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return result;
    }

    internal static DateTime? ParseExifDate(string? value, string? offset)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var formats = new[] { "yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss" };
        if (!DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (!string.IsNullOrWhiteSpace(offset)
            && TimeSpan.TryParse(offset.Trim(), CultureInfo.InvariantCulture, out var parsedOffset)
            && parsedOffset >= TimeSpan.FromHours(-14)
            && parsedOffset <= TimeSpan.FromHours(14))
        {
            return new DateTimeOffset(local, parsedOffset).UtcDateTime;
        }
        return local;
    }

    internal static DateTime? ParseFilenameDate(string originalName)
    {
        var name = Path.GetFileNameWithoutExtension(originalName);
        var patterns = new (Regex Regex, string Format)[]
        {
            (new Regex(@"(?<!\d)(?<date>20\d{2}[01]\d[0-3]\d)[_\- ](?<time>[0-2]\d[0-5]\d[0-5]\d)(?!\d)", RegexOptions.CultureInvariant), "yyyyMMddHHmmss"),
            (new Regex(@"(?<!\d)(?<date>20\d{2}-[01]\d-[0-3]\d)[ _-](?<time>[0-2]\d[.:-][0-5]\d[.:-][0-5]\d)(?!\d)", RegexOptions.CultureInvariant), "yyyy-MM-ddHH.mm.ss"),
            (new Regex(@"(?<!\d)(?<date>20\d{2}[01]\d[0-3]\d)(?!\d)", RegexOptions.CultureInvariant), "yyyyMMdd")
        };

        foreach (var (regex, format) in patterns)
        {
            var match = regex.Match(name);
            if (!match.Success) continue;
            var candidate = match.Groups["date"].Value + match.Groups["time"].Value.Replace(':', '.').Replace('-', '.');
            if (DateTime.TryParseExact(candidate, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
            }
        }
        return null;
    }

    private static DateTime? FirstValidEmbeddedDate(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var withOffset)) return withOffset.UtcDateTime;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var withoutOffset)) return DateTime.SpecifyKind(withoutOffset, DateTimeKind.Unspecified);
        }
        return null;
    }

    private static string? MimeForImageFormat(string format) => format.ToUpperInvariant() switch
    {
        "JPEG" or "JPG" or "JPE" => "image/jpeg",
        "PNG" or "APNG" => "image/png",
        "GIF" => "image/gif",
        "WEBP" => "image/webp",
        "HEIC" or "HEIF" => "image/heic",
        "AVIF" => "image/avif",
        "TIFF" or "TIF" => "image/tiff",
        "BMP" => "image/bmp",
        "SVG" or "SVGZ" => "image/svg+xml",
        "ICO" => "image/x-icon",
        "JXL" => "image/jxl",
        "JP2" or "J2K" => "image/jp2",
        _ => null
    };

    private static string? MimeForVideoFormat(string formatName, string? majorBrand)
    {
        var formats = formatName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (formats.Contains("webm", StringComparer.OrdinalIgnoreCase)) return "video/webm";
        if (formats.Contains("matroska", StringComparer.OrdinalIgnoreCase)) return "video/x-matroska";
        if (formats.Contains("avi", StringComparer.OrdinalIgnoreCase)) return "video/x-msvideo";
        if (formats.Contains("mov", StringComparer.OrdinalIgnoreCase) && string.Equals(majorBrand?.Trim(), "qt", StringComparison.OrdinalIgnoreCase)) return "video/quicktime";
        if (formats.Any(format => format is "mov" or "mp4" or "m4a" or "3gp" or "3g2" or "mj2")) return "video/mp4";
        return null;
    }

    private static int? ParseOrientation(string? value, string? name)
    {
        if (int.TryParse(value, CultureInfo.InvariantCulture, out var orientation) && orientation is >= 1 and <= 8) return orientation;
        return name?.Trim().ToLowerInvariant() switch
        {
            "topleft" => 1,
            "topright" => 2,
            "bottomright" => 3,
            "bottomleft" => 4,
            "lefttop" => 5,
            "righttop" => 6,
            "rightbottom" => 7,
            "leftbottom" => 8,
            _ => 1
        };
    }

    private static int? ParsePositiveInt(string? value) => int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;
    private static string? StringProperty(JsonElement element, string name) => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static int? IntProperty(JsonElement element, string name) => element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) && value > 0 ? value : null;
    private static string? TagValue(JsonElement element, string name) => element.TryGetProperty("tags", out var tags) ? StringProperty(tags, name) : null;
    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
