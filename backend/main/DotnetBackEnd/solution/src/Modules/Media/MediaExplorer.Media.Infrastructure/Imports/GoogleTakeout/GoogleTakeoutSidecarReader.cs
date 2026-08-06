using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaExplorer.Media.Infrastructure;

internal sealed record NormalizedImportMetadata(
    string? Title,
    DateTime? CapturedAt,
    string? Description,
    double? Latitude,
    double? Longitude,
    double? AltitudeMeters,
    IReadOnlyList<string> Warnings);

internal static class GoogleTakeoutSidecarReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public static async Task<NormalizedImportMetadata> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var sidecar = await JsonSerializer.DeserializeAsync<GoogleTakeoutSidecar>(stream, JsonOptions, cancellationToken)
            ?? throw new JsonException("The supplemental metadata document was empty.");
        var warnings = new List<string>();
        var capturedAt = ParseUnixSeconds(sidecar.PhotoTakenTime?.Timestamp);
        if (sidecar.PhotoTakenTime is not null && capturedAt is null) warnings.Add("taken_time_ignored");

        var description = sidecar.Description?.Trim();
        if (description?.Length > 4000)
        {
            description = description[..4000];
            warnings.Add("description_truncated");
        }
        if (description?.Length == 0) description = null;

        var (latitude, longitude, altitude) = NormalizeLocation(sidecar.GeoData, warnings);
        return new NormalizedImportMetadata(
            sidecar.Title?.Trim(),
            capturedAt,
            description,
            latitude,
            longitude,
            altitude,
            warnings);
    }

    private static DateTime? ParseUnixSeconds(string? value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return null;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static (double? Latitude, double? Longitude, double? Altitude) NormalizeLocation(
        GoogleGeoData? geoData,
        List<string> warnings)
    {
        if (geoData is null) return (null, null, null);
        if (!double.IsFinite(geoData.Latitude)
            || !double.IsFinite(geoData.Longitude)
            || geoData.Latitude is < -90 or > 90
            || geoData.Longitude is < -180 or > 180)
        {
            warnings.Add("location_ignored");
            return (null, null, null);
        }

        var isAllZeroSentinel = geoData.Latitude == 0
            && geoData.Longitude == 0
            && geoData.Altitude == 0
            && geoData.LatitudeSpan == 0
            && geoData.LongitudeSpan == 0;
        if (isAllZeroSentinel) return (null, null, null);

        double? altitude = double.IsFinite(geoData.Altitude) ? geoData.Altitude : null;
        if (!altitude.HasValue && geoData.Altitude != 0) warnings.Add("altitude_ignored");
        return (geoData.Latitude, geoData.Longitude, altitude);
    }

    private sealed class GoogleTakeoutSidecar
    {
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("photoTakenTime")]
        public GoogleTimestamp? PhotoTakenTime { get; init; }

        [JsonPropertyName("geoData")]
        public GoogleGeoData? GeoData { get; init; }
    }

    private sealed class GoogleTimestamp
    {
        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; init; }
    }

    private sealed class GoogleGeoData
    {
        [JsonPropertyName("latitude")]
        public double Latitude { get; init; }

        [JsonPropertyName("longitude")]
        public double Longitude { get; init; }

        [JsonPropertyName("altitude")]
        public double Altitude { get; init; }

        [JsonPropertyName("latitudeSpan")]
        public double LatitudeSpan { get; init; }

        [JsonPropertyName("longitudeSpan")]
        public double LongitudeSpan { get; init; }
    }
}
