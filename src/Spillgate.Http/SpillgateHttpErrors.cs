using System;

namespace Spillgate.Http;

/// <summary>Finds and maps Spillgate failures that other layers wrapped (HttpClient, ASP.NET Core, model binding).</summary>
public static class SpillgateHttpErrors
{
    private const int MaxDepth = 16;

    /// <summary>Walks the inner exceptions (and aggregates) for a <see cref="SpillgateException"/>.</summary>
    /// <param name="exception">The exception that was caught.</param>
    /// <returns>The first Spillgate failure in the chain, or <see langword="null"/>.</returns>
    public static SpillgateException? Find(Exception? exception)
    {
        for (int depth = 0; exception is not null && depth < MaxDepth; depth++)
        {
            if (exception is SpillgateException s)
            {
                return s;
            }

            if (exception is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    SpillgateException? found = Find(inner);
                    if (found is not null)
                    {
                        return found;
                    }
                }

                return null;
            }

            exception = exception.InnerException;
        }

        return null;
    }

    /// <summary>
    /// The HTTP status a server should answer for a failure while decoding a request body: 413 for limits,
    /// 415 for an unsupported encoding, 400 for malformed or corrupt data, 500 for internal failures.
    /// </summary>
    /// <param name="exception">The exception that was caught.</param>
    /// <returns>The status code, or <see langword="null"/> if the exception holds no Spillgate failure.</returns>
    public static int? ToStatusCode(Exception? exception) => Find(exception)?.Kind.HttpStatus();

    /// <summary>Gets a value indicating whether the exception chain holds a cancellation (the client went away).</summary>
    /// <param name="exception">The exception that was caught.</param>
    /// <returns>True for a cancellation.</returns>
    internal static bool IsCancellation(Exception? exception)
    {
        for (int depth = 0; exception is not null && depth < MaxDepth; depth++)
        {
            if (exception is OperationCanceledException)
            {
                return true;
            }

            exception = exception.InnerException;
        }

        return false;
    }
}
