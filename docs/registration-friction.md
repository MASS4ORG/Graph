# Hand-registration friction log

The contract samples register their node types by hand on purpose. Every point of friction is recorded here; together they are the requirements for source-generated registration (#11). When the generator lands, its output for each sample is differential-tested against the hand-written registration: identical stable identities, rename entries included.

Each entry names the sample that surfaced it, what happened, and what the generator (or, where marked, the core) must do.

## From the shader sample (#21)

### R1 — Every identity is a hand-written GUID

Port types, node types and every port key need an explicit `StableId`. The sample uses a private `Id(n)` helper with a per-library GUID prefix to keep this manageable. Nothing detects a collision with another library, and a copy-pasted node type keeps its source's identity until registration throws at run time.

Requirement: identities are declared once, next to the type, and checked at build time for duplicates within the assembly. They are never derived from names, so renames stay safe.

### R2 — Behaviors address ports by declaration index

`Lerp` writes `c.Write(3, …)` because its output is the fourth declared port. Inserting or reordering a port silently rewires every behavior, and reading a colour port as `float` fails only at evaluation.

Requirement: generated, typed per-node accessors (for example `inputs.T`, `outputs.Out = …`) or index constants derived from the declaration, so port order and types are checked by the compiler.

### R3 — One node, several registration surfaces

A node needs its `NodeType` registered in `GraphTypeRegistry` and its behavior bound in `DataflowSemantics`; a conversion node needs a third call (`AddConversionNode`). A port type is registered twice: as a `PortType` and as a dataflow value type with a CLR type that must agree. A mismatch only fails at run time.

Requirement: one declaration emits every registration consistently, as one `IGraphTypeModule` plus the semantics bindings.

### R4 — Serializers are written twice

Each port type needs a value serializer for variable defaults, and each payload type a payload serializer. A payload that wraps one value (`ScalarPayload`) duplicates its value serializer.

Requirement: generated, reflection-free payload serializers that reuse the value serializer of single-value payloads.

### R5 — Port keys must be public constants

Document builders wire by port key, so every key is a public member. To keep the identity count down the sample shares generic keys (`AKey`, `BKey`) across unrelated node types, which couples them.

Requirement: generated per-node key members (`Lerp.Keys.T`), each a distinct identity.

### R6 — No default payload

`new AddNodeCommand(ShaderLibrary.ScalarConstant)` without a payload leaves it null, and the behavior's `(ScalarPayload)c.Payload!` throws at evaluation, far from the cause.

Requirement: node declarations carry a default payload; the generator emits it (core candidate: a default-payload factory on `NodeType` used by `AddNodeCommand`).

### R7 — Every consumer rewrites the same builder helpers

The sample and every test project reimplement `Add`, `Connect` and `Require` over `AddNodeCommand` and `PortId(key)`. Wiring mistakes surface as command diagnostics at run time.

Requirement: a generated typed builder that returns typed port handles, so connecting a colour output to a scalar input does not compile unless a conversion exists.

### R8 — No declared graph output (core)

The renderer must be handed the albedo input port of the material output node; the document has no notion of the outputs a host reads.

Requirement (core, not generator): a declared output role, for example output-scope blackboard variables that a set-variable node writes, so hosts read outputs by variable identity.
