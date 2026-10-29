using System.Text;
using Microsoft.CodeAnalysis;
using static MASS4.Graph.Generator.AssetPlan;

namespace MASS4.Graph.Generator;

/// <summary>
/// Emits an <c>ICompiledGraph</c> for one planned asset. Evaluation is straight-line generated code: each node has an
/// ensure method that refreshes its stale sources and calls its behavior directly, and each state's transitions are
/// unrolled in check order. Storage follows the interpreter's slot order exactly, so layouts are identical.
/// </summary>
static class AssetEmitter
{
    const string G = "global::MASS4.Graph.";
    const string Ctx = G + "EvaluationContext c, " + G + "InstanceBuffer b, " + G + "InstanceHandle h";
    const string Args = "c, b, h";

    public static string Emit(INamedTypeSymbol symbol, AssetPlan plan)
    {
        var name = symbol.Name;
        var text = new StringBuilder(DeclarationEmitter.Header);
        text.AppendLine("#pragma warning disable CS0162");
        if (!symbol.ContainingNamespace.IsGlobalNamespace)
        {
            text.AppendLine($"namespace {symbol.ContainingNamespace.ToDisplayString()};");
        }
        text.AppendLine($"partial class @{name} : {G}ICompiledGraph\n{{");
        Header(text, name, plan);
        Constructor(text, name, plan);
        Contract(text, plan);
        Storage(text, plan);
        Evaluation(text, plan);
        Frame(text, name, plan);
        if (plan.Machine is { } machine)
        {
            Machine(text, name, plan, machine);
        }
        Backend(text, name);
        Describe(text);
        return text.AppendLine("}").ToString();
    }

    static void Header(StringBuilder text, string name, AssetPlan plan)
    {
        text.AppendLine($"const string Structure = {Definition.Quote(plan.Structure)};");
        text.AppendLine($"static readonly {G}StableId CompiledAsset = {Definition.Identity(Text(plan.AssetId))};");
        text.AppendLine($"static readonly int[] FirstPorts = [{string.Join(", ", plan.Nodes.Select(static n => n.FirstPort))}];");
        text.AppendLine($"readonly {G}GraphNode[] nodes;");
        text.AppendLine("readonly DataFrame data;");
        text.AppendLine($"readonly object?[] defaults;");
        foreach (var slot in plan.Slots)
        {
            text.AppendLine(slot.Reference
                ? $"readonly {G}InstanceReferenceSlot<{slot.ClrType}> s{slot.Index};"
                : $"readonly {G}InstanceSlot<{slot.ClrType}> s{slot.Index};");
        }
        text.AppendLine($"readonly {G}InstanceSlot<int> epochCounter;");
        text.AppendLine($"readonly {G}InstanceArraySlot<int> epochs;");
        text.AppendLine($"readonly {G}InstanceSlot<byte> initialized;");
        if (plan.Machine is not null)
        {
            text.AppendLine("readonly Machine machine;");
        }
        for (var i = 0; i < plan.Outputs.Count; i++)
        {
            text.AppendLine($"static readonly {G}ElementId O{i} = {Element(plan.Outputs[i].Variable)};");
        }
        foreach (var variable in plan.Variables.Where(static v => v.Slot >= 0))
        {
            text.AppendLine($"static readonly {G}ElementId V{variable.Slot} = {Element(variable.Id)};");
        }
        text.AppendLine($"static readonly {G}ElementId[] ObservedPorts = [{string.Join(", ", plan.Observed.Select(p => Element(plan.Ports[p].Id)))}];");
        text.AppendLine($"static readonly {G}ElementId[] DeclaredOutputs = [{string.Join(", ", plan.Outputs.Select(static (_, i) => $"O{i}"))}];");
    }

