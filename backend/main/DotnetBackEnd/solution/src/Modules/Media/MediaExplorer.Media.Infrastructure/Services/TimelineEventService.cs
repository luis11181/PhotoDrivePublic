using System.Data;
using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class TimelineEventService(
    MediaCatalogDbContext dbContext,
    LibraryBootstrapper libraryBootstrapper,
    ILogger<TimelineEventService> logger) : ITimelineEventService
{
    public async Task<Result<TimelineEventsRes>> ListAsync(
        Guid userId,
        string? query,
        int limit,
        CancellationToken cancellationToken)
    {
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        List<TimelineEvent> events;
        if (string.IsNullOrWhiteSpace(query))
        {
            events = await dbContext.TimelineEvents.AsNoTracking()
                .Where(item => item.LibraryId == library.Id)
                .OrderByDescending(item => item.StartDate)
                .ThenByDescending(item => item.Id)
                .Take(Math.Clamp(limit, 1, 500))
                .ToListAsync(cancellationToken);
        }
        else
        {
            var searchTerms = SearchText.Terms(query);
            if (searchTerms is null)
                return MediaErrors.Validation("Enter at least two letters or numbers to search timeline events.");
            events = await SearchAsync(library.Id, searchTerms, Math.Clamp(limit, 1, 50), cancellationToken);
        }

        return new TimelineEventsRes { Events = events.Select(ToResponse).ToList() };
    }

    public async Task<Result<TimelineEventRes>> GetAsync(Guid userId, Guid eventId, CancellationToken cancellationToken)
    {
        var item = await OwnedAsync(userId, eventId, tracking: false, cancellationToken);
        return item is null
            ? MediaErrors.NotFound("The timeline event was not found in your library.")
            : ToResponse(item);
    }

    public async Task<Result<TimelineEventRes>> CreateAsync(
        Guid userId,
        CreateTimelineEventReq request,
        CancellationToken cancellationToken)
    {
        var values = Validate(request.Title, request.StartDate, request.EndDate, request.Description);
        if (values.IsFailure) return values.ErrorValue;
        var library = await libraryBootstrapper.GetOrCreateAsync(userId, cancellationToken);
        var now = DateTime.UtcNow;
        var item = new TimelineEvent
        {
            LibraryId = library.Id,
            Title = values.SuccessValue.Title,
            StartDate = values.SuccessValue.StartDate,
            EndDate = values.SuccessValue.EndDate,
            Description = values.SuccessValue.Description,
            CreatedByUserId = userId,
            CreatedAt = now,
            ModifiedAt = now
        };
        dbContext.TimelineEvents.Add(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[media.timeline-events] event created eventId={EventId} libraryId={LibraryId}", item.Id, item.LibraryId);
        return ToResponse(item);
    }

    public async Task<Result<TimelineEventRes>> UpdateAsync(
        Guid userId,
        Guid eventId,
        UpdateTimelineEventReq request,
        CancellationToken cancellationToken)
    {
        var values = Validate(request.Title, request.StartDate, request.EndDate, request.Description);
        if (values.IsFailure) return values.ErrorValue;
        var item = await OwnedAsync(userId, eventId, tracking: true, cancellationToken);
        if (item is null) return MediaErrors.NotFound("The timeline event was not found in your library.");
        item.Title = values.SuccessValue.Title;
        item.StartDate = values.SuccessValue.StartDate;
        item.EndDate = values.SuccessValue.EndDate;
        item.Description = values.SuccessValue.Description;
        item.ModifiedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[media.timeline-events] event updated eventId={EventId}", item.Id);
        return ToResponse(item);
    }

    public async Task<Result> DeleteAsync(Guid userId, Guid eventId, CancellationToken cancellationToken)
    {
        var item = await OwnedAsync(userId, eventId, tracking: true, cancellationToken);
        if (item is null) return MediaErrors.NotFound("The timeline event was not found in your library.");
        dbContext.TimelineEvents.Remove(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[media.timeline-events] event deleted eventId={EventId}", item.Id);
        return Result.Success();
    }

    private async Task<TimelineEvent?> OwnedAsync(
        Guid userId,
        Guid eventId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = tracking ? dbContext.TimelineEvents : dbContext.TimelineEvents.AsNoTracking();
        return await query.SingleOrDefaultAsync(item => item.Id == eventId
            && dbContext.Libraries.Any(library => library.Id == item.LibraryId && library.OwnerUserId == userId), cancellationToken);
    }

    private async Task<List<TimelineEvent>> SearchAsync(
        Guid libraryId,
        IReadOnlyList<string> terms,
        int limit,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            // Every term must match the title or description; events matching entirely in the title rank first.
            var match = SearchText.MatchAll(command, terms, "q", "search.title", "search.description");
            var titleMatch = SearchText.MatchAll(command, terms, "t", "search.title");
            command.CommandText = $"""
                SELECT item.Id
                FROM timeline_event_search search
                JOIN timeline_events item ON item.Id = search.event_id COLLATE NOCASE
                WHERE ({match})
                  AND search.library_id = $libraryId COLLATE NOCASE
                ORDER BY CASE WHEN {titleMatch} THEN 0 ELSE 1 END, item.StartDate DESC, item.Id
                LIMIT $limit;
                """;
            AddParameter(command, "$libraryId", libraryId.ToDbText());
            AddParameter(command, "$limit", limit);
            var ids = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (Guid.TryParse(reader.GetString(0), out var id)) ids.Add(id);
            if (ids.Count == 0) return [];
            var byId = await dbContext.TimelineEvents.AsNoTracking()
                .Where(item => ids.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken);
            return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    private static Result<EventValues> Validate(string? title, DateOnly startDate, DateOnly? endDate, string? description)
    {
        var normalizedTitle = title?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle)) return MediaErrors.Validation("Event title is required.");
        if (normalizedTitle.Length > 200) return MediaErrors.Validation("Event title must be 200 characters or fewer.");
        if (startDate == default) return MediaErrors.Validation("Event start date is required.");
        if (endDate.HasValue && endDate.Value < startDate) return MediaErrors.Validation("Event end date cannot be before its start date.");
        var normalizedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (normalizedDescription?.Length > 2000) return MediaErrors.Validation("Event description must be 2000 characters or fewer.");
        return new EventValues(normalizedTitle, startDate, endDate, normalizedDescription);
    }


    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static TimelineEventRes ToResponse(TimelineEvent item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        StartDate = item.StartDate,
        EndDate = item.EndDate,
        Description = item.Description,
        CreatedAt = item.CreatedAt,
        ModifiedAt = item.ModifiedAt
    };

    private sealed record EventValues(string Title, DateOnly StartDate, DateOnly? EndDate, string? Description);
}
