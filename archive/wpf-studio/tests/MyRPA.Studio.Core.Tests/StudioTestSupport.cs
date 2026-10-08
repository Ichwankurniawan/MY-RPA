using Microsoft.Extensions.DependencyInjection;
using MyRPA.Activities;
using MyRPA.Core.Activities;
using MyRPA.Studio.Documents;

namespace MyRPA.Studio.Tests;

/// <summary>The real built-in activity catalog (Core.*), as Studio sees it.</summary>
public static class Catalogs
{
    public static IActivityCatalog BuiltIn { get; } =
        new ServiceCollection().AddLogging().AddMyRpaActivities().BuildServiceProvider().GetRequiredService<IActivityCatalog>();
}

public static class RepositoryPaths
{
    public static string Root { get; } = Find();

    public static string Samples => Path.Combine(Root, "samples");

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MyRPA.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("MyRPA.sln not found.");
    }
}

public static class Drafts
{
    /// <summary>A small workflow: Sequence [ Assign, If { then: Log } ] with one argument and one variable.</summary>
    public static WorkflowDraft Sample() => DraftJson.Read("""
        { "schemaVersion": "1.0", "id": "sample", "name": "Sample", "version": "1.0.0",
          "arguments": [ { "name": "who", "direction": "In", "type": "String", "default": "World" },
                         { "name": "result", "direction": "Out", "type": "String" } ],
          "variables": [ { "name": "count", "type": "Int", "default": 0 } ],
          "root": { "id": "main", "type": "Core.Sequence", "children": [
            { "id": "set", "type": "Core.Assign", "properties": { "to": "result", "value": "'Hello, ' + who" } },
            { "id": "check", "type": "Core.If", "properties": { "condition": "count == 0" },
              "slots": { "then": { "id": "say", "type": "Core.Log", "properties": { "message": "result" } } } } ] } }
        """);
}
