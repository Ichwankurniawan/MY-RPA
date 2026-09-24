using System.Diagnostics.CodeAnalysis;
using MyRPA.Core.Activities;

namespace MyRPA.Workflow.Tests;

/// <summary>
/// Activity catalog for validation tests. Mirrors the shapes of a few built-in activities without referencing the
/// activity library (Workflow tests reference only MyRPA.Workflow).
/// </summary>
public sealed class TestCatalog : IActivityCatalog
{
    private readonly Dictionary<ActivityTypeName, ActivityDescriptor> _descriptors;

    public TestCatalog()
    {
        ActivityDescriptor[] all =
        [
            new(new("Core.Sequence"), "Sequence", "Test", allowsChildren: true),
            new(new("Core.Assign"), "Assign", "Test", properties:
            [
                new("to", ActivityPropertyKind.AssignmentTarget, isRequired: true),
                new("value", ActivityPropertyKind.Expression, isRequired: true),
            ]),
            new(new("Core.Log"), "Log", "Test", properties:
            [
                new("message", ActivityPropertyKind.Expression, isRequired: true),
                new("level", ActivityPropertyKind.Text, allowedValues: ["Information", "Warning"]),
            ]),
            new(new("Core.If"), "If", "Test",
                properties: [new("condition", ActivityPropertyKind.Expression, isRequired: true)],
                slots: [new("then", isRequired: true), new("else")]),
            new(new("Core.Switch"), "Switch", "Test",
                properties: [new("expression", ActivityPropertyKind.Expression, isRequired: true)],
                slots: [new("case:", isPrefix: true), new("default")]),
            new(new("Core.ForEach"), "ForEach", "Test",
                properties:
                [
                    new("items", ActivityPropertyKind.Expression, isRequired: true),
                    new("itemVariable", ActivityPropertyKind.LocalName, isRequired: true, scopeSlots: ["body"]),
                ],
                slots: [new("body", isRequired: true)]),
            new(new("Core.TryCatch"), "TryCatch", "Test",
                properties: [new("exceptionVariable", ActivityPropertyKind.LocalName, scopeSlots: ["catch"])],
                slots: [new("try", isRequired: true), new("catch"), new("finally")]),
            new(new("Core.InvokeWorkflow"), "InvokeWorkflow", "Test", properties:
            [
                new("workflow", ActivityPropertyKind.Text, isRequired: true),
                new("arguments", ActivityPropertyKind.ExpressionMap),
                new("outputs", ActivityPropertyKind.AssignmentTargetMap),
            ]),
        ];
        _descriptors = all.ToDictionary(d => d.TypeName);
        Descriptors = all;
    }

    public IReadOnlyList<ActivityDescriptor> Descriptors { get; }

    public bool TryGet(ActivityTypeName typeName, [NotNullWhen(true)] out ActivityDescriptor? descriptor) =>
        _descriptors.TryGetValue(typeName, out descriptor);
}
