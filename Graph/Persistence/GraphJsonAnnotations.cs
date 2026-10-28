namespace MASS4.Graph;

public static partial class GraphJson
{
    /// <summary>
    /// Reads a node's annotations, resolving renamed annotation types and signals. Values of unregistered types, or
    /// that do not match their type's kind, are preserved verbatim.
    /// </summary>
    static Dictionary<StableId, object>? ReadAnnotations(
        JsonElement element,
        ElementId node,
        GraphTypeRegistry registry,
        DiagnosticBag diagnostics)
    {
        if (!element.TryGetProperty("annotations", out var saved))
        {
            return null;
        }
        if (saved.ValueKind != JsonValueKind.Object)
        {
            throw new GraphFormatException("'annotations' must be an object.");
        }
        Dictionary<StableId, object> annotations = [];
        foreach (var property in saved.EnumerateObject())
        {
            if (!StableId.TryParse(property.Name, out var savedType))
            {
                throw new GraphFormatException($"Annotation key '{property.Name}' is not a valid identity.");
            }
            var type = registry.Annotations.Canonicalize(savedType);
            annotations[type] = registry.Annotations.TryResolve(type, out var annotation)
                ? ReadAnnotation(annotation, property.Value, node, registry, diagnostics)
                : new UnknownPayload(property.Value);
        }
        return annotations;
    }

    static object ReadAnnotation(
        AnnotationType annotation,
        JsonElement value,
        ElementId node,
        GraphTypeRegistry registry,
        DiagnosticBag diagnostics)
    {
        object? read = (annotation.Kind, value.ValueKind) switch
        {
            (AnnotationKind.Signal, JsonValueKind.String) when StableId.TryParse(value.GetString(), out var signal) =>
                registry.Signals.Canonicalize(signal),
            (AnnotationKind.Integer, JsonValueKind.Number) when value.TryGetInt32(out var integer) => integer,
            (AnnotationKind.Number, JsonValueKind.Number) => value.GetDouble(),
            (AnnotationKind.Boolean, JsonValueKind.True or JsonValueKind.False) => value.GetBoolean(),
            (AnnotationKind.Text, JsonValueKind.String) => value.GetString()!,
            _ => null,
        };
        if (read is not null)
        {
            return read;
        }
        diagnostics.Warning(GraphDiagnosticIds.PayloadUnreadable, node,
            $"Annotation '{annotation.DisplayName}' is not a {annotation.Kind} value; it is preserved unchanged.");
        return new UnknownPayload(value);
    }
}
