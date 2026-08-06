namespace CSSApps.Shared.Contracts.APICommons;

/// <summary>
///     Represents a standardized response for a paged response.
/// </summary>
public interface IPagedQuery
{
    /// <summary>
    ///     Page number. If null then default is 1.
    /// </summary>
    int? PageNumber { get; }

    /// <summary>
    ///     Records number per page (page size).
    /// </summary>
    int? PageSize { get; }
}