    static void Constructor(StringBuilder text, string name, AssetPlan plan)
    {
        text.AppendLine($$"""
            /// <summary>Binds the compiled asset to its loaded document, whose payloads and defaults it reads.</summary>
            /// <exception cref="global::System.InvalidOperationException">The document is another asset or its topology changed after compilation.</exception>
            public @{{name}}({{G}}GraphDocument document)
            {
                if (document.AssetId != CompiledAsset)
                {
                    throw new global::System.InvalidOperationException($"This backend was compiled for asset {CompiledAsset}, not {document.AssetId}.");
                }
                if (Describe(document) != Structure)
                {
                    throw new global::System.InvalidOperationException("The asset's topology changed after it was compiled; rebuild to regenerate its backend.");
                }
                nodes = [.. global::System.Linq.Enumerable.OrderBy(document.Nodes, static n => n.Id)];
                var layout = new {{G}}InstanceLayoutBuilder();
            """);
        foreach (var slot in plan.Slots)
        {
            text.AppendLine(slot.Reference
                ? $"s{slot.Index} = layout.AddReference<{slot.ClrType}>();"
                : $"s{slot.Index} = layout.Add<{slot.ClrType}>();");
        }
        text.AppendLine($$"""
            epochCounter = layout.Add<int>();
            epochs = layout.AddArray<int>({{plan.Nodes.Count}});
            initialized = layout.Add<byte>();
            defaults = [{{string.Join(", ", plan.Variables.Where(static v => v.Slot >= 0).Select(v => $"Default(document, {Element(v.Id)})"))}}];
            data = new DataFrame(this);
            """);
        var features = string.Join(", ", plan.Features.Select(static f => $"typeof({f})"));
        if (plan.Machine is not null)
        {
            text.AppendLine($$"""
                machine = new Machine(this, document, layout);
                RequiredFeatures = [.. global::System.Linq.Enumerable.Distinct(global::System.Linq.Enumerable.Append(
                    global::System.Linq.Enumerable.Concat(new global::System.Type[] { {{features}} }, machine.Features),
                    typeof(global::MASS4.Graph.State.StateTime)))];
                """);
        }
        else
        {
            text.AppendLine($"RequiredFeatures = new global::System.Type[] {{ {features} }};");
        }
        text.AppendLine("""
                InstanceLayout = layout.Build();
            }

            static object? Default(global::MASS4.Graph.GraphDocument document, global::MASS4.Graph.ElementId id) =>
                document.TryGetVariable(id, out var variable) ? variable.DefaultValue : null;
            """);
    }

    static void Contract(StringBuilder text, AssetPlan plan)
    {
        var machine = plan.Machine is not null;
        text.AppendLine($$"""
            /// <inheritdoc />
            public {{G}}StableId AssetId => CompiledAsset;
            /// <inheritdoc />
            public {{G}}InstanceLayout InstanceLayout { get; }
            /// <inheritdoc />
            public global::System.Collections.Generic.IReadOnlyList<global::System.Type> RequiredFeatures { get; }
            /// <inheritdoc />
            public global::System.Collections.Generic.IReadOnlyList<{{G}}ElementId> Outputs => DeclaredOutputs;

            /// <inheritdoc />
            public void Initialize({{Ctx}})
            {
                c.Require(RequiredFeatures);
                InitializeData(b, h);
                {{(machine ? "machine.Initialize(c, b, h);" : "")}}
            }

            /// <inheritdoc />
            public global::System.Threading.Tasks.ValueTask EvaluateAsync({{Ctx}})
            {
                c.Cancellation.ThrowIfCancellationRequested();
                c.Require(RequiredFeatures);
                BeginEvaluation(b, h);
                {{(machine ? "machine.Tick(c, b, h, c.Get<global::MASS4.Graph.State.StateTime>().DeltaSeconds);" : "")}}
                return global::System.Threading.Tasks.ValueTask.CompletedTask;
            }

            /// <inheritdoc />
            public bool Raise({{G}}InstanceBuffer b, {{G}}InstanceHandle h, {{G}}StableId signal) => {{(machine ? "machine.Raise(b, h, signal)" : "false")}};
            """);
        Variables(text, plan);
        Outputs(text, plan);
        text.AppendLine($$"""
            /// <inheritdoc />
            public void Observe({{Ctx}}, global::System.Collections.Generic.ICollection<{{G}}GraphObservation> observations)
            {
                foreach (var output in DeclaredOutputs)
                {
                    observations.Add(new(output, ReadOutputBoxed(c, b, h, output)));
                }
            """);
        for (var i = 0; i < plan.Observed.Count; i++)
        {
            text.AppendLine($"observations.Add(new(ObservedPorts[{i}], ReadBoxed(c, b, h, {plan.Observed[i]})));");
        }
        if (machine)
        {
            text.AppendLine("machine.Observe(b, h, observations);");
        }
        text.AppendLine("}");
    }

