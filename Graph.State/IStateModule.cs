namespace MASS4.Graph.State;

/// <summary>
/// A type module that also binds state roles, so hosts composing several modules can bind them all into one shared
/// <see cref="StateSemantics"/>.
/// </summary>
public interface IStateModule : IGraphTypeModule
{
    /// <summary>Adds this module's state and transition behaviors to <paramref name="semantics"/>.</summary>
    /// <exception cref="ArgumentException">A node type already has a state role.</exception>
    void ConfigureState(StateSemantics semantics);
}
