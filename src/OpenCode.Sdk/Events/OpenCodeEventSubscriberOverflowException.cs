using System.Diagnostics;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>The subscription could not accept another selected event; its unread backlog was discarded.</summary>
public sealed class OpenCodeEventSubscriberOverflowException : OpenCodeException
{
    /// <summary>Initializes an exception without a custom message.</summary>
    public OpenCodeEventSubscriberOverflowException()
    {
    }

    /// <summary>Initializes an exception with a custom message.</summary>
    /// <param name="message">The failure description.</param>
    public OpenCodeEventSubscriberOverflowException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes an exception with a custom message and underlying failure.</summary>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure.</param>
    public OpenCodeEventSubscriberOverflowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal OpenCodeEventSubscriberOverflowException(int capacity)
        : base("Event subscriber exceeded its 4096-event capacity") =>
        Debug.Assert(capacity == EventFeedBounds.SubscriberCapacity, "The SDK has one fixed subscriber capacity.");

    /// <summary>Gets the SDK's fixed 4,096-event subscriber bound, including on consumer-constructed exceptions.</summary>
    public int Capacity { get; } = EventFeedBounds.SubscriberCapacity;
}