    static void Variables(StringBuilder text, AssetPlan plan)
    {
        text.AppendLine($"/// <inheritdoc />\npublic bool SetVariable({G}InstanceBuffer b, {G}InstanceHandle h, {G}ElementId variable, object? value)\n{{");
        foreach (var variable in plan.Variables.Where(static v => v.Slot >= 0))
        {
            var slot = plan.Slots[variable.Slot];
            var accepts = slot.Reference ? $"value is null or {slot.ClrType}" : $"value is {slot.ClrType}";
            var write = slot.Reference
                ? $"b.SetReference(h, s{slot.Index}, ({slot.ClrType}?)value);"
                : $"b.Get(h, s{slot.Index}) = ({slot.ClrType})value!;";
            text.AppendLine($"if (variable == V{slot.Index}) {{ if (!({accepts})) {{ return false; }} EnsureInitialized(b, h); {write} Invalidate(b, h); return true; }}");
        }
        text.AppendLine("return false;\n}");
        text.AppendLine($"/// <inheritdoc />\npublic bool SetVariable<T>({G}InstanceBuffer b, {G}InstanceHandle h, {G}ElementId variable, T value) where T : unmanaged\n{{");
        foreach (var variable in plan.Variables.Where(v => v.Slot >= 0 && !plan.Slots[v.Slot].Reference))
        {
            var slot = plan.Slots[variable.Slot];
            text.AppendLine($"if (variable == V{slot.Index}) {{ if (typeof(T) != typeof({slot.ClrType})) {{ return false; }} EnsureInitialized(b, h); b.Get(h, s{slot.Index}) = As<T, {slot.ClrType}>(ref value); Invalidate(b, h); return true; }}");
        }
        text.AppendLine("return false;\n}");
    }

    static void Outputs(StringBuilder text, AssetPlan plan)
    {
        string Slot(int i) => $"s{plan.Variables.First(v => v.Id == plan.Outputs[i].Variable).Slot}";
        text.AppendLine($"/// <inheritdoc />\npublic T ReadOutput<T>({Ctx}, {G}ElementId output) where T : unmanaged\n{{");
        for (var i = 0; i < plan.Outputs.Count; i++)
        {
            var (variable, port) = plan.Outputs[i];
            var slot = plan.Slots[plan.Variables.First(v => v.Id == variable).Slot];
            var read = port >= 0 ? $"DataRead<T>(c, b, h, {port})"
                : slot.Reference ? $"throw Mismatch(typeof(T), typeof({slot.ClrType}))"
                : $"ReadVariable<{slot.ClrType}, T>(b, h, {Slot(i)})";
            text.AppendLine($"if (output == O{i}) {{ return {read}; }}");
        }
        text.AppendLine("throw new global::System.Collections.Generic.KeyNotFoundException($\"The document declares no output {output}.\");\n}");
        text.AppendLine($"/// <inheritdoc />\npublic T? ReadOutputReference<T>({Ctx}, {G}ElementId output) where T : class\n{{");
        for (var i = 0; i < plan.Outputs.Count; i++)
        {
            var (variable, port) = plan.Outputs[i];
            var slot = plan.Slots[plan.Variables.First(v => v.Id == variable).Slot];
            var read = port >= 0 ? $"DataReadReference<T>(c, b, h, {port})"
                : slot.Reference ? $"ReadVariableReference<{slot.ClrType}, T>(b, h, {Slot(i)})"
                : $"throw Mismatch(typeof(T), typeof({slot.ClrType}))";
            text.AppendLine($"if (output == O{i}) {{ return {read}; }}");
        }
        text.AppendLine("throw new global::System.Collections.Generic.KeyNotFoundException($\"The document declares no output {output}.\");\n}");
        text.AppendLine($"object? ReadOutputBoxed({Ctx}, {G}ElementId output)\n{{");
        for (var i = 0; i < plan.Outputs.Count; i++)
        {
            var port = plan.Outputs[i].Port;
            var slot = plan.Slots[plan.Variables.First(v => v.Id == plan.Outputs[i].Variable).Slot];
            var read = port >= 0 ? $"return ReadBoxed(c, b, h, {port});"
                : slot.Reference ? $"EnsureInitialized(b, h); return b.GetReference(h, {Slot(i)});"
                : $"EnsureInitialized(b, h); return b.Get(h, {Slot(i)});";
            text.AppendLine($"if (output == O{i}) {{ {read} }}");
        }
        text.AppendLine("throw new global::System.Collections.Generic.KeyNotFoundException($\"The document declares no output {output}.\");\n}");
    }

