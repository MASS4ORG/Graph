using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

[assembly: MASS4.Graph.GraphModule("MASS4.Graph.Samples.CustomNodes.ReviewModule")]

namespace MASS4.Graph.Samples.CustomNodes;

/// <summary>Text carried between review nodes.</summary>
/// <param name="Text">The note.</param>
public sealed record Note(string Text);

/// <summary>Payload of a sticky note; version 2 renamed <c>body</c> to <c>text</c>.</summary>
/// <param name="Text">The note text.</param>
/// <param name="Pinned">Whether the note stays on top.</param>
public sealed record StickyPayload(string Text, bool Pinned);

/// <summary>Notes as a port and variable type.</summary>
[GraphValue("7e710000-0000-4000-8000-000000000001", "note", typeof(Note), JsonContext = typeof(ReviewJson))]
public static partial class Notes;

/// <summary>A sticky note that publishes its text.</summary>
[GraphNode("7e710000-0000-4000-8000-000000000101", "Sticky note", Version = 2)]
[GraphPort("Text", "7e710000-0000-4000-8000-000000000201", "text", PortDirection.Output, typeof(Notes))]
[GraphPayload(typeof(StickyPayload), JsonContext = typeof(ReviewJson), DefaultFactory = nameof(Default))]
[GraphMigration(1, typeof(Sticky), nameof(RenameBody))]
[GraphRenamedFrom("7e710000-0000-4000-8000-000000000100")]
public static partial class Sticky
{
    static StickyPayload Default() => new("", false);

    /// <summary>Version 1 stored the text as <c>body</c>.</summary>
    internal static JsonObject RenameBody(JsonObject node)
    {
        if (node["payload"] is JsonObject payload && payload["body"] is { } body)
        {
            payload.Remove("body");
            payload["text"] = body;
        }
        return node;
    }
}

/// <summary>A checklist collecting any number of notes.</summary>
[GraphNode("7e710000-0000-4000-8000-000000000102", "Checklist")]
[GraphPort("Items", "7e710000-0000-4000-8000-000000000202", "items", PortDirection.Input, typeof(Notes), Multiplicity = PortMultiplicity.Multiple)]
public static partial class Checklist;

/// <summary>Raised when a reviewer signs off.</summary>
[GraphSignal("7e710000-0000-4000-8000-000000000301", "Reviewed")]
public static partial class Reviewed;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Note))]
[JsonSerializable(typeof(StickyPayload))]
partial class ReviewJson : JsonSerializerContext;
