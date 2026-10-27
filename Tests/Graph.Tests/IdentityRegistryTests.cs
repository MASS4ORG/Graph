namespace MASS4.Graph.Tests;

public class IdentityRegistryTests
{
    [Test]
    public async Task ResolveFollowsRenameChains()
    {
        var registry = new IdentityRegistry<PortType>();
        var current = new PortType(Id(3), "float");
        registry.Register(current);
        registry.AddRename(Id(1), Id(2));
        registry.AddRename(Id(2), Id(3));

        await Assert.That(registry.Canonicalize(Id(1))).IsEqualTo(Id(3));
        await Assert.That(registry.Resolve(Id(1))).IsSameReferenceAs(current);
        await Assert.That(registry.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RegisterRejectsEmptyDuplicateAndRenamedIdentities()
    {
        var registry = new IdentityRegistry<PortType>();
        registry.Register(new(Id(1), "a"));
        registry.AddRename(Id(2), Id(1));

        await Assert.That(() => registry.Register(new(StableId.Empty, "empty"))).ThrowsExactly<ArgumentException>();
        await Assert.That(() => registry.Register(new(Id(1), "again"))).ThrowsExactly<ArgumentException>();
        await Assert.That(() => registry.Register(new(Id(2), "renamed"))).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task AddRenameRejectsCyclesConflictsAndEmptyIdentities()
    {
        var registry = new IdentityRegistry<PortType>();
        registry.Register(new(Id(9), "registered"));
        registry.AddRename(Id(1), Id(2));

        await Assert.That(() => registry.AddRename(Id(2), Id(1))).ThrowsExactly<ArgumentException>();
        await Assert.That(() => registry.AddRename(Id(1), Id(3))).ThrowsExactly<ArgumentException>();
        await Assert.That(() => registry.AddRename(Id(9), Id(3))).ThrowsExactly<ArgumentException>();
        await Assert.That(() => registry.AddRename(StableId.Empty, Id(3))).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task ResolveThrowsForUnknownIdentity()
    {
        var registry = new IdentityRegistry<PortType>();

        await Assert.That(registry.TryResolve(Id(1), out _)).IsFalse();
        await Assert.That(() => registry.Resolve(Id(1))).ThrowsExactly<KeyNotFoundException>();
    }

    [Test]
    public async Task StableIdRoundTripsCanonicalText()
    {
        var id = StableId.New();

        await Assert.That(StableId.Parse(id.ToString())).IsEqualTo(id);
        await Assert.That(StableId.TryParse("not-a-guid", out _)).IsFalse();
        await Assert.That(StableId.TryParse(null, out _)).IsFalse();
        await Assert.That(StableId.Empty.IsEmpty).IsTrue();
        await Assert.That(ElementId.Parse(Id(5).ToString()).Value).IsEqualTo(Id(5).Value);
        await Assert.That(ElementId.TryParse("{bad}", out _)).IsFalse();
    }

    [Test]
    public async Task NodeTypeRejectsDuplicatePortKeys()
    {
        PortDeclaration[] ports =
        [
            PortDeclaration.Input(Id(1), "a", NumberId),
            PortDeclaration.Output(Id(1), "b", NumberId),
        ];

        await Assert.That(() => new NodeType(Id(10), "Broken", ports)).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task GraphTypeRegistryAddsModulesAndRejectsDuplicateMigrations()
    {
        var registry = new GraphTypeRegistry().Add(new SampleModule());
        var migration = new NodeMigration(Id(20), 1, static node => node);
        registry.AddMigration(migration);

        await Assert.That(registry.NodeTypes.TryResolve(Id(20), out _)).IsTrue();
        await Assert.That(registry.HostFunctions.Resolve(Id(21)).ParameterTypes.Count).IsEqualTo(2);
        await Assert.That(registry.TryGetMigration(Id(20), 1, out var found)).IsTrue();
        await Assert.That(found).IsSameReferenceAs(migration);
        await Assert.That(() => registry.AddMigration(migration)).ThrowsExactly<ArgumentException>();
    }

    sealed class SampleModule : IGraphTypeModule
    {
        public void Register(GraphTypeRegistry registry)
        {
            registry.NodeTypes.Register(new(Id(20), "Sample", []));
            registry.HostFunctions.Register(new(Id(21), "Clamp", [NumberId, NumberId], NumberId));
        }
    }
}
