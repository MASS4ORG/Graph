namespace MASS4.Graph.Control;

/// <summary>
/// A type module that also binds control behaviors, so hosts composing several modules can bind them all into one
/// shared <see cref="ControlSemantics"/>.
/// </summary>
public interface IControlModule : IGraphTypeModule
{
    /// <summary>Adds this module's control node behaviors to <paramref name="semantics"/>.</summary>
    /// <exception cref="ArgumentException">A node type already has a control behavior.</exception>
    void ConfigureControl(ControlSemantics semantics);
}
