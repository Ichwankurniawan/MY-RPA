# MyRPA — Phased Product Requirements Document

**Document Type:** Product Requirements Document
**Project:** MyRPA
**Status:** Planning
**Version:** 1.0
**Primary Reference:** OpenRPA
**Target Platform:** Windows initially, with future cross-platform/runtime expansion
**Primary Language:** C#
**Target Framework:** .NET 10

---

# 1. Product Vision

MyRPA is a modern, open-source Robotic Process Automation (RPA) platform inspired by the architecture and concepts of OpenRPA.

The goal is **not** to create a simple OpenRPA clone.

MyRPA should modernize the traditional RPA architecture by combining:

* Deterministic workflow automation
* Browser automation
* Windows automation
* API automation
* Python and JavaScript execution
* Visual workflow development
* Recording and selectors
* AI-assisted workflow development
* AI agents
* Model Context Protocol (MCP)
* Human-in-the-loop approvals
* Central orchestration
* Queues
* Scheduling
* Credentials
* RBAC
* Observability
* Package management
* CI/CD

The long-term goal is to create a platform where traditional RPA and modern AI automation operate within the same workflow ecosystem.

---

# 2. Product Philosophy

MyRPA follows five core principles.

## 2.1 Deterministic RPA Provides Reliability

Traditional workflow activities should remain predictable.

Examples:

```text
Click
Type
Read Text
HTTP Request
Read File
Write File
Run Python
Run SQL
```

These activities should behave deterministically whenever possible.

---

## 2.2 AI Provides Flexibility

AI should be used where deterministic automation becomes difficult.

Examples:

```text
Understand unstructured email
Extract information from documents
Generate workflow logic
Analyze workflow failures
Choose between tools
Handle ambiguous web content
```

AI should augment deterministic automation rather than replace it.

---

## 2.3 MCP Provides Extensibility

MCP should allow MyRPA agents to consume external tools and capabilities without requiring every integration to be implemented directly inside MyRPA.

Example:

```text
MyRPA Agent
    │
    ├── Built-in Tools
    │
    └── MCP Servers
          ├── Database
          ├── GitHub
          ├── SaaS
          ├── Internal APIs
          └── Custom Enterprise Tools
```

---

## 2.4 Orchestration Provides Scale

Local automation should eventually be capable of being managed centrally.

```text
Orchestrator
    │
    ├── Robot 1
    ├── Robot 2
    ├── Robot 3
    └── Robot N
```

---

## 2.5 Human-in-the-Loop Provides Control

AI agents and automation should be able to pause when human authorization is required.

Example:

```text
AI Agent
    ↓
Prepare payment
    ↓
Approval required
    ↓
Human approval
    ↓
Continue
```

---

# 3. Product Goals

## 3.1 Primary Goals

MyRPA should provide:

1. A modern workflow execution engine
2. A visual workflow designer
3. A plugin architecture
4. Browser automation
5. Windows automation
6. API automation
7. File and data automation
8. Python and JavaScript integration
9. Recording and selector capabilities
10. AI-assisted workflow creation
11. AI agent capabilities
12. MCP integration
13. Human approval workflows
14. Central orchestration
15. Queue management
16. Scheduling
17. Enterprise security
18. Package management
19. Observability
20. CI/CD support

---

# 4. Non-Goals

MyRPA should not initially attempt to:

* Implement every automation technology
* Support every operating system
* Replace every existing RPA platform
* Build a custom browser engine
* Build a custom programming language
* Build its own LLM
* Implement AI execution without security controls
* Build enterprise orchestration before the local runtime is stable

The platform must grow incrementally.

---

# 5. Reference Architecture

The conceptual architecture is:

```text
                         MYRPA
                           │
          ┌────────────────┼────────────────┐
          │                │                │
       Studio           Runtime        Orchestrator
          │                │                │
     ┌────┴────┐      ┌────┼────┐      ┌────┼────┐
     │         │      │    │    │      │    │    │
 Designer   Recorder  Browser Windows API   Jobs Queues
     │         │      │    │    │      │    │    │
     └────┬────┘      └────┼────┘      └────┼────┘
          │                │                │
          └────────────────┼────────────────┘
                           │
                     Workflow Engine
                           │
              ┌────────────┼────────────┐
              │            │            │
             RPA           AI           MCP
              │            │            │
              │        AI Agent         │
              │            │            │
              └────────────┼────────────┘
                           │
                    Human Approval
```

---

# 6. High-Level Component Model

MyRPA should eventually consist of:

```text
MyRPA Studio
MyRPA Runtime
MyRPA Workflow Engine
MyRPA Activity SDK
MyRPA Plugin SDK
MyRPA Recorder
MyRPA Selector Engine
MyRPA Browser Provider
MyRPA Windows Provider
MyRPA AI
MyRPA Agent Runtime
MyRPA MCP Client
MyRPA Orchestrator
MyRPA Robot
MyRPA Queue System
MyRPA Identity/Security
MyRPA Package System
MyRPA Observability
```

