using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace MASS4.Graph.Generator;

/// <summary>
/// The compile-time view of one saved asset: the same inference, slot layout, cache classification and state-machine
/// order the interpreter derives at run time, computed from the asset file and the compilation's declarations.
/// </summary>
sealed class AssetPlan
{
    internal static readonly Guid FlowType = Builtin(0x201);
    static readonly Guid Entry = Builtin(0x202);
    static readonly Guid State = Builtin(0x203);
    static readonly Guid Transition = Builtin(0x204);
    static readonly Guid Wildcard = Builtin(0x301);
    static readonly Guid GetVariable = Builtin(0x302);
    static readonly Guid Output = Builtin(0x303);
    static readonly Guid TriggerAnnotation = Builtin(0x221);
    static readonly Guid PriorityAnnotation = Builtin(0x222);
    internal static readonly Guid ConditionKey = Builtin(0x216);

    public Guid AssetId { get; private set; }
    public List<Variable> Variables { get; } = [];
    public List<Node> Nodes { get; } = [];
    public List<Port> Ports { get; } = [];
    public List<Edge> Edges { get; } = [];
    public List<Slot> Slots { get; } = [];
    public string Structure { get; private set; } = "";
    public List<string> Features { get; } = [];
    public List<(Guid Variable, int Port)> Outputs { get; } = [];
    public List<int> Observed { get; } = [];
    public MachinePlan? Machine { get; private set; }

    readonly Dictionary<Guid, (ITypeSymbol Type, bool Reference)> values = [];
    readonly Dictionary<Guid, Definition> declarations = [];
    readonly Dictionary<Guid, int> portIndex = [];
    readonly Dictionary<Guid, List<Edge>> edgesByPort = [];
    readonly List<string> errors = [];

    public IReadOnlyList<string> Errors => errors;

