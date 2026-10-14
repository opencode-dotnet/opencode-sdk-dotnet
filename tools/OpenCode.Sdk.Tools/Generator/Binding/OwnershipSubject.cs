namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// A type the ownership rule places: its declared name, and the concept an interface name stands for
/// (<c>IExampleEvent</c> stands for <c>ExampleEvent</c>). A pattern matches either.
/// </summary>
internal sealed record OwnershipSubject(string Name, string Concept)
{
    public bool Matches(string pattern) => NamePrefix.Matches(Name, pattern) || NamePrefix.Matches(Concept, pattern);
}