    static void Storage(StringBuilder text, AssetPlan plan)
    {
        Switch(text, $"T ReadFlat<T>({G}InstanceBuffer b, {G}InstanceHandle h, int flat) where T : unmanaged", plan,
            static slot => slot.Reference ? $"throw Mismatch(typeof(T), typeof({slot.ClrType}));" : $"return As<{slot.ClrType}, T>(ref b.Get(h, s{slot.Index}));");
        Switch(text, $"T? ReadFlatReference<T>({G}InstanceBuffer b, {G}InstanceHandle h, int flat) where T : class", plan,
            static slot => slot.Reference ? $"return AsReference<{slot.ClrType}, T>(b.GetReference(h, s{slot.Index}));" : $"throw Mismatch(typeof(T), typeof({slot.ClrType}));");
        Switch(text, $"void WriteFlat<T>({G}InstanceBuffer b, {G}InstanceHandle h, int flat, T value) where T : unmanaged", plan,
            static slot => slot.Reference ? $"throw Mismatch(typeof(T), typeof({slot.ClrType}));" : $"b.Get(h, s{slot.Index}) = As<T, {slot.ClrType}>(ref value); return;");
        Switch(text, $"void WriteFlatReference<T>({G}InstanceBuffer b, {G}InstanceHandle h, int flat, T? value) where T : class", plan,
            static slot => slot.Reference ? $"b.SetReference(h, s{slot.Index}, AsReference<T, {slot.ClrType}>(value)); return;" : $"throw Mismatch(typeof(T), typeof({slot.ClrType}));");
        Switch(text, $"void CopyFlat({G}InstanceBuffer b, {G}InstanceHandle h, int flat, int target)", plan,
            static slot => slot.Reference ? $"WriteFlatReference(b, h, target, b.GetReference(h, s{slot.Index})); return;" : $"WriteFlat(b, h, target, b.Get(h, s{slot.Index})); return;");
        Switch(text, $"object? ReadBoxedFlat({G}InstanceBuffer b, {G}InstanceHandle h, int flat)", plan,
            static slot => slot.Reference ? $"return b.GetReference(h, s{slot.Index});" : $"return b.Get(h, s{slot.Index});");
        var connected = plan.Ports.Where(static p => p.SourceNode >= 0).Select(static p => p.Index.ToString()).ToArray();
        text.AppendLine($"static bool Connected(int flat) => {(connected.Length == 0 ? "false" : $"flat is {string.Join(" or ", connected)}")};");
        text.AppendLine($$"""
            static T As<TSlot, T>(ref TSlot value) where TSlot : unmanaged where T : unmanaged =>
                typeof(T) == typeof(TSlot) ? global::System.Runtime.CompilerServices.Unsafe.As<TSlot, T>(ref value) : throw Mismatch(typeof(T), typeof(TSlot));
            static T? AsReference<TSlot, T>(TSlot? value) where TSlot : class where T : class =>
                typeof(T) == typeof(TSlot) ? global::System.Runtime.CompilerServices.Unsafe.As<T>(value) : throw Mismatch(typeof(T), typeof(TSlot));
            static global::System.InvalidOperationException Mismatch(global::System.Type requested, global::System.Type stored) =>
                new($"Expected a {stored.Name} value, not {requested.Name}.");
            static global::System.InvalidOperationException NoData() => new("The port carries no data.");
            T ReadVariable<TSlot, T>({{G}}InstanceBuffer b, {{G}}InstanceHandle h, {{G}}InstanceSlot<TSlot> slot) where TSlot : unmanaged where T : unmanaged
            {
                EnsureInitialized(b, h);
                return As<TSlot, T>(ref b.Get(h, slot));
            }
            T? ReadVariableReference<TSlot, T>({{G}}InstanceBuffer b, {{G}}InstanceHandle h, {{G}}InstanceReferenceSlot<TSlot> slot) where TSlot : class where T : class
            {
                EnsureInitialized(b, h);
                return AsReference<TSlot, T>(b.GetReference(h, slot));
            }
            """);
    }

    /// <summary>A switch from flat port index to its slot; ports sharing a slot share a case.</summary>
    static void Switch(StringBuilder text, string signature, AssetPlan plan, Func<Slot, string> body)
    {
        text.AppendLine($"{signature}\n{{\nswitch (flat)\n{{");
        foreach (var group in plan.Ports.Where(static p => p.Slot >= 0).GroupBy(static p => p.Slot))
        {
            text.AppendLine($"{string.Concat(group.Select(static p => $"case {p.Index}: "))}{{ {body(plan.Slots[group.Key])} }}");
        }
        text.AppendLine("}\nthrow NoData();\n}");
    }

