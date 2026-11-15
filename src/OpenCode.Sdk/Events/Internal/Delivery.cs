using System.Runtime.ExceptionServices;

namespace OpenCode.Sdk.Internal;

/// <summary>A selected item, end of enumeration, or failure, independent of channel completion.</summary>
internal sealed record Delivery<T>(T? Item, Exception? Error, ExceptionDispatchInfo? Dispatch = null) where T : class;