These components should have clear boundaries.

---

# 7. Architectural Principles

## 7.1 Core Independence

The workflow core must not depend directly on:

* WPF
* Playwright
* Windows UI Automation
* Browser-specific implementation
* AI provider
* MCP implementation
* Database implementation
* Orchestrator implementation

Instead, use interfaces and dependency inversion.

---

## 7.2 Provider-Based Architecture

Example:

```text
Workflow Activity
       ↓
IBrowserProvider
       ↓
PlaywrightProvider
```

Future providers could include:

```text
IBrowserProvider
IWindowsAutomationProvider
IApiProvider
IOfficeProvider
IAiProvider
IMcpProvider
```

---

## 7.3 One Workflow Engine

The CLI, Studio, Robot, and Orchestrator must ultimately use the same workflow execution engine.

Do not create separate execution implementations.

```text
                 Workflow Model
                       │
              ┌────────┼────────┐
              │        │        │
             CLI     Studio    Robot
              │        │        │
              └────────┼────────┘
                       │
                Workflow Engine
```

---

# 8. Technology Direction

Initial technology choices:

| Area               | Technology                                           |
| ------------------ | ---------------------------------------------------- |
| Language           | C#                                                   |
| Framework          | .NET 10                                              |
| Studio             | WPF                                                  |
| Browser            | Playwright                                           |
| Windows Automation | Windows UI Automation / appropriate modern framework |
| API                | ASP.NET Core / HttpClient                            |
| Database           | SQLite initially                                     |
| Future Database    | PostgreSQL                                           |
| Logging            | Microsoft.Extensions.Logging                         |
| Telemetry          | OpenTelemetry                                        |
| Testing            | xUnit or equivalent                                  |
| Serialization      | JSON                                                 |
| Source Control     | Git                                                  |
| CI                 | GitHub Actions or equivalent                         |
| AI                 | Provider abstraction                                 |
| MCP                | MCP client architecture                              |

Technology choices may be revised when validated against implementation requirements.

---

# 9. Development Phases

The project is divided into:

```text
Phase 0  — OpenRPA Reverse Engineering
Phase 1  — MyRPA Foundation
Phase 2  — Workflow Engine
Phase 3  — Automation SDK & Plugin System
Phase 4  — Browser Automation
Phase 5  — MyRPA Studio
Phase 6  — Selectors & Recorder
Phase 7  — Enterprise Automation Activities
Phase 8  — AI Automation
Phase 9  — AI Agents & MCP
Phase 10 — Orchestrator
Phase 11 — Enterprise Security & Operations
Phase 12 — Production Ecosystem
```

Each phase must be completed and reviewed before moving to the next phase.

---

# PHASE 0 — OpenRPA Reverse Engineering

## Objective

Understand OpenRPA deeply before implementing MyRPA.

This phase is research only.

No MyRPA production implementation should depend on assumptions that have not been validated.

---

## 0.1 Study

Analyze:

* Solution structure
* Project dependencies
* Application startup
* Workflow runtime
* Workflow activities
* Workflow designer
* Plugin system
* Recording
* Selectors
* Automation elements
* Browser integration
* Windows automation
* Office automation
* Native messaging
* Detectors/events
* Work items
* OpenFlow integration
* Configuration
* Logging
* Package/dependency management

---

## 0.2 Required Documentation

Create:

```text
docs/research/openrpa-overview.md
docs/research/openrpa-projects.md
docs/research/openrpa-runtime.md
docs/research/openrpa-activities.md
docs/research/openrpa-plugins.md
docs/research/openrpa-recording.md
docs/research/openrpa-selectors.md
docs/research/openrpa-browser.md
docs/research/openrpa-windows.md
docs/research/openrpa-orchestration.md
docs/research/openrpa-analysis.md
```

---

## 0.3 Required Diagrams

Create Mermaid diagrams for:

1. Application startup
2. Workflow execution
3. Activity execution
4. Recording
5. Selector resolution
6. Browser automation
7. Windows automation
8. Plugin loading
9. Robot/orchestrator communication

---

## 0.4 Analysis

Document:

### Concepts Worth Retaining

Identify OpenRPA concepts that are architecturally valuable.

### Concepts That Should Be Redesigned

For each:

```text
OpenRPA approach
Problem / limitation
MyRPA direction
Reason
```

### Concepts That Should Not Be Copied

Identify implementation details that should not be carried into MyRPA.

### Modernization Opportunities

Analyze:

* Modern .NET
* Better dependency boundaries
* Playwright
* Provider abstractions
* Modern plugin architecture
* AI
* MCP
* API-first orchestration
* Observability
* Security
* Testing
* Package management

Do not design the complete MyRPA architecture during Phase 0.

---

## 0.5 Definition of Done

Claude must be able to trace:

```text
User clicks Run
        ↓
Workflow instance
        ↓
Activity
        ↓
Automation provider
        ↓
Automation element
        ↓
Result
```