    static void Evaluation(StringBuilder text, AssetPlan plan)
    {
        text.AppendLine($"void InitializeData({G}InstanceBuffer b, {G}InstanceHandle h)\n{{");
        var index = 0;
        foreach (var variable in plan.Variables.Where(static v => v.Slot >= 0))
        {
            var slot = plan.Slots[variable.Slot];
            text.AppendLine(slot.Reference
                ? $"b.SetReference(h, s{slot.Index}, defaults[{index}] as {slot.ClrType});"
                : $"b.Get(h, s{slot.Index}) = defaults[{index}] is {slot.ClrType} d{index} ? d{index} : default;");
            index++;
        }
        text.AppendLine($$"""
                b.Get(h, initialized) = 1;
                Invalidate(b, h);
            }
            void EnsureInitialized({{G}}InstanceBuffer b, {{G}}InstanceHandle h)
            {
                if (b.Get(h, initialized) == 0)
                {
                    InitializeData(b, h);
                }
            }
            void Invalidate({{G}}InstanceBuffer b, {{G}}InstanceHandle h) => b.GetSpan(h, epochs).Clear();
            void BeginEvaluation({{G}}InstanceBuffer b, {{G}}InstanceHandle h)
            {
                EnsureInitialized(b, h);
                ref var counter = ref b.Get(h, epochCounter);
                if (counter >= int.MaxValue - 1)
                {
                    b.GetSpan(h, epochs).Clear();
                    counter = 0;
                }
                else
                {
                    counter++;
                }
            }
            T DataRead<T>({{Ctx}}, int flat) where T : unmanaged
            {
                Prepare(c, b, h, flat);
                return ReadFlat<T>(b, h, flat);
            }
            T? DataReadReference<T>({{Ctx}}, int flat) where T : class
            {
                Prepare(c, b, h, flat);
                return ReadFlatReference<T>(b, h, flat);
            }
            object? ReadBoxed({{Ctx}}, int flat)
            {
                Prepare(c, b, h, flat);
                return ReadBoxedFlat(b, h, flat);
            }
            void Prepare({{Ctx}}, int flat)
            {
                EnsureInitialized(b, h);
                var current = b.Get(h, epochCounter) + 1;
                switch (flat)
                {
            """);
        foreach (var port in plan.Ports)
        {
            var owner = port.IsOutput ? port.Node : port.SourceNode;
            if (owner >= 0 && plan.Nodes[owner].Data != DataRole.None)
            {
                text.AppendLine($"case {port.Index}: Ensure{owner}({Args}, current); break;");
            }
        }
        text.AppendLine("}\n}");
        foreach (var node in plan.Nodes.Where(static n => n.Data != DataRole.None))
        {
            text.AppendLine($"void Ensure{node.Index}({Ctx}, int current)\n{{");
            text.AppendLine($"var epoch = b.GetSpan(h, epochs)[{node.Index}];\nif (epoch == -1 || epoch == current) {{ return; }}");
            // The interpreter's explicit stack evaluates the last stale source first; recursion in reverse matches it.
            foreach (var source in Enumerable.Reverse(node.Sources).Where(s => plan.Nodes[s].Data != DataRole.None))
            {
                text.AppendLine($"Ensure{source}({Args}, current);");
            }
            if (node.Data == DataRole.Behavior)
            {
                text.AppendLine($"{node.Owner}.@{node.Method}(new global::MASS4.Graph.Dataflow.DataflowContext(data, {Args}, {node.Index}));");
            }
            text.AppendLine($"b.GetSpan(h, epochs)[{node.Index}] = {(node.Static ? "-1" : "current")};\n}}");
        }
        var publishable = plan.Ports.Where(p => p.IsOutput && p.Slot >= 0 && plan.Nodes[p.Node].Data == DataRole.None)
            .Select(static p => p.Index.ToString()).ToArray();
        text.AppendLine($$"""
            void Publish<T>({{G}}InstanceBuffer b, {{G}}InstanceHandle h, int flat, T value) where T : unmanaged
            {
                if (!({{(publishable.Length == 0 ? "false" : $"flat is {string.Join(" or ", publishable)}")}}))
                {
                    throw new global::System.InvalidOperationException("Only outputs of nodes the data plane does not evaluate can be published.");
                }
                WriteFlat(b, h, flat, value);
                BeginEvaluation(b, h);
            }
            bool HasData(int flat) => {{(plan.Ports.Any(static p => p.Slot >= 0) ? $"flat is {string.Join(" or ", plan.Ports.Where(static p => p.Slot >= 0).Select(static p => p.Index))}" : "false")}};
            """);
    }