    public static AssetPlan Create(string json, IEnumerable<Definition> definitions)
    {
        var plan = new AssetPlan();
        foreach (var definition in definitions)
        {
            if (!Guid.TryParse(definition.Id, out var id))
            {
                continue;
            }
            if (definition.Kind == "GraphValueAttribute" &&
                definition.Attribute.ConstructorArguments.LastOrDefault().Value is ITypeSymbol type)
            {
                plan.values[id] = (type, type.IsReferenceType);
            }
            else if (definition.Kind == "GraphNodeAttribute")
            {
                plan.declarations[id] = definition;
            }
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            plan.Read(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or KeyNotFoundException or
                                              InvalidOperationException)
        {
            plan.errors.Add($"The asset is not a readable graph document: {exception.Message}");
            return plan;
        }
        if (plan.errors.Count == 0)
        {
            plan.Classify();
        }
        if (plan.errors.Count == 0)
        {
            plan.LayOut();
            plan.PlanMachine();
        }
        return plan;
    }

    void Read(JsonElement root)
    {
        if (root.GetProperty("format").GetString() != "mass4.graph")
        {
            errors.Add("The asset is not a 'mass4.graph' document.");
            return;
        }
        AssetId = Guid.Parse(root.GetProperty("assetId").GetString()!);
        foreach (var element in Array(root, "variables"))
        {
            Variables.Add(new(Guid.Parse(element.GetProperty("id").GetString()!),
                Guid.Parse(element.GetProperty("type").GetString()!), element.GetProperty("scope").GetString()!));
        }
        Variables.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        List<JsonElement> nodes = [.. Array(root, "nodes")];
        nodes.Sort(static (a, b) => Guid.Parse(a.GetProperty("id").GetString()!)
            .CompareTo(Guid.Parse(b.GetProperty("id").GetString()!)));
        foreach (var element in nodes)
        {
            ReadNode(element);
        }
        foreach (var element in Array(root, "edges"))
        {
            var edge = new Edge(Guid.Parse(element.GetProperty("id").GetString()!),
                Guid.Parse(element.GetProperty("source").GetString()!),
                Guid.Parse(element.GetProperty("target").GetString()!));
            Edges.Add(edge);
        }
        Edges.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        foreach (var edge in Edges)
        {
            Attach(edge.Source, edge);
            Attach(edge.Target, edge);
        }
        Structure = Describe();
    }

    void ReadNode(JsonElement element)
    {
        var node = new Node(Nodes.Count, Guid.Parse(element.GetProperty("id").GetString()!),
            Guid.Parse(element.GetProperty("type").GetString()!), Ports.Count);
        foreach (var saved in Array(element, "ports"))
        {
            var port = new Port(Ports.Count, node.Index, Guid.Parse(saved.GetProperty("id").GetString()!),
                Guid.Parse(saved.GetProperty("key").GetString()!), Guid.Parse(saved.GetProperty("type").GetString()!),
                saved.GetProperty("direction").GetString() == "output");
            portIndex[port.Id] = port.Index;
            Ports.Add(port);
            node.PortCount++;
        }
        if (element.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object &&
            (node.Type == GetVariable || node.Type == Output) && payload.TryGetProperty("variable", out var variable))
        {
            node.Variable = Guid.Parse(variable.GetString()!);
        }
        if (element.TryGetProperty("annotations", out var annotations) && annotations.ValueKind == JsonValueKind.Object)
        {
            foreach (var annotation in annotations.EnumerateObject())
            {
                var key = Guid.Parse(annotation.Name);
                if (key == PriorityAnnotation)
                {
                    node.Priority = annotation.Value.GetInt32();
                }
                else if (key == TriggerAnnotation)
                {
                    node.HasTrigger = true;
                }
            }
        }
        Nodes.Add(node);
    }

    /// <summary>The topology the generated code bakes in, in the format of the emitted run-time check.</summary>
    string Describe()
    {
        var text = new StringBuilder();
        foreach (var variable in Variables)
        {
            text.Append("V ").Append(Text(variable.Id)).Append(' ').Append(Text(variable.Type)).Append(' ')
                .Append(variable.Scope).Append('\n');
        }
        foreach (var node in Nodes)
        {
            text.Append("N ").Append(Text(node.Id)).Append(' ').Append(Text(node.Type)).Append('\n');
            for (var p = 0; p < node.PortCount; p++)
            {
                var port = Ports[node.FirstPort + p];
                text.Append("P ").Append(Text(port.Id)).Append(' ').Append(Text(port.Key)).Append(' ')
                    .Append(Text(port.Type)).Append(port.IsOutput ? " output" : " input").Append('\n');
            }
            if (node.Variable is { } variable)
            {
                text.Append("R ").Append(Text(variable)).Append('\n');
            }
            if (node.Priority != 0)
            {
                text.Append("A ").Append(node.Priority.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            if (node.HasTrigger)
            {
                text.Append("T\n");
            }
        }
        foreach (var edge in Edges)
        {
            text.Append("E ").Append(Text(edge.Id)).Append(' ').Append(Text(edge.Source)).Append(' ')
                .Append(Text(edge.Target)).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>Assigns dataflow and state roles from declarations and the built-in types.</summary>
    void Classify()
    {
        foreach (var node in Nodes)
        {
            if (node.Type == GetVariable || node.Type == Output)
            {
                node.Data = DataRole.Variable;
            }
            else if (node.Type == Entry)
            {
                node.Role = StateRole.Entry;
            }
            else if (node.Type == State)
            {
                node.Role = StateRole.State;
            }
            else if (node.Type == Transition)
            {
                node.Role = StateRole.Transition;
                node.Transition = TransitionKind.Trigger;
            }
            else if (declarations.TryGetValue(node.Type, out var definition))
            {
                ClassifyDeclared(node, definition);
            }
            else
            {
                errors.Add($"Node {Text(node.Id)} has type {Text(node.Type)}, which no GraphNode declaration in this compilation provides.");
            }
        }
    }

    void ClassifyDeclared(Node node, Definition definition)
    {
        node.Declaration = definition;
        if (definition.IsGuarded)
        {
            node.Role = StateRole.Transition;
            node.Transition = TransitionKind.Guarded;
            return;
        }
        var behavior = definition.One("Behavior");
        switch (behavior?.ConstructorArguments[0].Value)
        {
            case 0:
                node.Data = DataRole.Behavior;
                node.Owner = Definition.TypeName(behavior.ConstructorArguments[1]);
                node.Method = Definition.Named<string>(behavior, "Method");
                node.Volatile = Definition.Named<bool>(behavior, "Volatile");
                var features = behavior.NamedArguments.FirstOrDefault(static p => p.Key == "Features");
                node.Features = features.Key is null ? [] : [.. features.Value.Values.Select(Definition.TypeName)];
                break;
            case 2:
                node.Role = StateRole.State;
                node.Owner = Definition.TypeName(behavior.ConstructorArguments[1]);
                break;
            case 3:
                node.Role = StateRole.Transition;
                node.Transition = TransitionKind.Declared;
                node.Owner = Definition.TypeName(behavior.ConstructorArguments[1]);
                break;
            case 1:
                errors.Add($"Node {Text(node.Id)} uses control flow, which generated backends do not compile yet.");
                break;
        }
    }

    /// <summary>Mirrors the dataflow compiler: inference, then variable, output and input slots, then program slots.</summary>
    void LayOut()
    {
        var inferred = Infer();
        var types = new Guid?[Ports.Count];
        foreach (var port in Ports)
        {
            var type = inferred.TryGetValue(port.Id, out var resolved) ? resolved : port.Type;
            types[port.Index] = values.ContainsKey(type) ? type : null;
        }
        Dictionary<Guid, int> variableSlots = [];
        foreach (var variable in Variables)
        {
            if (values.ContainsKey(variable.Type))
            {
                variable.Slot = Add(variable.Type);
                variableSlots[variable.Id] = variable.Slot;
            }
        }
        foreach (var port in Ports.Where(static port => port.IsOutput))
        {
            var node = Nodes[port.Node];
            if (node.Variable is { } variable && variableSlots.TryGetValue(variable, out var aliased))
            {
                port.Slot = aliased;
            }
            else if (types[port.Index] is { } type)
            {
                port.Slot = Add(type);
            }
        }
        foreach (var port in Ports.Where(port => !port.IsOutput && types[port.Index] is not null))
        {
            LayOutInput(port, types[port.Index]!.Value);
        }
        PlanNodes(variableSlots);
    }

    void LayOutInput(Port port, Guid type)
    {
        var edge = EdgesOf(port.Id).FirstOrDefault(IsDataEdge);
        var source = edge is null ? null : Ports[portIndex[edge.Source]];
        if (source is null || source.Slot < 0)
        {
            port.Slot = Add(type);
            return;
        }
        var sourceType = Slots[source.Slot].ValueType;
        if (sourceType == type || SameStorage(sourceType, type))
        {
            port.Slot = source.Slot;
            port.SourceNode = source.Node;
            return;
        }
        errors.Add($"Edge {Text(edge!.Id)} needs a coercion; generated backends compile declared conversion nodes only.");
    }

    void PlanNodes(Dictionary<Guid, int> variableSlots)
    {
        foreach (var node in Nodes)
        {
            for (var p = 0; p < node.PortCount; p++)
            {
                if (Ports[node.FirstPort + p].SourceNode is var source and >= 0 && !node.Sources.Contains(source))
                {
                    node.Sources.Add(source);
                }
            }
        }
        MarkStatic();
        foreach (var feature in Nodes.SelectMany(static node => node.Features).Where(feature => !Features.Contains(feature)))
        {
            Features.Add(feature);
        }
        foreach (var variable in Variables.Where(v => v.Scope == "output" && variableSlots.ContainsKey(v.Id)))
        {
            var defined = Nodes.LastOrDefault(node => node.Type == Output && node.Variable == variable.Id &&
                                                      Ports[node.FirstPort].SourceNode >= 0);
            Outputs.Add((variable.Id, defined is null ? -1 : defined.FirstPort));
        }
        Observed.AddRange(Ports.Where(static port => port.IsOutput && port.Slot >= 0)
            .OrderBy(static port => port.Id).Select(static port => port.Index));
    }

    /// <summary>Marks nodes cacheable per instance: a non-volatile behavior fed only by such nodes.</summary>
    void MarkStatic()
    {
        var pending = Nodes.Select(static node => node.Sources.Count).ToArray();
        var dependents = Nodes.Select(static _ => new List<int>()).ToArray();
        foreach (var node in Nodes)
        {
            foreach (var source in node.Sources)
            {
                dependents[source].Add(node.Index);
            }
        }
        var ready = new Queue<int>(Nodes.Where(static node => node.Sources.Count == 0).Select(static node => node.Index));
        while (ready.Count > 0)
        {
            var node = Nodes[ready.Dequeue()];
            node.Static = node.Data != DataRole.None && !node.Volatile && node.Sources.All(s => Nodes[s].Static);
            foreach (var dependent in dependents[node.Index].Where(dependent => --pending[dependent] == 0))
            {
                ready.Enqueue(dependent);
            }
        }
    }

    /// <summary>Mirrors the state compiler: sorted states, joined transitions in priority then identity order.</summary>
    void PlanMachine()
    {
        if (!Nodes.Any(static node => node.Role != StateRole.None))
        {
            return;
        }
        var states = Nodes.Where(static node => node.Role == StateRole.State).ToList();
        var stateIndex = states.Select(static (node, index) => (node.Index, index)).ToDictionary(p => p.Item1, p => p.Item2);
        var entries = Nodes.Where(static node => node.Role == StateRole.Entry).ToList();
        if (entries.Count != 1 || Neighbor(entries[0], true, stateIndex) is not (var initial and >= 0))
        {
            errors.Add("A compiled state machine needs exactly one entry node leading to a state.");
            return;
        }
        var machine = new MachinePlan(entries[0], initial, states);
        List<(int From, Node Node, int To)> links = [];
        foreach (var node in Nodes.Where(static node => node.Role == StateRole.Transition))
        {
            var link = (Neighbor(node, false, stateIndex), node, Neighbor(node, true, stateIndex));
            if (link.Item1 < 0 || link.Item3 < 0)
            {
                errors.Add($"Transition {Text(node.Id)} needs a source state and a target state.");
                return;
            }
            links.Add(link);
        }
        for (var s = 0; s < states.Count; s++)
        {
            machine.FirstTransition.Add(machine.Transitions.Count);
            machine.Transitions.AddRange(links.Where(link => link.From == s)
                .OrderByDescending(static link => link.Node.Priority).ThenBy(static link => link.Node.Id)
                .Select(static link => (link.Node, link.To)));
        }
        machine.FirstTransition.Add(machine.Transitions.Count);
        Machine = machine;
    }

    int Neighbor(Node node, bool output, Dictionary<int, int> states)
    {
        var port = Enumerable.Range(node.FirstPort, node.PortCount).Select(i => Ports[i])
            .FirstOrDefault(port => port.Type == FlowType && port.IsOutput == output);
        var edge = port is null ? null : EdgesOf(port.Id).FirstOrDefault();
        if (edge is null || !portIndex.TryGetValue(output ? edge.Target : edge.Source, out var far))
        {
            return -1;
        }
        return states.TryGetValue(Ports[far].Node, out var index) ? index : -1;
    }

    Dictionary<Guid, Guid> Infer()
    {
        var parent = new Dictionary<int, int>();
        var binding = new Dictionary<int, Guid>();
        int Find(int node)
        {
            parent.TryAdd(node, node);
            while (parent[node] != node)
            {
                node = parent[node] = parent[parent[node]];
            }
            return node;
        }
        void Bind(int root, Guid type)
        {
            if (!binding.TryGetValue(root, out var bound))
            {
                binding[root] = type;
            }
            else if (bound != type)
            {
                errors.Add("Wildcard ports would carry two different types.");
            }
        }
        foreach (var node in Nodes)
        {
            if (node.Variable is { } id && Variables.FirstOrDefault(v => v.Id == id) is { } variable)
            {
                Bind(Find(node.Index), variable.Type);
            }
        }
        foreach (var edge in Edges.Where(IsDataEdge))
        {
            var source = Ports[portIndex[edge.Source]];
            var target = Ports[portIndex[edge.Target]];
            switch (source.Type == Wildcard, target.Type == Wildcard)
            {
                case (true, true):
                    var (a, b) = (Find(source.Node), Find(target.Node));
                    if (a != b)
                    {
                        parent[b] = a;
                        if (binding.TryGetValue(b, out var bound))
                        {
                            Bind(a, bound);
                        }
                    }
                    break;
                case (true, false):
                    Bind(Find(source.Node), target.Type);
                    break;
                case (false, true):
                    Bind(Find(target.Node), source.Type);
                    break;
            }
        }
        var resolved = new Dictionary<Guid, Guid>();
        foreach (var port in Ports.Where(static port => port.Type == Wildcard))
        {
            if (parent.ContainsKey(port.Node) && binding.TryGetValue(Find(port.Node), out var type))
            {
                resolved[port.Id] = type;
            }
        }
        return resolved;
    }

    bool IsDataEdge(Edge edge) => portIndex.TryGetValue(edge.Source, out var source) &&
                                  portIndex.TryGetValue(edge.Target, out var target) &&
                                  IsData(Ports[source].Type) && IsData(Ports[target].Type);

    bool IsData(Guid type) => type == Wildcard || values.ContainsKey(type);

    bool SameStorage(Guid a, Guid b) => values[a].Reference == values[b].Reference &&
                                        SymbolEqualityComparer.Default.Equals(values[a].Type, values[b].Type);

    int Add(Guid type)
    {
        var (symbol, reference) = values[type];
        Slots.Add(new(Slots.Count, type, symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), reference));
        return Slots.Count - 1;
    }

    IEnumerable<Edge> EdgesOf(Guid port) => edgesByPort.TryGetValue(port, out var list) ? list : [];

    void Attach(Guid port, Edge edge)
    {
        if (!edgesByPort.TryGetValue(port, out var list))
        {
            edgesByPort[port] = list = [];
        }
        list.Add(edge);
    }

    static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];

    internal static string Text(Guid id) => id.ToString("D");

    static Guid Builtin(int value) => new($"4d415353-3447-5241-5048-{value:x12}");

    internal sealed record Variable(Guid Id, Guid Type, string Scope)
    {
        public int Slot { get; set; } = -1;
    }

    internal sealed class Node(int index, Guid id, Guid type, int firstPort)
    {
        public int Index { get; } = index;
        public Guid Id { get; } = id;
        public Guid Type { get; } = type;
        public int FirstPort { get; } = firstPort;
        public int PortCount { get; set; }
        public Guid? Variable { get; set; }
        public int Priority { get; set; }
        public bool HasTrigger { get; set; }
        public Definition? Declaration { get; set; }
        public DataRole Data { get; set; }
        public StateRole Role { get; set; }
        public TransitionKind Transition { get; set; }
        public string? Owner { get; set; }
        public string? Method { get; set; }
        public bool Volatile { get; set; }
        public string[] Features { get; set; } = [];
        public List<int> Sources { get; } = [];
        public bool Static { get; set; }
    }

    internal sealed class Port(int index, int node, Guid id, Guid key, Guid type, bool isOutput)
    {
        public int Index { get; } = index;
        public int Node { get; } = node;
        public Guid Id { get; } = id;
        public Guid Key { get; } = key;
        public Guid Type { get; } = type;
        public bool IsOutput { get; } = isOutput;
        public int Slot { get; set; } = -1;
        public int SourceNode { get; set; } = -1;
    }

    internal sealed record Edge(Guid Id, Guid Source, Guid Target);

    internal sealed record Slot(int Index, Guid ValueType, string ClrType, bool Reference);

    internal sealed class MachinePlan(Node entry, int initial, List<Node> states)
    {
        public Node Entry { get; } = entry;
        public int Initial { get; } = initial;
        public List<Node> States { get; } = states;
        public List<int> FirstTransition { get; } = [];
        public List<(Node Node, int Target)> Transitions { get; } = [];
    }

    internal enum DataRole { None, Variable, Behavior }

    internal enum StateRole { None, Entry, State, Transition }

    internal enum TransitionKind { None, Trigger, Guarded, Declared }
}
