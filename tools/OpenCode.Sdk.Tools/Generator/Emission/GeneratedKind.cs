namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>What a generated file holds, as far as its folder is concerned.</summary>
internal enum GeneratedKind
{
    /// <summary>A public model, enum, union interface, union carrier or hoisted interface.</summary>
    Model,

    /// <summary>A public model an operation sends as its body; it sits with the operation records.</summary>
    BodyRequest,

    /// <summary>A public operation record: a query request or a response envelope.</summary>
    OperationRecord,

    /// <summary>A public family client.</summary>
    Client,

    /// <summary>An internal-raw family client a hand-written door wraps.</summary>
    RawClient,

    /// <summary>The root client.</summary>
    RootClient,

    /// <summary>A family's part of the public route surface.</summary>
    Routes,

    /// <summary>The converter a union or structural union reads and writes through.</summary>
    ModelConverter,

    /// <summary>An operation's response adapter.</summary>
    ResponseAdapter,

    /// <summary>An operation's single-pass envelope deserialization DTO.</summary>
    EnvelopeDto,

    /// <summary>A streaming operation's adapter.</summary>
    StreamAdapter,

    /// <summary>The single serializer registry and the optional-value converters every slice shares.</summary>
    SharedSerialization,
}
