namespace KeyManagement.Domain.Custody;

/// <summary>
/// Thrown when something asks for a custody change the state machine does not allow.
/// </summary>
/// <remarks>
/// This is a bug, not a refused request. A holder who may not take an asset produces a
/// recorded denial and a normal response; only a caller trying to move an asset straight from
/// checked out to checkout pending, or to return one that was never taken, lands here.
/// </remarks>
public sealed class InvalidCustodyTransitionException : InvalidOperationException
{
    /// <summary>Creates an exception with no detail.</summary>
    public InvalidCustodyTransitionException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">Description of the rejected transition.</param>
    public InvalidCustodyTransitionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an underlying cause.</summary>
    /// <param name="message">Description of the rejected transition.</param>
    /// <param name="innerException">The cause.</param>
    public InvalidCustodyTransitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