using actual OpenRPA classes/interfaces.

---

# PHASE 1 — MyRPA Foundation

## Objective

Create the new MyRPA repository and establish the architecture.

---

## 1.1 Technology

* C#
* .NET 10
* Git
* Dependency Injection
* Structured logging
* Unit testing
* CI

WPF will be introduced when Studio development begins.

---

## 1.2 Initial Solution

```text
MyRPA.sln

src/
    MyRPA.Core
    MyRPA.Workflow
    MyRPA.Runtime
    MyRPA.Activities
    MyRPA.Storage
    MyRPA.Cli

tests/
    MyRPA.Core.Tests
    MyRPA.Workflow.Tests
    MyRPA.Runtime.Tests
    MyRPA.Activities.Tests
    MyRPA.Integration.Tests
```

The exact project structure may be adjusted after Phase 0 architectural findings.

---

## 1.3 Deliverables

Create:

* Solution
* Projects
* Test infrastructure
* CI
* `.editorconfig`
* `Directory.Build.props`
* `CLAUDE.md`
* README
* Architecture documentation

---

## 1.4 Architecture Rules

Core must not depend on:

* WPF
* Playwright
* Browser implementation
* Windows UI implementation
* AI
* MCP
* Database implementation

---

## 1.5 Definition of Done

```bash
dotnet build
```

passes.

```bash
dotnet test
```

passes.

No circular project dependencies.

---

# PHASE 2 — Workflow Engine

## Objective

Build the deterministic workflow runtime.

This is the foundation of the platform.

---

## 2.1 Core Concepts

Implement:

```text
Workflow
Activity
ActivityContext
ExecutionContext
WorkflowRunner
ActivityResult
WorkflowExecution
```

---

## 2.2 Control Flow

Implement:

* Sequence
* Assign
* Log
* Delay
* If
* Switch
* While
* DoWhile
* ForEach
* TryCatch
* Throw
* InvokeWorkflow

---

## 2.3 Variables

Support:

* String
* Int
* Decimal
* Boolean
* DateTime
* Object
* List
* Dictionary

---

## 2.4 Arguments

Support:

* In
* Out
* InOut

---

## 2.5 Runtime

Support:

* Cancellation
* Timeout
* Exception propagation
* Execution IDs
* Structured logging

---

## 2.6 Serialization

Create a versioned JSON workflow format.

Example:

```json
{
  "name": "HelloWorld",
  "version": "1.0.0",
  "activities": [
    {
      "type": "Log",
      "message": "Hello World"
    }
  ]
}
```

---

## 2.7 CLI

Implement:

```bash
myrpa run workflow.json
myrpa validate workflow.json
```

---

## 2.8 Definition of Done

The execution path must work:

```text
JSON
 ↓
Loader
 ↓
Validator
 ↓
WorkflowRunner
 ↓
Activities
 ↓
Result
```

with automated tests.

---

# PHASE 3 — Automation SDK & Plugin System

## Objective

Create the abstraction layer between the workflow engine and automation technologies.

---

## 3.1 Interfaces

Design appropriate abstractions for:

```text
IPlugin
IPluginRegistry
IAutomationProvider
IAutomationElement
ISelector
ISelectorResolver
IRecorderPlugin
ITrigger
```

Exact naming can change if architectural review identifies better names.

---

## 3.2 Plugin Lifecycle

```text
Discover
 ↓
Load
 ↓
Initialize
 ↓
Register
 ↓
Execute
 ↓
Dispose
```

---

## 3.3 Plugin Categories

Prepare extension points for:

```text
Browser
Windows
API
Office
Python
JavaScript
AI
MCP
Storage
Triggers
```

Do not implement all categories yet.

---

## 3.4 Definition of Done

Create a sample plugin outside the Core project.

The sample plugin must:

* Discover
* Load
* Initialize
* Register itself
* Expose a test activity/provider
* Execute successfully
* Dispose correctly

---

# PHASE 4 — Browser Automation

## Objective

Implement the first real automation provider.

---

## 4.1 Technology

Use Playwright.

---

## 4.2 Architecture

```text
Workflow Activity
        ↓
IBrowserProvider
        ↓
PlaywrightProvider
        ↓
Browser
        ↓
BrowserContext
        ↓
Page
```

---

## 4.3 Activities

Implement:

* Open Browser
* Navigate
* Click
* Type Text
* Get Text
* Get Attribute
* Wait for Element
* Select Option
* Upload File
* Download File
* Close Browser

---

## 4.4 Browser Sessions

Support reusable sessions.

```text
BrowserSession
 ├── Browser
 ├── Context
 ├── Page
 └── Metadata
```

---

## 4.5 Errors

Support:

* Element not found
* Timeout
* Browser unavailable
* Navigation failure
* Invalid selector

---

## 4.6 Definition of Done

This must work:

```text
Open browser
 ↓
Navigate to test site
 ↓
Find element
 ↓
Click
 ↓
Type
 ↓
Read result
 ↓
Close browser
```

