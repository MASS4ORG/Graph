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
var machine = GuardExample.Build(guard.CreateDataflow(), guard.CreateState(), states);
var compositor = new CompositorModule();
var frames = new GraphTypeRegistry().Add(compositor);
var composition = CompositionExample.Build(compositor.CreateDataflow(), frames);
Check(material.Document, materials, new(shader.CreateDataflow()));
Check(machine.Document, states, new(guard.CreateDataflow(), state: guard.CreateState()));
Check(composition.Document, frames, new(compositor.CreateDataflow()));
Console.WriteLine("Generated modules registered, persisted and compiled all three samples with reflection disabled.");

static void Check(GraphDocument document, GraphTypeRegistry registry, GraphInterpreter interpreter)
{
    var saved = GraphJson.Serialize(document);
    var loaded = GraphJson.Deserialize(saved, registry);
    if (loaded.Diagnostics.Count != 0 || interpreter.Compile(loaded.Document!).Graph is null)
    {
        throw new InvalidOperationException("Generated registration failed the AOT contract.");
    }
}