    static void Frame(StringBuilder text, string name, AssetPlan plan) =>
        text.AppendLine($$"""
            sealed class DataFrame(@{{name}} graph) : global::MASS4.Graph.Dataflow.DataflowFrame
            {
                protected override {{G}}GraphNode NodeAt(int node) => graph.nodes[node];
                protected override bool IsConnected(int node, int port) => Connected(FirstPorts[node] + port);
                protected override T Read<T>({{G}}InstanceBuffer b, {{G}}InstanceHandle h, int node, int port) =>
                    graph.ReadFlat<T>(b, h, FirstPorts[node] + port);
                protected override T? ReadReference<T>({{G}}InstanceBuffer b, {{G}}InstanceHandle h, int node, int port) where T : class =>
                    graph.ReadFlatReference<T>(b, h, FirstPorts[node] + port);
                protected override void Write<T>({{G}}InstanceBuffer b, {{G}}InstanceHandle h, int node, int port, T value) =>
                    graph.WriteFlat(b, h, FirstPorts[node] + port, value);
                protected override void WriteReference<T>({{G}}InstanceBuffer b, {{G}}InstanceHandle h, int node, int port, T? value) where T : class =>
                    graph.WriteFlatReference(b, h, FirstPorts[node] + port, value);
                protected override void Copy({{G}}InstanceBuffer b, {{G}}InstanceHandle h, int node, int source, int target) =>
                    graph.CopyFlat(b, h, FirstPorts[node] + source, FirstPorts[node] + target);
            }
            """);