---

# PHASE 5 — MyRPA Studio

## Objective

Create the visual workflow designer.

---

## 5.1 Technology

WPF.

---

## 5.2 Initial UI

```text
┌──────────────────────────────────────────────────────────────┐
│ File Edit View Debug                         Run             │
├──────────────┬───────────────────────────────┬───────────────┤
│ Activities   │                               │ Properties    │
│              │       Workflow Designer       │               │
│ Control Flow │                               │ Selected      │
│ Browser      │                               │ Activity      │
│ Windows      │                               │               │
│ API          │                               │               │
│ Data         │                               │               │
├──────────────┴───────────────────────────────┴───────────────┤
│ Variables | Arguments | Output | Logs | Errors               │
└──────────────────────────────────────────────────────────────┘
```

---

## 5.3 Requirements

Support:

* Drag activity
* Drop activity
* Activity nesting
* Activity properties
* Variables
* Arguments
* Save
* Open
* Undo
* Redo
* Delete
* Copy/paste
* Run

---

## 5.4 Critical Architecture Rule

The designer must consume the same workflow model used by CLI/runtime.

There must be only one workflow execution engine.

---

## 5.5 Definition of Done

User can:

```text
Create workflow
 ↓
Add activities
 ↓
Configure properties
 ↓
Save
 ↓
Open
 ↓
Run
```

---

# PHASE 6 — Selectors & Recorder

## Objective

Make MyRPA capable of recording real user interactions.

---

## 6.1 Selector Strategies

Support:

* CSS
* XPath
* Text
* Role
* Accessibility
* Attributes
* Automation ID

---

## 6.2 Selector Resolution

```text
Selector
 ↓
Provider
 ↓
Candidate elements
 ↓
Match
 ↓
Automation Element
```

---

## 6.3 Browser Recorder

Capture:

* Click
* Type
* Navigate
* Select
* Upload
* Download

---

## 6.4 Workflow Generation

Example:

```text
User:

Click Login
Type username
Type password
Click Submit
```

Recorder produces:

```text
Browser.Click
Browser.Type
Browser.Type
Browser.Click
```

---

## 6.5 Important Rule

Do not rely only on coordinates.

Prefer semantic selectors.

---

## 6.6 Definition of Done

A user can record a simple website interaction and insert the generated activities into Studio.

---

# PHASE 7 — Enterprise Automation Activities

## Objective

Expand beyond browser automation.

---

## 7.1 Windows Automation

Implement:

* Open Application
* Close Application
* Find Element
* Click
* Type
* Get Text
* Wait
* Keyboard
* Mouse

---

## 7.2 File Automation

Implement:

* Read
* Write
* Copy
* Move
* Delete
* Directory operations

---

## 7.3 API Automation

Implement:

* HTTP Request
* REST
* Headers
* Authentication
* JSON parsing
* JSON transformation

---

## 7.4 Data

Implement:

* CSV
* JSON
* XML
* DataTable

---

## 7.5 Office

Eventually support:

* Excel
* Word
* Outlook

---

## 7.6 Python

Implement:

```text
Run Python
```

with:

* Input arguments
* Output
* Timeout
* Error handling
* Environment selection

---

## 7.7 JavaScript

Implement:

```text
Run JavaScript
```

---

## 7.8 Definition of Done

A realistic business process can combine:

```text
Email/API
 ↓
File
 ↓
Excel
 ↓
Browser
 ↓
API
```

inside one workflow.

---

# PHASE 8 — AI Automation

## Objective

Introduce AI without compromising deterministic execution.

---

## 8.1 AI Provider

Create:

```text
IAiProvider
```

Support future providers:

* OpenAI
* Anthropic
* Local models
* Other compatible providers

---

## 8.2 AI Assistant

Studio assistant capabilities:

### Explain Workflow

Example:

```text
Explain what this workflow does.
```

### Generate Activity

Example:

```text
Create an activity that extracts the invoice number.
```

### Generate Workflow

Example:

```text
Download today's invoices and save them to the invoice folder.
```

### Debug

Example:

```text
Why did this workflow fail?
```

---

## 8.3 AI Workflow Builder

Architecture:

```text
Natural Language
 ↓
AI
 ↓
Structured Workflow
 ↓
Schema Validation
 ↓
Policy Validation
 ↓
Human Review
 ↓
Workflow
```

---

## 8.4 Critical Rule

AI must NOT directly execute arbitrary actions.

AI-generated workflows must pass validation before execution.

---

# PHASE 9 — AI Agents & MCP

## Objective

Allow AI agents to operate inside deterministic workflows.

---

## 9.1 Agent Activity

Properties:

```text
Goal
Model
Instructions
Tools
Max Iterations
Timeout
Approval Policy
```

---

## 9.2 Tool System

Create:

```text
ITool
```

Implement:

```text
BrowserTool
FileTool
HttpTool
PythonTool
WorkflowTool
```

