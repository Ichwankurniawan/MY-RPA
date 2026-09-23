# ADR-0002: Own workflow engine over a versioned JSON workflow model (no WF4/XAML/VB)

- Status: Accepted
- Date: 2026-09-23
- Phase: 1 (model skeleton); Phase 2 implements serialization, validation and execution

## Context
OpenRPA hosts Windows Workflow Foundation 4 with XAML and compiled VB expressions (`OpenRPA/Workflow.cs:535`,
`OpenRPA/WorkflowInstance.cs:226`), reaches into WF4 private fields via reflection (N7), and cannot run on .NET 10 (D1).
The PRD requires one workflow model for CLI, Studio, Robot, Orchestrator and AI generation (PRD 7.3, 8.3), in JSON
(PRD 2.6), with schema migrations (PRD 10.6).

## Decision
1. MyRPA defines its own immutable workflow model in `MyRPA.Workflow`; there is exactly one model for all clients.
2. The model carries **two versions**: `SchemaVersion` (file-format version, drives migrations) and `Version`
   (the author's content version of the workflow).
3. Nodes reference activity types by **registered name** (`ActivityTypeName`, e.g. `Core.Log`), never by CLR type
   name. Resolution goes through an explicit `IActivityCatalog`.
4. The model has no dependency on XAML, WF4, VB expressions, WPF designer metadata, or any serializer. Phase 2 adds a
   `System.Text.Json`-based versioned serializer without changing the model's dependency set.
5. Phase 1 ships only the skeleton: `WorkflowDefinition` (Id, Name, Version, SchemaVersion, Root) and `NodeDefinition`
   (Id, Type, DisplayName, Children). Arguments, variables, properties and validation belong to Phase 2.

## Alternatives
- CoreWF (community WF4 port) — keeps XAML/VB and WF4 semantics; conflicts with the JSON and AI-generation goals.
- Elsa or another engine — large dependency with its own model; would fight "one model" ownership. Revisit only by ADR.

## Consequences
- MyRPA owns engine correctness; Phase 2 must test it thoroughly.
- The expression language for JSON workflows is an open Phase 2 decision (security vs. power).
