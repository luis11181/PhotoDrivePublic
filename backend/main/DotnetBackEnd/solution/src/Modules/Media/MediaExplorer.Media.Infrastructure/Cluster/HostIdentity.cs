using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>
/// Stable identity of this PhotoDrive host. The id is generated once and kept in a small file next to the host's
/// local cache (never shared between machines). The display name defaults to the machine name and can be set with
/// <c>Host:Name</c>, which containers should do because their machine name is random.
/// </summary>
public sealed class HostIdentity
{
    public HostIdentity(Guid id, string? name)
    {
        Id = id;
        var trimmed = string.IsNullOrWhiteSpace(name) ? "PhotoDrive host" : name.Trim();
        Name = trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }

    public Guid Id { get; }

    public string Name { get; }

    public static HostIdentity FromConfiguration(IConfiguration configuration)
    {
        var configuredName = configuration["Host:Name"];
        var name = string.IsNullOrWhiteSpace(configuredName) ? Environment.MachineName : configuredName;
        if (Guid.TryParse(configuration["Host:Id"], out var configuredId)) return new HostIdentity(configuredId, name);

        var staging = Path.GetFullPath(configuration["MediaStorage:LocalDisk:StagingDirectory"] ?? "media-explorer.staging");
        var directory = Path.GetDirectoryName(staging) ?? staging;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "photodrive-host-id");
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var storedId)) return new HostIdentity(storedId, name);

        var id = Guid.CreateVersion7();
        File.WriteAllText(path, id.ToString("D"));
        return new HostIdentity(id, name);
    }
}