---

## 9.3 MCP

Implement:

```text
MCP Client
MCP Tool Discovery
MCP Tool Execution
MCP Permissions
```

---

## 9.4 Architecture

```text
Workflow
 ↓
AI Agent
 ↓
Tool Registry
 ├── Browser
 ├── Files
 ├── HTTP
 ├── Python
 └── MCP
```

---

## 9.5 Human-in-the-Loop

Sensitive operations should support approval:

```text
AI Agent
 ↓
Sensitive action
 ↓
Approval required
 ↓
Human
 ↓
Approve / Reject
 ↓
Continue / Stop
```

---

## 9.6 Definition of Done

A workflow can combine:

```text
Deterministic Activities
        +
AI Agent
        +
Tools
        +
MCP
        +
Human Approval
```

---

# PHASE 10 — Orchestrator

## Objective

Move from desktop RPA to a managed automation platform.

---

## 10.1 Components

```text
MyRPA Orchestrator
        │
 ┌──────┼─────────┐
 │      │         │
Robots Jobs     Queues
 │      │         │
 └──────┼─────────┘
        │
     Workflows
```

---

## 10.2 Robot States

Support:

* Offline
* Available
* Busy
* Paused
* Error
* Disabled

---

## 10.3 Jobs

Support:

* Create
* Start
* Stop
* Cancel
* Retry
* Monitor

---

## 10.4 Queues

Support:

* Queue creation
* Queue item
* Priority
* Retry
* Status
* Multiple robots

---

## 10.5 Scheduling

Support:

* Once
* Interval
* Cron
* Event-based

---

## 10.6 Definition of Done

A central server can:

```text
Publish workflow
 ↓
Create job
 ↓
Assign robot
 ↓
Robot executes
 ↓
Report result
 ↓
Store execution history
```

---

# PHASE 11 — Enterprise Security & Operations

## Objective

Make the platform suitable for serious organizational use.

---

## 11.1 Identity

Support:

* Users
* Organizations
* Projects
* Environments

---

## 11.2 RBAC

Initial roles:

```text
Administrator
Developer
Operator
Viewer
```

---

## 11.3 Credentials

Use credential references.

Never store raw passwords inside workflows.

---

## 11.4 Audit

Track:

* Login
* Workflow changes
* Publishing
* Execution
* Credential access
* Permission changes
* Approvals

---

## 11.5 Observability

Metrics:

* Execution count
* Success rate
* Failure rate
* Duration
* Queue latency
* Robot utilization

---

## 11.6 OpenTelemetry

Prepare the runtime and orchestrator for OpenTelemetry.

---

## 11.7 Security

Implement:

* Secret protection
* Permission enforcement
* Audit logging
* Package validation
* AI tool permissions
* Domain allowlists
* Execution policies

---

# PHASE 12 — Production Ecosystem

## Objective

Turn MyRPA into a mature platform and ecosystem.

---

## 12.1 Package Management

Support:

```text
Package
Version
Dependencies
Activities
Plugins
```

---

## 12.2 Package Registry

Support:

* Local registry
* Private registry
* Public registry

---

## 12.3 CI/CD

Enable:

```text
Git
 ↓
Build
 ↓
Test
 ↓
Package
 ↓
Publish
 ↓
Deploy
```

---

## 12.4 Environments

Support:

```text
Development
Testing
Production
```

---

## 12.5 Workflow Promotion

```text
Development
      ↓
Testing
      ↓
Production
```

---

## 12.6 Deployment

Support:

* Local
* VM
* Windows Server
* Containerized orchestrator components
* Cloud deployment

---

# 10. Cross-Phase Requirements

These requirements apply to every phase.

---

## 10.1 Architecture

Core must remain independent from:

* UI
* Browser provider
* AI provider
* Database
* Orchestrator

---

## 10.2 Testing

Every feature should include appropriate:

* Unit tests
* Integration tests
* End-to-end tests

The required test level depends on the feature.

---

## 10.3 Documentation

Every phase must update:

```text
docs/
```

with the current architecture and implementation decisions.

---

## 10.4 Security

Every feature must consider:

* Sensitive data
* Permissions
* Attack surface
* Logging risks
* Credential handling
* External communication

---

## 10.5 Observability

Runtime features should provide appropriate structured logging.

---

## 10.6 Workflow Versioning

Once workflow formats are published, schema migrations must be supported.

---

## 10.7 Dependency Management

Dependencies should be explicit and isolated.

Avoid unnecessary coupling between projects.

---

# 11. Phase Dependencies

```text
                    Phase 0
               OpenRPA Research
                      │
                      ▼
                    Phase 1
                  Foundation
                      │
                      ▼
                    Phase 2
                Workflow Engine
                      │
                      ▼
                    Phase 3
                Automation SDK
                      │
                      ▼
                    Phase 4
                Browser Engine
                      │
                      ▼
                    Phase 5
                    Studio
                      │
                      ▼
                    Phase 6
              Recorder + Selectors
                      │
                      ▼
                    Phase 7
             Enterprise Activities
                      │
                      ▼
                    Phase 8
                  AI Platform
                      │
                      ▼
                    Phase 9
                Agents + MCP
                      │
                      ▼
                   Phase 10
                 Orchestrator
                      │
                      ▼
                   Phase 11
             Enterprise Security
                      │
                      ▼
                   Phase 12
             Production Ecosystem
```

