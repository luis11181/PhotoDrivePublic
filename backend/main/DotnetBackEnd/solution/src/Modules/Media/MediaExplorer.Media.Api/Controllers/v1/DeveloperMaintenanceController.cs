using System.Security.Claims;
using Asp.Versioning;
using CSSApps.Shared.Contracts.APICommons;
using CSSApps.Shared.Utils.ApiHelpers.Filters;
using MediaExplorer.Media.Api.Http;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MediaExplorer.Media.Api.Controllers.v1;

/// <summary>
/// Owner-only storage audit, verified backup, and selected inconsistency-cleanup actions.
/// </summary>
/// <remarks>
/// The storage audit and selected cleanup actions have no scheduled counterpart. The recurring scheduler runs only the same verified backup pipeline exposed here.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/developer/maintenance")]
[Authorize(Policy = "Owner")]
public sealed class DeveloperMaintenanceController(IMediaMaintenanceService maintenanceService) : ControllerBase
{
    /// <summary>Returns the privacy-safe timestamp, age, and freshness of the latest remotely verified backup.</summary>
    [HttpGet("backup-status")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<DeveloperBackupStatusRes>))]
    public async Task<IActionResult> BackupStatus(CancellationToken cancellationToken)
    {
        var result = await maintenanceService.GetBackupStatusAsync(cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Returns privacy-safe recovery, queue, worker, disk, and cache-pressure alerts.</summary>
    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<DeveloperOperationalHealthRes>))]
    public async Task<IActionResult> OperationalHealth(CancellationToken cancellationToken)
    {
        var result = await maintenanceService.GetOperationalHealthAsync(cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Runs an explicit storage audit without deleting originals or creating a catalog backup.</summary>
    /// <remarks>
    /// This CSRF-protected owner action checks catalog references, inventories versioned original/recovery-manifest storage, refreshes opaque inconsistency candidates, removes only completed-upload staging remnants, and evicts the rebuildable original cache only to its configured budget. It never deletes S3 originals or manifests, never deletes catalog data, and never creates a backup.
    /// </remarks>
    [HttpPost("audit")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<DeveloperMaintenanceReportRes>))]
    public async Task<IActionResult> Audit(CancellationToken cancellationToken)
    {
        var result = await maintenanceService.AuditAsync(cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Creates, verifies, and remotely verifies one database backup set without scanning original storage.</summary>
    /// <remarks>
    /// This CSRF-protected owner action runs the same steps as the weekly scheduled backup: a consistent snapshot of the whole database (catalog and accounts) converted to a plain SQLite file, integrity/foreign-key/coverage/user-reference verification, upload to the dedicated backup bucket, and remote checksum/length verification. It never inventories or deletes original S3 objects, manifests, catalog items, or caches.
    /// </remarks>
    [HttpPost("backup")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<DeveloperBackupReportRes>))]
    public async Task<IActionResult> Backup(CancellationToken cancellationToken)
    {
        var result = await maintenanceService.BackupAsync(cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Permanently cleans explicitly selected storage inconsistencies from the latest developer-maintenance report.</summary>
    /// <remarks>
    /// This CSRF-protected owner action first creates and remotely verifies a fresh safety backup, then rechecks every selected candidate and permanently deletes only selected exact original/manifest keys that remain unreferenced. It does not clear Trash or delete unselected candidates.
    /// </remarks>
    [HttpPost("cleanup")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<CleanupStorageInconsistenciesRes>))]
    public async Task<IActionResult> Cleanup([FromBody] CleanupStorageInconsistenciesReq request, CancellationToken cancellationToken)
    {
        var result = await maintenanceService.CleanupAsync(request, cancellationToken);
        return this.ToApiResult(result);
    }

    /// <summary>Lists the PhotoDrive hosts that share this installation's database and which one is primary.</summary>
    /// <remarks>
    /// The primary host runs installation-wide background work: metadata extraction and the scheduled weekly database backup.
    /// Imports and cache builds always run on the host that started them, because their files are local to that machine.
    /// </remarks>
    [HttpGet("hosts")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<ClusterHostsRes>))]
    public async Task<IActionResult> Hosts(CancellationToken cancellationToken) =>
        this.ToApiResult(await maintenanceService.GetHostsAsync(cancellationToken));

    /// <summary>Makes another known host the primary host, for example before turning the current primary off.</summary>
    /// <remarks>The previous primary stops starting new installation-wide work within about 30 seconds.</remarks>
    [HttpPut("primary-host")]
    [RequireAntiforgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiRes<ClusterHostsRes>))]
    public async Task<IActionResult> SetPrimaryHost([FromBody] SetPrimaryHostReq request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized(ApiRes.Unauthorized());
        return this.ToApiResult(await maintenanceService.SetPrimaryHostAsync(userId, request, cancellationToken));
    }
}
