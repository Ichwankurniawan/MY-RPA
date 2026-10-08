namespace MyRPA.Core.Activities;

/// <summary>How the engine runs a node's ordered <c>children</c> list (ADR-0037).</summary>
public enum ActivityChildLayout
{
    /// <summary>The children are a list the activity runs as it chooses (for example in order, like <c>Core.Sequence</c>).</summary>
    List = 0,

    /// <summary>
    /// The children are the steps of a graph: the first child is the start step, and each step's <c>transitions</c>
    /// name the sibling to run next (for example <c>Core.Flowchart</c>). Requires a children list.
    /// </summary>
    Graph = 1,
}