Some work can happen in parallel after the required interfaces are stable.

However, Claude must not introduce dependencies on future components prematurely.

---

# 12. Phase Completion Rule

Claude must **never automatically proceed to the next phase**.

At the end of every phase, produce:

```text
PHASE COMPLETION REPORT

Phase:
Status:

Implemented:
- ...

Files changed:
- ...

Architecture:
- ...

Tests:
- ...

Build:
PASS / FAIL

Test:
PASS / FAIL

Known issues:
- ...

Technical debt:
- ...

Documentation:
- ...

Recommended next phase:
- ...
```

The user must explicitly authorize the next phase.

---

# 13. First Implementation Order

The development sequence is:

```text
1. Phase 0
   Analyze OpenRPA

2. Phase 1
   Create MyRPA architecture

3. Phase 2
   Build workflow engine

4. Verify CLI

5. Phase 3
   Build plugin/automation abstraction

6. Phase 4
   Build Playwright browser automation

7. Verify real automation

8. Phase 5
   Build Studio

9. Phase 6
   Build recorder/selectors

10. Phase 7
    Expand automation

11. Phase 8
    Add AI

12. Phase 9
    Add agents/MCP

13. Phase 10
    Add orchestrator

14. Phase 11
    Harden security/operations

15. Phase 12
    Build ecosystem
```

---

# 14. MVP Definition

The first usable MyRPA MVP consists of:

```text
Phase 1
+
Phase 2
+
Phase 3
+
Phase 4
+
Basic Phase 5
```

---

## 14.1 MVP User Journey

A user should be able to:

```text
Open Studio
     ↓
Create Workflow
     ↓
Open Browser
     ↓
Navigate
     ↓
Click
     ↓
Type
     ↓
Read Data
     ↓
Close Browser
     ↓
Run
     ↓
View Logs
```

---

# 15. MVP Success Criteria

The MVP is successful when a developer can:

1. Open MyRPA Studio
2. Create a workflow
3. Add a browser activity
4. Configure the activity
5. Navigate to a website
6. Interact with the website
7. Read data
8. Save the workflow
9. Reload the workflow
10. Execute it
11. See structured logs
12. Handle a basic failure
13. Run the same workflow through the runtime/CLI

The Studio and CLI must use the same underlying workflow model and execution engine.

---

# 16. Long-Term Product Architecture

The final platform should enable:

```text
                         MYRPA
                           │
          ┌────────────────┼────────────────┐
          │                │                │
        Studio           Runtime        Orchestrator
          │                │                │
     ┌────┼────┐      ┌────┼────┐      ┌────┼────┐
     │    │    │      │    │    │      │    │    │
 Recorder Designer  Browser Windows API  Robots Jobs Queues
     │    │    │      │    │    │      │    │    │
     └────┼────┘      └────┼────┘      └────┼────┘
          │                │                │
          └────────────────┼────────────────┘
                           │
                    Workflow Engine
                           │
                  ┌────────┼────────┐
                  │        │        │
                 RPA       AI       MCP
                  │        │        │
                  │    AI Agents    │
                  │        │        │
                  └────────┼────────┘
                           │
                    Human Approval
```

---

# 17. AI Architecture Direction

AI should exist at multiple levels.

## Level 1 — AI Assistant

Used by developers.

```text
Developer
    ↓
AI Assistant
    ↓
Explain / Generate / Debug
```

---

## Level 2 — AI Workflow Generator

```text
Natural Language
       ↓
AI
       ↓
Workflow Definition
       ↓
Validation
       ↓
Human Review
       ↓
Workflow
```

---

## Level 3 — AI Agent

```text
Workflow
   ↓
Agent
   ↓
Tools
   ↓
Observation
   ↓
Reasoning
   ↓
Action
```

---

## Level 4 — MCP Agent

```text
Agent
  │
  ├── MyRPA Tools
  │
  └── MCP Tools
```

All levels must respect security and execution policies.

---

# 18. Security Architecture Direction

Security must be designed into the platform rather than added at the end.

Important areas:

```text
Identity
    ↓
Authentication
    ↓
Authorization
    ↓
RBAC
    ↓
Credential Management
    ↓
Audit
    ↓
Execution Policies
    ↓
AI Tool Permissions
    ↓
MCP Permissions
```

Sensitive operations should support explicit policies.

Examples:

```text
File deletion
Credential access
External HTTP calls
Financial actions
Production deployment
Database modification
```

---

# 19. AI Safety / Execution Policy

AI-generated actions should be subject to policy.

Example:

