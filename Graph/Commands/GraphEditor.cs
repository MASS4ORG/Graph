namespace MASS4.Graph;

/// <summary>Outcome of executing, undoing or redoing a command.</summary>
/// <param name="Succeeded">Whether the document changed.</param>
/// <param name="Diagnostics">Errors explaining a rollback, or warnings accepted with the change.</param>
public sealed record CommandResult(bool Succeeded, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// The single mutation entry point of a document: it applies commands, validates the changes they made, rolls back
/// any command that produces errors, and keeps undo and redo history.
/// </summary>
public sealed class GraphEditor
{
    static readonly CommandResult NothingToDo = new(false, []);

    readonly List<IGraphCommand> undo = [];
    readonly List<IGraphCommand> redo = [];

    /// <summary>Creates an editor for a document.</summary>
    /// <param name="document">The document to mutate.</param>
    /// <param name="compatibility">Port compatibility strategy; exact matching when null.</param>
    /// <param name="additionalRules">Rules run after the built-in type and compatibility rules.</param>
    public GraphEditor(
        GraphDocument document,
        IPortCompatibility? compatibility = null,
        IEnumerable<IGraphRule>? additionalRules = null)
    {
        Document = document;
        Compatibility = compatibility ?? ExactPortCompatibility.Instance;
        Rules = [KnownTypesRule.Instance, new PortCompatibilityRule(Compatibility), .. additionalRules ?? []];
    }

    /// <summary>The document being edited.</summary>
    public GraphDocument Document { get; }

    /// <summary>The port compatibility strategy.</summary>
    public IPortCompatibility Compatibility { get; }

    /// <summary>Rules every command's changes must pass.</summary>
    public IReadOnlyList<IGraphRule> Rules { get; }

    /// <summary>Maximum number of undo steps kept; older steps are discarded.</summary>
    public int UndoLimit { get; set; } = 500;

    /// <summary>Whether <see cref="Undo"/> has a step to revert.</summary>
    public bool CanUndo => undo.Count > 0;

    /// <summary>Whether <see cref="Redo"/> has a step to reapply.</summary>
    public bool CanRedo => redo.Count > 0;

    /// <summary>The step <see cref="Undo"/> would revert, for menu labels.</summary>
    public IGraphCommand? NextUndo => undo.Count > 0 ? undo[^1] : null;

    /// <summary>The step <see cref="Redo"/> would reapply, for menu labels.</summary>
    public IGraphCommand? NextRedo => redo.Count > 0 ? redo[^1] : null;

    /// <summary>Applies and validates a command, rolling it back when it fails or produces errors.</summary>
    public CommandResult Execute(IGraphCommand command)
    {
        var diagnostics = new DiagnosticBag();
        Document.BeginRecording();
        if (!command.Apply(Document, diagnostics))
        {
            Document.EndRecording();
            return new(false, [.. diagnostics]);
        }
        var changes = Document.EndRecording();
        GraphValidator.Run(Document, changes, Rules, diagnostics);
        if (diagnostics.HasErrors)
        {
            command.Revert(Document);
            return new(false, [.. diagnostics]);
        }
        undo.Add(command);
        if (undo.Count > UndoLimit)
        {
            undo.RemoveRange(0, undo.Count - UndoLimit);
        }
        redo.Clear();
        Document.RaiseChanged(changes);
        return new(true, [.. diagnostics]);
    }

    /// <summary>Reverts the most recent step.</summary>
    public CommandResult Undo()
    {
        if (undo.Count == 0)
        {
            return NothingToDo;
        }
        var command = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        Document.BeginRecording();
        command.Revert(Document);
        var changes = Document.EndRecording();
        redo.Add(command);
        Document.RaiseChanged(changes);
        return new(true, []);
    }

    /// <summary>Reapplies the most recently undone step.</summary>
    public CommandResult Redo()
    {
        if (redo.Count == 0)
        {
            return NothingToDo;
        }
        var command = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        var diagnostics = new DiagnosticBag();
        Document.BeginRecording();
        if (!command.Apply(Document, diagnostics))
        {
            Document.EndRecording();
            redo.Clear();
            return new(false, [.. diagnostics]);
        }
        var changes = Document.EndRecording();
        undo.Add(command);
        Document.RaiseChanged(changes);
        return new(true, [.. diagnostics]);
    }

    /// <summary>Discards undo and redo history, for example after loading or saving a document.</summary>
    public void ClearHistory()
    {
        undo.Clear();
        redo.Clear();
    }

    /// <summary>
    /// Builds the command connecting two ports: a plain edge, or, when the strategy requires conversion, a conversion
    /// node placed between the ports and wired to both.
    /// </summary>
    public IGraphCommand CreateConnection(ElementId source, ElementId target) =>
        Document.TryGetPort(source, out var from) && Document.TryGetPort(target, out var to) &&
        RequiredConversion(from, to) is { } conversion
            ? ConnectThrough(conversion, from, to)
            : new ConnectCommand(source, target);

    /// <summary>Connects two ports, inserting a conversion node when the strategy requires one.</summary>
    public CommandResult Connect(ElementId source, ElementId target) => Execute(CreateConnection(source, target));

    NodeType? RequiredConversion(GraphPort from, GraphPort to)
    {
        var types = Document.Registry.PortTypes;
        if (!types.TryResolve(from.TypeId, out var fromType) || !types.TryResolve(to.TypeId, out var toType))
        {
            return null;
        }
        var result = Compatibility.Check(fromType, toType);
        return result.Kind == PortCompatibilityKind.RequiresConversion ? result.Conversion : null;
    }

    CompositeCommand ConnectThrough(NodeType conversion, GraphPort from, GraphPort to)
    {
        var add = new AddNodeCommand(conversion);
        var input = conversion.Ports.First(static port => port.Direction == PortDirection.Input);
        var output = conversion.Ports.First(static port => port.Direction == PortDirection.Output);
        List<IGraphCommand> steps =
        [
            add,
            new ConnectCommand(from.Id, add.PortId(input.Key)),
            new ConnectCommand(add.PortId(output.Key), to.Id),
        ];
        var positions = Document.Positions;
        if (positions.TryGetValue(from.NodeId, out var a) && positions.TryGetValue(to.NodeId, out var b))
        {
            steps.Add(new MoveNodeCommand(add.NodeId, new((a.X + b.X) / 2, (a.Y + b.Y) / 2)));
        }
        return new($"Connect through {conversion.DisplayName}", steps);
    }
}
