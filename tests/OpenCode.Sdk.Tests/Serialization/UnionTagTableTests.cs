using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Serialization;

public sealed class UnionTagTableTests
{
    private static readonly UnionTagTable<int> Table = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["use"] = 1,
        ["user"] = 2,
        ["usar"] = 3,
        ["assistant"] = 4,
        ["été"] = 5,
    }.ToUnionTagTable();

    [Test]
    [Arguments("use", 1)]
    [Arguments("user", 2)]
    [Arguments("usar", 3)]
    [Arguments("assistant", 4)]
    [Arguments("été", 5)]
    public async Task TryGetValue_Should_Find_Each_Key_By_Its_Bytes_And_Its_String(string key, int expected)
    {
        var foundByBytes = Table.TryGetValue(Encoding.UTF8.GetBytes(key), out var byBytes);
        var foundByString = Table.TryGetValue(key, out var byString);

        await Assert.That(foundByBytes && foundByString).IsTrue();
        await Assert.That((byBytes, byString)).IsEqualTo((expected, expected));
    }

    [Test]
    [Arguments("us")]
    [Arguments("users")]
    [Arguments("ser")]
    [Arguments("User")]
    [Arguments("usor")]
    [Arguments("assistanT")]
    [Arguments("étè")]
    [Arguments("")]
    public async Task TryGetValue_Should_Miss_A_Near_Key(string key)
    {
        await Assert.That(Table.TryGetValue(Encoding.UTF8.GetBytes(key), out _)).IsFalse();
    }

    [Test]
    [Arguments("", "user")]
    [Arguments(" \t", "user")]
    [Arguments("user", "user")]
    [Arguments("\ud800", "\udbff")]
    public async Task Constructor_Should_Refuse_A_Blank_Or_Twice_Encoded_Tag(string first, string second)
    {
        KeyValuePair<string, int>[] entries = [new(first, 1), new(second, 2)];

        _ = await Assert.That(() => entries.ToUnionTagTable()).Throws<ArgumentException>();
    }

    [Test]
    public async Task Every_Generated_Table_Should_Find_Each_Variant_By_Its_Tag()
    {
        var assembly = typeof(IOpenCodeError).Assembly;
        var tables = GeneratedTables(assembly).ToList();
        var misses = new List<string>();
        foreach (var (converter, table) in tables)
        {
            var union = converter.BaseType?.GetGenericArguments().SingleOrDefault();
            var variants = union is null
                ? []
                : assembly.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false } && union.IsAssignableFrom(type) && !IsUnknownCarrier(type));
            var tagged = variants.Select(static variant => (Variant: variant, Tags: ConstantTags(variant))).Where(static pair => pair.Tags.Count > 0).ToList();
            misses.AddRange(tagged.Where(pair => !pair.Tags.Any(tag => Finds(table, tag, pair.Variant, union))).Select(pair => $"{converter.Name}: {pair.Variant.Name}"));
            if (tagged.Count is 0)
            {
                misses.Add($"{converter.Name}: no variant with a constant tag");
            }
        }

        await Assert.That(tables).IsNotEmpty();
        await Assert.That(misses).IsEmpty();
    }

    private static IEnumerable<(Type Converter, object Table)> GeneratedTables(Assembly assembly) =>
        from type in assembly.GetTypes()
        let field = type.GetField("ArmsByTag", BindingFlags.NonPublic | BindingFlags.Static)
        where field is { FieldType.IsGenericType: true } && field.FieldType.GetGenericTypeDefinition() == typeof(UnionTagTable<>)
        let table = field.GetValue(null)
        where table is not null
        select (type, table);

    /// <summary>An unknown carrier is the table's miss arm: it is built from the marker and the raw payload.</summary>
    private static bool IsUnknownCarrier(Type variant) => variant.GetConstructor([typeof(string), typeof(JsonElement)]) is not null;

    /// <summary>The tags a variant always writes: get-only wire properties that return a value on an empty instance.</summary>
    private static List<string> ConstantTags(Type variant)
    {
#if NET
        var empty = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(variant);
#else
        var empty = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(variant);
#endif
        return
        [
            .. variant.GetProperties()
                .Where(static property => property is { CanWrite: false, PropertyType: var type } && type == typeof(string)
                                          && property.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
                .Select(property => property.GetValue(empty) as string)
                .OfType<string>(),
        ];
    }

    private static bool Finds(object table, string tag, Type variant, Type? union)
    {
        object?[] arguments = [tag, null];
        var lookup = table.GetType().GetMethod("TryGetValue", [typeof(string), table.GetType().GetGenericArguments()[0].MakeByRefType()]);
        // A nested union's outer tag maps to the inner union's interface, never to the union's own.
        return lookup?.Invoke(table, arguments) is true
               && arguments[1]?.GetType().GetProperty("Type")?.GetValue(arguments[1]) is Type mapped
               && mapped != union
               && mapped.IsAssignableFrom(variant);
    }
}