```text
AI Agent
    ↓
Tool Request
    ↓
Policy Engine
    │
    ├── Allowed → Execute
    │
    ├── Denied → Reject
    │
    └── Approval Required
             ↓
           Human
```

The agent should not be able to bypass the policy layer.

---

# 20. Observability Architecture

The platform should eventually provide:

```text
Workflow Execution
       │
       ├── Execution ID
       ├── Robot ID
       ├── Workflow ID
       ├── Start Time
       ├── End Time
       ├── Status
       ├── Activity Logs
       ├── Errors
       └── Metrics
```

Potential future telemetry:

```text
Logs
Metrics
Traces
```

using OpenTelemetry-compatible architecture.

---

# 21. Plugin Ecosystem

The long-term plugin system should allow third-party developers to create:

```text
Browser Plugins
Windows Plugins
Office Plugins
API Plugins
Database Plugins
AI Plugins
MCP Plugins
Storage Plugins
Trigger Plugins
Custom Activities
Custom Providers
```

Plugins should not require modifications to the core runtime.

---

# 22. Package Ecosystem

Eventually workflows should be distributable as packages.

Example:

```text
InvoiceAutomation
 ├── Workflow
 ├── Activities
 ├── Dependencies
 ├── Configuration
 └── Version
```

Packages should support:

* Versioning
* Dependencies
* Validation
* Publishing
* Installation
* Updates
* Rollback

---

# 23. Enterprise Deployment Model

Long-term deployment:

```text
                   Organization
                        │
                  MyRPA Orchestrator
                        │
          ┌─────────────┼─────────────┐
          │             │             │
       Project A     Project B     Project C
          │             │             │
       Robots        Robots        Robots
          │             │             │
      Workflows     Workflows     Workflows
```

Each organization/project/environment should have controlled access.

---

# 24. Environment Model

Support:

```text
Development
     ↓
Testing
     ↓
Production
```

Each environment should have:

* Separate configuration
* Separate credentials
* Separate permissions
* Separate execution targets
* Deployment history

---

# 25. Development Quality Standards

All implementation should follow:

* Clean architecture principles where appropriate
* SOLID principles where useful
* Dependency inversion
* Clear interfaces
* Small cohesive components
* Automated testing
* Structured logging
* Meaningful exceptions
* XML documentation for public APIs where useful
* Consistent naming
* Minimal unnecessary abstractions

Do not over-engineer simple features.

---

# 26. Code Quality Rules

Avoid:

* God classes
* Global mutable state
* Hidden dependencies
* Hardcoded credentials
* Hardcoded environment URLs
* Duplicate workflow engines
* Provider-specific logic in Core
* UI logic in the runtime
* AI logic directly inside deterministic activities
* Excessive static state

---

# 27. Testing Strategy

Testing should exist at multiple levels.

## Unit Tests

Test:

* Workflow activities
* Serialization
* Validation
* Selectors
* Business logic
* Providers

## Integration Tests

Test:

* Browser provider
* Storage
* Plugin loading
* API communication
* Runtime integration

## End-to-End Tests

Test:

```text
Studio
 ↓
Workflow
 ↓
Runtime
 ↓
Browser
 ↓
Result
```

Later:

```text
Orchestrator
 ↓
Robot
 ↓
Workflow
 ↓
Result
```

---

# 28. Documentation Strategy

Documentation should include:

```text
README.md

docs/
├── architecture/
├── research/
├── development/
├── user-guide/
├── api/
├── security/
└── decisions/
```

Important architecture decisions should be recorded.

---

# 29. Architecture Decision Records

When a major architecture decision is made, create an ADR.

Example:

```text
docs/decisions/ADR-001-workflow-model.md
docs/decisions/ADR-002-browser-provider.md
docs/decisions/ADR-003-plugin-system.md
```

Each ADR should explain:

```text
Context
Decision
Alternatives
Consequences
```

---

# 30. OpenRPA Relationship

OpenRPA is a reference implementation for architectural research.

MyRPA should learn from concepts such as:

* Workflow-based automation
* Activities
* Plugins
* Selectors
* Automation elements
* Recording
* Provider separation
* Robot execution
* Orchestration

However:

**MyRPA should not blindly reproduce the OpenRPA implementation.**

The implementation should be redesigned where modern technology, security, maintainability, extensibility, or architecture requires it.

OpenRPA source code must be treated according to its applicable open-source license and project requirements.

---

# 31. Phase Isolation Rule

Each phase should introduce only the dependencies necessary for that phase.

For example:

Phase 1 should not introduce:

```text
Playwright
AI SDK
MCP SDK
Orchestrator API
WPF
```

unless specifically justified by the phase requirements.

Similarly, Phase 4 should not require the future AI architecture.

This keeps the architecture clean and testable.

---

# 32. No Premature Implementation

Claude must not implement features simply because they appear later in this PRD.

For example:

During Phase 2:

Do not implement:

* AI agents
* MCP
* Orchestrator
* Package registry
* Enterprise RBAC

