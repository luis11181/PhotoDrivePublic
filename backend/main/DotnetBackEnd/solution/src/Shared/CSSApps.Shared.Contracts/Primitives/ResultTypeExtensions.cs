namespace CSSApps.Shared.Contracts.Primitives;

/// <summary>
///     Extension methods for the Result type.
/// </summary>
public static class ResultExtensions
{
    // Public method to merge an array of results
    public static Result<IEnumerable<TSuccess>> Merge<TSuccess>(params Result<TSuccess>[] results)
    {
        return AggregateResults(results);
    }

    //// Extension method to merge this result with another
    //public static Result<TSuccess> MergeWith<TSuccess>(this Result<TSuccess> firstResult, Result<TSuccess> secondResult)
    //{
    //    return AggregateResults(new[] { firstResult, secondResult }).Map(results => results.First());
    //}

    // Private method handling the core merging logic
    private static Result<IEnumerable<TSuccess>> AggregateResults<TSuccess>(Result<TSuccess>[] results)
    {
        var errors = new List<ResultError>();
        var successes = new List<TSuccess>();

        foreach (var result in results)
            if (result.IsSuccess)
                successes.Add(result.SuccessValue);
            else
                errors.Add(result.ErrorValue);

        if (errors.Count == 0) return Result<IEnumerable<TSuccess>>.Success(successes);

        var aggregatedError = new CompositeResultError(errors.Select(e => e.Message));
        return Result<IEnumerable<TSuccess>>.Failure(aggregatedError);
    }

    // Composite error to handle multiple error messages
    public record CompositeResultError : ResultError
    {
        public CompositeResultError(IEnumerable<string> errorMessages)
            : base($"Composite Error: {string.Join(", ", errorMessages)}", "COMPOSITE_ERROR")
        {
            ErrorMessages = errorMessages;
        }

        public IEnumerable<string> ErrorMessages { get; }
    }
}