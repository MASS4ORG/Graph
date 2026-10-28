namespace MASS4.Graph.Dataflow;

/// <summary>Validates a document and lays out its ports, sources and cache slots as a program.</summary>
static class DataflowCompiler
{
    public static DataflowProgram? Compile(
        DataflowSemantics semantics,
        GraphDocument document,
        InstanceLayoutBuilder layout,
        DiagnosticBag diagnostics)
    {
        var local = new DiagnosticBag();
        IGraphRule[] rules = [new PortCompatibilityRule(semantics.Conversions), .. semantics.Rules];
        foreach (var diagnostic in GraphValidator.Validate(document, rules))
        {
            local.Add(diagnostic);
        }
        var builder = new Builder(semantics, document, layout, local);
        builder.LayOutPorts();
        foreach (var diagnostic in local)
        {
            diagnostics.Add(diagnostic);
        }
        return local.HasErrors ? null : builder.Build();
    }

    sealed class Builder
    {
        readonly DataflowSemantics semantics;
        readonly GraphDocument document;
        readonly InstanceLayoutBuilder layout;
        readonly DiagnosticBag diagnostics;
        readonly Dictionary<ElementId, StableId> inferred;
        readonly GraphNode[] graphNodes;
        readonly Dictionary<ElementId, int> flatPorts = [];
        readonly int[] firstPorts;
        readonly DataflowValueType?[] types;
        readonly PortEntry[] ports;

        public Builder(
            DataflowSemantics semantics,
            GraphDocument document,
            InstanceLayoutBuilder layout,
            DiagnosticBag diagnostics)
        {
            this.semantics = semantics;
            this.document = document;
            this.layout = layout;
            this.diagnostics = diagnostics;
            inferred = TypeInference.Run(semantics, document).Resolved;
            graphNodes = [.. document.Nodes.OrderBy(static node => node.Id)];
            firstPorts = new int[graphNodes.Length];
            var count = 0;
            for (var i = 0; i < graphNodes.Length; i++)
            {
                firstPorts[i] = count;
                foreach (var port in graphNodes[i].Ports)
                {
                    flatPorts.Add(port.Id, count++);
                }
            }
            types = new DataflowValueType?[count];
            ports = new PortEntry[count];
        }

        readonly Dictionary<ElementId, VariableSlot> variables = [];

        public void LayOutPorts()
        {
            foreach (var variable in document.Variables.OrderBy(static variable => variable.Id))
            {
                if (semantics.TryGetValueType(variable.TypeId, out var type))
                {
                    variables.Add(variable.Id, new(type.Allocate(layout), variable.DefaultValue));
                }
            }
            ForEachPort((node, port, flat) =>
            {
                types[flat] = ValueTypeOf(port);
                var location = port.Direction == PortDirection.Output ? OutputLocation(node, flat) : null;
                ports[flat] = new(node, port.Direction == PortDirection.Output, location, -1, null, 0);
            });
            ForEachPort((node, port, flat) =>
            {
                if (port.Direction == PortDirection.Input && types[flat] is { } type)
                {
                    ports[flat] = LayOutInput(node, port, flat, type);
                }
            });
        }

        public DataflowProgram Build()
        {
            List<int> sources = [];
            var nodes = new NodeEntry[graphNodes.Length];
            for (var i = 0; i < graphNodes.Length; i++)
            {
                var first = sources.Count;
                for (var p = 0; p < graphNodes[i].Ports.Count; p++)
                {
                    var source = ports[firstPorts[i] + p].SourceNode;
                    if (source >= 0 && !sources.Skip(first).Contains(source))
                    {
                        sources.Add(source);
                    }
                }
                semantics.TryGetBehavior(graphNodes[i].TypeId, out var behavior);
                nodes[i] = new(graphNodes[i], behavior, firstPorts[i], first, sources.Count - first, false);
            }
            MarkStatic(nodes, sources);
            var slots = new ProgramSlots(layout.Add<int>(), layout.AddArray<int>(nodes.Length), layout.Add<byte>());
            return new(document, nodes, ports, [.. sources], slots, variables);
        }

        /// <summary>Variable reads alias their variable's slot; other outputs get slots of their own.</summary>
        ValueLocation? OutputLocation(int node, int flat) =>
            DataflowNodes.TryGetVariable(document, graphNodes[node], out var variable) &&
            variables.TryGetValue(variable.Id, out var slot)
                ? slot.Location
                : types[flat]?.Allocate(layout);

        void ForEachPort(Action<int, GraphPort, int> action)
        {
            for (var i = 0; i < graphNodes.Length; i++)
            {
                for (var p = 0; p < graphNodes[i].Ports.Count; p++)
                {
                    action(i, graphNodes[i].Ports[p], firstPorts[i] + p);
                }
            }
        }

        DataflowValueType? ValueTypeOf(GraphPort port)
        {
            var typeId = inferred.TryGetValue(port.Id, out var resolved) ? resolved : port.TypeId;
            return semantics.TryGetValueType(typeId, out var type) ? type : null;
        }

        PortEntry LayOutInput(int node, GraphPort port, int flat, DataflowValueType type)
        {
            var edge = document.GetEdges(port.Id)
                .FirstOrDefault(edge => semantics.IsDataEdge(document, edge, out _, out _));
            var source = edge is null ? -1 : flatPorts[edge.Source];
            if (source < 0 || ports[source].Location is not { } sourceLocation)
            {
                return new(node, false, type.Allocate(layout), -1, null, 0);
            }
            var sourceNode = ports[source].Node;
            var sourceType = sourceLocation.Type;
            if (sourceType.PortTypeId == type.PortTypeId ||
                (sourceType.ClrType == type.ClrType && sourceType.Storage == type.Storage))
            {
                return new(node, false, sourceLocation, sourceNode, null, 0);
            }
            if (semantics.TryGetCoercion(sourceType.PortTypeId, type.PortTypeId, out var coercion))
            {
                return new(node, false, type.Allocate(layout), sourceNode, coercion, sourceLocation.Offset);
            }
            diagnostics.Error(DataflowDiagnosticIds.CoercionMissing, edge!.Id,
                $"No coercion converts {sourceType.ClrType.Name} values to {type.ClrType.Name}.");
            return ports[flat];
        }

        /// <summary>Marks nodes cacheable per instance: not volatile and fed only by such nodes.</summary>
        static void MarkStatic(NodeEntry[] nodes, List<int> sources)
        {
            var pending = nodes.Select(static node => node.SourceCount).ToArray();
            var dependents = Dependents(nodes, sources);
            var ready = new Queue<int>(Enumerable.Range(0, nodes.Length).Where(node => pending[node] == 0));
            while (ready.TryDequeue(out var node))
            {
                var entry = nodes[node];
                var fedByStatic = sources.Skip(entry.FirstSource).Take(entry.SourceCount).All(s => nodes[s].IsStatic);
                nodes[node] = entry with { IsStatic = entry.Behavior is { IsVolatile: false } && fedByStatic };
                foreach (var dependent in dependents[node].Where(dependent => --pending[dependent] == 0))
                {
                    ready.Enqueue(dependent);
                }
            }
        }

        static List<int>[] Dependents(NodeEntry[] nodes, List<int> sources)
        {
            var dependents = nodes.Select(static _ => new List<int>()).ToArray();
            for (var i = 0; i < nodes.Length; i++)
            {
                for (var s = 0; s < nodes[i].SourceCount; s++)
                {
                    dependents[sources[nodes[i].FirstSource + s]].Add(i);
                }
            }
            return dependents;
        }
    }
}
