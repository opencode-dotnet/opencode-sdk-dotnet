namespace OpenCode.Sdk;

/// <summary>
/// Carries the three states a request-body member can be in: absent, explicitly null, or set to a
/// value. <c>default</c> is absent and the member is not written at all; a constructed instance is
/// set, and a set instance whose value is null writes an explicit JSON null. The server decides
/// what an explicit null means — for the preferences patch it deletes the key, while most
/// operations treat it the same as omission.
/// </summary>
/// <typeparam name="T">The carried value type, always the nullable type the property would
/// otherwise declare.</typeparam>
/// <remarks>
/// This is a value type rather than a record because <c>default</c> is what makes a member absent:
/// the serializer omits a property whose value equals <c>default</c>, and only a struct gives that
/// state for free in an object initializer nobody wrote a member for.
/// </remarks>
public readonly struct Optional<T> : IEquatable<Optional<T>>
{
    private readonly T _value;
    private readonly bool _isSet;

    /// <summary>Initializes a set instance.</summary>
    /// <param name="value">The carried value; null means an explicit JSON null.</param>
    public Optional(T value)
    {
        _value = value;
        _isSet = true;
    }

    /// <summary>Gets a set instance whose value is null, which writes an explicit JSON null.</summary>
    public static Optional<T> Null => new(default!);

    /// <summary>Gets a value indicating whether the member was assigned, including an assignment of null.</summary>
    public bool IsSet => _isSet;

    /// <summary>Gets the carried value, which is <c>default</c> when the member is absent.</summary>
    public T Value => _value;

    /// <summary>Wraps a value so an ordinary object initializer assigns the member directly.</summary>
    /// <param name="value">The carried value; null means an explicit JSON null.</param>
    public static implicit operator Optional<T>(T value) => new(value);

    /// <summary>Compares two instances for equality.</summary>
    /// <param name="left">The left instance.</param>
    /// <param name="right">The right instance.</param>
    /// <returns>Whether both instances carry the same state and value.</returns>
    public static bool operator ==(Optional<T> left, Optional<T> right) => left.Equals(right);

    /// <summary>Compares two instances for inequality.</summary>
    /// <param name="left">The left instance.</param>
    /// <param name="right">The right instance.</param>
    /// <returns>Whether the two instances differ in state or value.</returns>
    public static bool operator !=(Optional<T> left, Optional<T> right) => !left.Equals(right);

    /// <summary>Compares this instance with another for equality.</summary>
    /// <param name="other">The other instance.</param>
    /// <returns>Whether both instances carry the same state and value.</returns>
    public bool Equals(Optional<T> other) =>
        _isSet == other._isSet && EqualityComparer<T>.Default.Equals(_value, other._value);

    /// <summary>Compares this instance with another object for equality.</summary>
    /// <param name="obj">The other object.</param>
    /// <returns>Whether the object is an instance carrying the same state and value.</returns>
    public override bool Equals(object? obj) => obj is Optional<T> other && Equals(other);

    /// <summary>Computes the hash code for this instance.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() =>
        unchecked(((_isSet ? 1 : 0) * 397) ^ (_value is null ? 0 : EqualityComparer<T>.Default.GetHashCode(_value)));
}