    static void Machine(StringBuilder text, string name, AssetPlan plan, MachinePlan machine)
    {
        const string S = "global::MASS4.Graph.State.";
        var states = machine.States;
        text.AppendLine($$"""
            sealed class Machine : {{S}}StateFrame
            {
                static readonly int[] StateNodes = [{{string.Join(", ", states.Select(static n => n.Index))}}];
                static readonly int[] TransitionNodes = [{{string.Join(", ", machine.Transitions.Select(static t => t.Node.Index))}}];
                static readonly {{G}}ElementId EntryNode = {{Element(machine.Entry.Id)}};
                static readonly {{G}}StableId TriggerAnnotation = {{Definition.Identity("4d415353-3447-5241-5048-000000000221")}};
                readonly @{{name}} graph;
                readonly {{G}}IdentityRegistry<{{G}}SignalType> signals;
                readonly global::System.Collections.Generic.Dictionary<{{G}}StableId, int> triggers;
                readonly int[] transitionTriggers;
                readonly int[] memoryOffsets;
                readonly int[] memorySizes;
                readonly {{G}}InstanceSlot<int> current;
                readonly {{G}}InstanceSlot<double> time;
                readonly {{G}}InstanceArraySlot<byte> raised;
            """);
        foreach (var (state, i) in states.Select(static (s, i) => (s, i)).Where(static p => p.s.Owner is not null))
        {
            text.AppendLine($"readonly {S}IStateNode state{i} = new {state.Owner}();");
        }
        for (var t = 0; t < machine.Transitions.Count; t++)
        {
            if (machine.Transitions[t].Node.Transition == TransitionKind.Declared)
            {
                text.AppendLine($"readonly {S}ITransitionNode transition{t} = new {machine.Transitions[t].Node.Owner}();");
            }
        }
        var stateBehaviors = string.Join(", ", states.Select(static (s, i) => s.Owner is null ? "null" : $"state{i}"));
        var transitionBehaviors = string.Join(", ", machine.Transitions.Select(static (t, i) =>
            t.Node.Transition == TransitionKind.Declared ? $"transition{i}" : "null"));
        text.AppendLine($$"""
            public Machine(@{{name}} graph, {{G}}GraphDocument document, {{G}}InstanceLayoutBuilder layout)
            {
                this.graph = graph;
                signals = document.Registry.Signals;
                triggers = global::System.Linq.Enumerable.ToDictionary(global::System.Linq.Enumerable.Select(
                    global::System.Linq.Enumerable.Order(global::System.Linq.Enumerable.Select(signals.Entries, static s => s.Id)),
                    static (id, index) => (id, index)));
                {{S}}IStateNode?[] states = [{{stateBehaviors}}];
                memoryOffsets = new int[states.Length];
                memorySizes = new int[states.Length];
                for (var s = 0; s < states.Length; s++)
                {
                    var size = states[s]?.MemorySize ?? 0;
                    memorySizes[s] = size;
                    memoryOffsets[s] = size > 0 ? layout.AddArray<long>((size + 7) / 8).Offset : 0;
                }
                transitionTriggers = new int[TransitionNodes.Length];
                for (var t = 0; t < TransitionNodes.Length; t++)
                {
                    transitionTriggers[t] = graph.nodes[TransitionNodes[t]].Annotations.TryGetValue(TriggerAnnotation, out var value) &&
                        value is {{G}}StableId trigger ? triggers[signals.Canonicalize(trigger)] : -1;
                }
                {{S}}ITransitionNode?[] transitions = [{{transitionBehaviors}}];
                Features = [.. global::System.Linq.Enumerable.Distinct(global::System.Linq.Enumerable.Concat(
                    global::System.Linq.Enumerable.SelectMany(states, static s => s?.RequiredFeatures ?? []),
                    global::System.Linq.Enumerable.SelectMany(transitions, static t => t?.RequiredFeatures ?? [])))];
                current = layout.Add<int>();
                time = layout.Add<double>();
                raised = layout.AddArray<byte>(triggers.Count);
            }

            public global::System.Type[] Features { get; }

            public void Initialize({{Ctx}})
            {
                graph.BeginEvaluation(b, h);
                b.Get(h, current) = {{machine.Initial + 1}};
                b.Get(h, time) = 0;
                OnEnter({{Args}}, {{machine.Initial}});
            }

            public bool Raise({{G}}InstanceBuffer b, {{G}}InstanceHandle h, {{G}}StableId signal)
            {
                if (!triggers.TryGetValue(signals.Canonicalize(signal), out var index))
                {
                    return false;
                }
                b.GetSpan(h, raised)[index] = 1;
                return true;
            }

            public void Observe({{G}}InstanceBuffer b, {{G}}InstanceHandle h, global::System.Collections.Generic.ICollection<{{G}}GraphObservation> observations)
            {
                var state = b.Get(h, current) - 1;
                if (state >= 0)
                {
                    observations.Add(new(EntryNode, graph.nodes[StateNodes[state]].Id));
                }
            }

            bool Raised({{G}}InstanceBuffer b, {{G}}InstanceHandle h, int transition) =>
                transitionTriggers[transition] is var trigger && (trigger < 0 || b.GetSpan(h, raised)[trigger] != 0);

            public void Tick({{Ctx}}, double delta)
            {
                if (b.Get(h, current) == 0)
                {
                    Initialize({{Args}});
                }
                else
                {
                    graph.BeginEvaluation(b, h);
                }
                b.Get(h, time) += delta;
                switch (b.Get(h, current) - 1)
                {
            """);
        for (var s = 0; s < states.Count; s++)
        {
            text.AppendLine($"case {s}:");
            for (var t = machine.FirstTransition[s]; t < machine.FirstTransition[s + 1]; t++)
            {
                var (node, target) = machine.Transitions[t];
                List<string> conditions = [];
                if (node.HasTrigger)
                {
                    conditions.Add($"Raised(b, h, {t})");
                }
                if (node.Transition == TransitionKind.Guarded)
                {
                    var condition = Enumerable.Range(node.FirstPort, node.PortCount)
                        .First(i => plan.Ports[i].Key == ConditionKey);
                    conditions.Add($"graph.DataRead<bool>({Args}, {condition})");
                }
                else if (node.Transition == TransitionKind.Declared)
                {
                    conditions.Add($"transition{t}.ShouldFire(new {S}TransitionContext(this, {Args}, {t}))");
                }
                var test = conditions.Count == 0 ? "true" : string.Join(" && ", conditions);
                text.AppendLine($"if ({test}) {{ Change({Args}, {s}, {target}); break; }}");
            }
            text.AppendLine("break;");
        }
        text.AppendLine($$"""
                }
                OnUpdate({{Args}}, b.Get(h, current) - 1);
                b.GetSpan(h, raised).Clear();
            }

            void Change({{Ctx}}, int from, int to)
            {
                OnExit({{Args}}, from);
                b.Get(h, current) = to + 1;
                b.Get(h, time) = 0;
                OnEnter({{Args}}, to);
            }
            """);
        foreach (var callback in new[] { "OnEnter", "OnExit", "OnUpdate" })
        {
            text.AppendLine($"void {callback}({Ctx}, int state)\n{{\nswitch (state)\n{{");
            foreach (var (_, i) in states.Select(static (s, i) => (s, i)).Where(static p => p.s.Owner is not null))
            {
                text.AppendLine($"case {i}: state{i}.{callback}(new {S}StateContext(this, {Args}, {i})); break;");
            }
            text.AppendLine("}\n}");
        }
        text.AppendLine($$"""
                protected override {{G}}GraphNode StateNode(int state) => graph.nodes[StateNodes[state]];
                protected override {{G}}GraphNode TransitionNode(int transition) => graph.nodes[TransitionNodes[transition]];
                protected override double ElapsedInState({{G}}InstanceBuffer b, {{G}}InstanceHandle h) => b.Get(h, time);
                protected override global::System.Span<byte> Memory({{G}}InstanceBuffer b, {{G}}InstanceHandle h, int state) =>
                    b.GetRecord(h).Slice(memoryOffsets[state], memorySizes[state]);
                protected override T ReadState<T>({{Ctx}}, int state, int port) =>
                    graph.DataRead<T>({{Args}}, Flat(FirstPorts[StateNodes[state]] + port));
                protected override void WriteState<T>({{G}}InstanceBuffer b, {{G}}InstanceHandle h, int state, int port, T value) =>
                    graph.Publish(b, h, Flat(FirstPorts[StateNodes[state]] + port), value);
                protected override T ReadTransition<T>({{Ctx}}, int transition, int port) =>
                    graph.DataRead<T>({{Args}}, Flat(FirstPorts[TransitionNodes[transition]] + port));
                protected override bool HasTrigger(int transition) => transitionTriggers[transition] >= 0;
                protected override bool IsRaised({{G}}InstanceBuffer b, {{G}}InstanceHandle h, {{G}}StableId trigger) =>
                    triggers.TryGetValue(signals.Canonicalize(trigger), out var index) && b.GetSpan(h, raised)[index] != 0;
                int Flat(int flat) => graph.HasData(flat) ? flat
                    : throw new global::System.InvalidOperationException("The port carries no data, or the program has no data plane.");
            }
            """);
    }

