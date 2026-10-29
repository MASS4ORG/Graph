using MASS4.Graph;
using MASS4.Graph.Interpreter;
using MASS4.Graph.Samples.Shader;
using MASS4.Graph.Samples.Fsm;
using MASS4.Graph.Samples.Compositor;
using MASS4.Graph.Samples.Shader.Generated;
using MASS4.Graph.Samples.Fsm.Generated;
using MASS4.Graph.Samples.Compositor.Generated;

var shader = new ShaderModule();
var materials = new GraphTypeRegistry().Add(shader);
var material = MaterialExample.Build(shader.CreateDataflow(), materials);
var guard = new GuardModule();
var states = new GraphTypeRegistry().Add(guard);
var machine = GeneratedGuardExample.Build(guard, states);
var compositor = new CompositorModule();
var frames = new GraphTypeRegistry().Add(compositor);
var composition = CompositionExample.Build(compositor.CreateDataflow(), frames);
Check(material.Document, materials, new(shader.CreateDataflow()));
Check(machine.Document, states, new(guard.CreateDataflow(), state: guard.CreateState()));
Check(composition.Document, frames, new(compositor.CreateDataflow()));
CheckTypedTrigger(machine, states, guard);
CheckCompiled("material.graph.json", materials, new(shader.CreateDataflow()), MaterialGraph.Backend,
    new EvaluationContext().Set(new Fragment()));
CheckCompiled("guard.graph.json", states, new(guard.CreateDataflow(), state: guard.CreateState()), GuardGraph.Backend,
    new EvaluationContext().Set(new MASS4.Graph.State.StateTime { DeltaSeconds = .1 }));
CheckCompiled("composition.graph.json", frames, new(compositor.CreateDataflow()), CompositionGraph.Backend,
    new EvaluationContext().Set(new Timeline { Seconds = .5 }).Set(new RenderSettings { Width = 4, Height = 3 }));
Console.WriteLine("Generated modules and backends registered, persisted, compiled and ran all three samples with reflection disabled.");

static void Check(GraphDocument document, GraphTypeRegistry registry, GraphInterpreter interpreter)
{
    var saved = GraphJson.Serialize(document);
    var loaded = GraphJson.Deserialize(saved, registry);
    if (loaded.Diagnostics.Count != 0 || interpreter.Compile(loaded.Document!).Graph is null)
    {
        throw new InvalidOperationException("Generated registration failed the AOT contract.");
    }
}

static void CheckTypedTrigger(GuardExample example, GraphTypeRegistry registry, GuardModule module)
{
    var loaded = GraphJson.Deserialize(GraphJson.Serialize(example.Document), registry).Document!;
    var graph = new GraphInterpreter(module.CreateDataflow(), state: module.CreateState()).Compile(loaded).Graph!;
    var instances = new InstanceBuffer(graph.InstanceLayout);
    var instance = instances.Allocate();
    var context = new EvaluationContext().Set(new MASS4.Graph.State.StateTime { DeltaSeconds = .1 });
    graph.Initialize(context, instances, instance);
    var raised = SeePlayer.Raise(graph, instances, instance);
    var evaluation = graph.EvaluateAsync(context, instances, instance);
    if (!raised || !evaluation.IsCompleted || ((InterpretedGraph)graph).StateMachine!.CurrentState(instances, instance).Id != example.Chase)
    {
        throw new InvalidOperationException("Typed triggers failed the AOT contract.");
    }
    evaluation.GetAwaiter().GetResult();
}

static void CheckCompiled(string asset, GraphTypeRegistry registry, GraphInterpreter interpreter, IGraphBackend backend,
    EvaluationContext context)
{
    var document = GraphJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", asset)), registry).Document!;
    var expected = Run(interpreter.Compile(document).Graph!, context);
    var compiled = backend.CompileAsync(new(document));
    var actual = compiled.IsCompleted ? Run(compiled.Result.Graph!, context) : [];
    if (expected.Count == 0 || expected.Count != actual.Count ||
        expected.Zip(actual).Any(static pair => pair.First.Element != pair.Second.Element ||
            (pair.First.Value is not Frame && !Equals(pair.First.Value, pair.Second.Value))))
    {
        throw new InvalidOperationException($"The generated backend of {asset} diverged from the interpreter.");
    }
}

static List<GraphObservation> Run(ICompiledGraph graph, EvaluationContext context)
{
    var instances = new InstanceBuffer(graph.InstanceLayout);
    var instance = instances.Allocate();
    graph.Initialize(context, instances, instance);
    var evaluation = graph.EvaluateAsync(context, instances, instance);
    if (!evaluation.IsCompleted)
    {
        throw new InvalidOperationException("Backends evaluate synchronously.");
    }
    evaluation.GetAwaiter().GetResult();
    List<GraphObservation> observations = [];
    graph.Observe(context, instances, instance, observations);
    return observations;
}