During Phase 4:

Do not implement:

* AI agents
* MCP
* Orchestrator

unless explicitly required to validate an existing architectural boundary.

---

# 33. Phase Completion Report

At the end of every phase, produce:

```text
PHASE COMPLETION REPORT

Phase:
Status:

Implemented:
- ...

Files changed:
- ...

Architecture:
- ...

Tests:
- ...

Build:
PASS / FAIL

Test:
PASS / FAIL

Known issues:
- ...

Technical debt:
- ...

Documentation:
- ...

Architecture decisions:
- ...

Unresolved questions:
- ...

Recommended next phase:
- ...
```

Claude must stop after the report.

Do not automatically start the next phase.

---

# 34. User Authorization Rule

The user must explicitly authorize the next phase.

Valid example:

```text
Proceed to Phase 1.
```

or:

```text
Start Phase 2.
```

Until explicit authorization is given, Claude should remain in the current phase or wait for further instructions.

---

# 35. First Development Sequence

The intended sequence is:

```text
Phase 0
OpenRPA Reverse Engineering
        ↓
Review
        ↓
Phase 1
Foundation
        ↓
Review
        ↓
Phase 2
Workflow Engine
        ↓
Verify CLI
        ↓
Review
        ↓
Phase 3
Automation SDK
        ↓
Review
        ↓
Phase 4
Browser Automation
        ↓
Verify Real Automation
        ↓
Review
        ↓
Phase 5
Studio
        ↓
Review
        ↓
Phase 6
Recorder + Selectors
        ↓
Review
        ↓
Phase 7
Enterprise Activities
        ↓
Review
        ↓
Phase 8
AI
        ↓
Review
        ↓
Phase 9
Agents + MCP
        ↓
Review
        ↓
Phase 10
Orchestrator
        ↓
Review
        ↓
Phase 11
Security + Operations
        ↓
Review
        ↓
Phase 12
Production Ecosystem
```

---

# 36. MVP Roadmap

The MVP should be:

```text
Phase 1
+
Phase 2
+
Phase 3
+
Phase 4
+
Basic Phase 5
```

The MVP does not require:

* AI
* MCP
* Orchestrator
* Enterprise RBAC
* Package registry

Those belong to later phases.

---

# 37. MVP Example

A simple automation:

```text
Open MyRPA Studio
        ↓
Create Workflow
        ↓
Add Open Browser
        ↓
Add Navigate
        ↓
Add Click
        ↓
Add Type Text
        ↓
Add Get Text
        ↓
Add Close Browser
        ↓
Save Workflow
        ↓
Run Workflow
        ↓
View Logs
```

Example use case:

```text
Open website
 ↓
Navigate to login
 ↓
Enter username
 ↓
Enter password
 ↓
Click login
 ↓
Read dashboard value
 ↓
Log result
 ↓
Close browser
```

---

# 38. Long-Term Vision

The final MyRPA platform should allow a developer to create a workflow such as:

```text
Receive Email
      ↓
Read Attachment
      ↓
Extract Data
      ↓
Call API
      ↓
AI Agent
      ↓
Check Business Rules
      ↓
Browser Automation
      ↓
Update ERP
      ↓
Request Human Approval
      ↓
Continue
      ↓
Write Result
      ↓
Send Notification
      ↓
Store Audit Trail
```

The same platform should support:

```text
Deterministic Automation
        +
AI Automation
        +
Agentic Automation
        +
MCP
        +
Human-in-the-Loop
        +
Enterprise Orchestration
```

---

# 39. Final Product Principle

MyRPA should NOT become:

> "OpenRPA with a new UI."

It should become:

> **A modern, extensible RPA platform inspired by OpenRPA, combining deterministic automation, browser and desktop automation, AI agents, MCP, developer tooling, and enterprise orchestration.**

OpenRPA should be studied to understand proven RPA concepts.

Modern architecture should be used where appropriate.

Every phase should leave the project in a working and understandable state.

Architectural correctness should not be sacrificed merely to reach the next phase faster.

---

# 40. Final Success Definition

MyRPA succeeds when it becomes a platform where developers can build, execute, debug, deploy, and operate automation using:

```text
                    MYRPA
                      │
      ┌───────────────┼────────────────┐
      │               │                │
    Build           Execute          Manage
      │               │                │
    Studio          Runtime        Orchestrator
      │               │                │
    Recorder        Browser           Robots
    Designer        Windows           Jobs
    Debugger        API               Queues
                    Python            Schedules
                      │
                      ▼
                     AI
                      │
               ┌──────┴──────┐
               │             │
             Agent          MCP
               │             │
               └──────┬──────┘
                      │
                Human Approval
                      │
                      ▼
                 Audit / Logs
```

The core philosophy remains:

**Deterministic RPA provides reliability.**

**AI provides flexibility.**

**MCP provides extensibility.**

**Orchestration provides scale.**

**Human-in-the-loop provides control.**