    static void Backend(StringBuilder text, string name) =>
        text.AppendLine($$"""
            /// <summary>A backend compiling only this asset; other assets and changed topologies report MG6004.</summary>
            public static {{G}}IGraphBackend Backend { get; } = new GeneratedBackend();

            sealed class GeneratedBackend : {{G}}IGraphBackend
            {
                public string Name => "generated {{name}}";
                public global::System.Threading.Tasks.ValueTask<{{G}}CompileResult> CompileAsync({{G}}GraphSource source,
                    global::System.IProgress<{{G}}EvaluationProgress>? progress = null, global::System.Threading.CancellationToken cancellation = default)
                {
                    cancellation.ThrowIfCancellationRequested();
                    {{G}}CompileResult result;
                    try
                    {
                        result = new(new @{{name}}(source.Document), []);
                    }
                    catch (global::System.InvalidOperationException exception)
                    {
                        result = new(null, [new {{G}}Diagnostic("MG6004", {{G}}DiagnosticSeverity.Error, null, exception.Message)]);
                    }
                    progress?.Report(new(1, "compiled"));
                    return global::System.Threading.Tasks.ValueTask.FromResult(result);
                }
            }
            """);

    static void Describe(StringBuilder text) =>
        text.AppendLine($$"""
            static string Describe({{G}}GraphDocument document)
            {
                var text = new global::System.Text.StringBuilder();
                foreach (var variable in global::System.Linq.Enumerable.OrderBy(document.Variables, static v => v.Id))
                {
                    text.Append("V ").Append(variable.Id.ToString()).Append(' ').Append(variable.TypeId.ToString()).Append(' ')
                        .Append(variable.Scope switch
                        {
                            {{G}}VariableScope.Input => "input",
                            {{G}}VariableScope.Output => "output",
                            {{G}}VariableScope.External => "external",
                            _ => "local",
                        }).Append('\n');
                }
                var priority = {{Definition.Identity("4d415353-3447-5241-5048-000000000222")}};
                var trigger = {{Definition.Identity("4d415353-3447-5241-5048-000000000221")}};
                foreach (var node in global::System.Linq.Enumerable.OrderBy(document.Nodes, static n => n.Id))
                {
                    text.Append("N ").Append(node.Id.ToString()).Append(' ').Append(node.TypeId.ToString()).Append('\n');
                    foreach (var port in node.Ports)
                    {
                        text.Append("P ").Append(port.Id.ToString()).Append(' ').Append(port.Declaration.Key.ToString()).Append(' ')
                            .Append(port.TypeId.ToString()).Append(port.Direction == {{G}}PortDirection.Output ? " output" : " input").Append('\n');
                    }
                    if (node.Payload is global::MASS4.Graph.Dataflow.VariableReference reference)
                    {
                        text.Append("R ").Append(reference.Variable.ToString()).Append('\n');
                    }
                    if (node.Annotations.TryGetValue(priority, out var value) && value is int rank && rank != 0)
                    {
                        text.Append("A ").Append(rank.ToString(global::System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                    }
                    if (node.Annotations.ContainsKey(trigger))
                    {
                        text.Append("T\n");
                    }
                }
                foreach (var edge in global::System.Linq.Enumerable.OrderBy(document.Edges, static e => e.Id))
                {
                    text.Append("E ").Append(edge.Id.ToString()).Append(' ').Append(edge.Source.ToString()).Append(' ')
                        .Append(edge.Target.ToString()).Append('\n');
                }
                return text.ToString();
            }
            """);

    static string Element(Guid id) => $"new {G}ElementId(new global::System.Guid(\"{Text(id)}\"))";
}
