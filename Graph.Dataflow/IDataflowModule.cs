namespace MASS4.Graph.Dataflow;

/// <summary>
/// A type module that also binds dataflow behaviors, so hosts composing several modules can bind them all into one
/// shared <see cref="DataflowSemantics"/>.
/// </summary>
public interface IDataflowModule : IGraphTypeModule
{
    /// <summary>Adds this module's value types and node behaviors to <paramref name="semantics"/>.</summary>
    /// <exception cref="ArgumentException">A value type or node behavior is already bound.</exception>
    void ConfigureDataflow(DataflowSemantics semantics);
}
