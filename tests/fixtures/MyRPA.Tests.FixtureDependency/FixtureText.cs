using System.Runtime.Loader;

namespace MyRPA.Tests.FixtureDependency;

/// <summary>Reports which load context this private dependency was loaded into.</summary>
public static class FixtureText
{
    /// <summary>The name of the load context that loaded this assembly.</summary>
    public static string LoadContextName() => AssemblyLoadContext.GetLoadContext(typeof(FixtureText).Assembly)?.Name ?? "(none)";
}
