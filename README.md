# AgentIsland for Windows (C# .NET 8 + WPF)

[English](README.md) | [简体中文](README_zh.md)

[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat&logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-0078D6?style=flat&logo=windows)](https://www.microsoft.com/windows)
[![Tests](https://img.shields.io/badge/Tests-56%20Passing-brightgreen?style=flat&logo=githubactions)](tests/AgentIsland.Tests)
[![Memory Footprint](https://img.shields.io/badge/Working%20Set-~100MB%20(down%20from%20200MB+)-success?style=flat)](docs/performance.md)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**AgentIsland for Windows** is a high-performance, ultra-low-footprint desktop Dynamic Island monitoring tool tailored for AI coding agents on Windows. Pinned to the top edge of your screen with fluid 60fps spring animations, it visualizes real-time session activity, token consumption, remaining quota, and turn status for Claude Code, OpenAI Codex, DeepSeek Harness, Google Antigravity, xAI Grok, and Cursor.

---

## 🌟 Upstream Origin & Independent Modernization

* **Upstream Baseline (v2.1.2)**:  
  This project originally originated from the upstream open-source project [Agent Island](https://github.com/agent-island/agent-island) at baseline **v2.1.2**. Upstream was primarily architected around a macOS (Swift) application, with an early, incomplete Windows prototype located in a subfolder (`windows/`).
* **Why an Independent Reimplementation in Modern C#?**  
  In real-world 24/7 background usage on Windows, the early prototype exhibited noticeable engineering bottlenecks: pervasive global static singletons, hardcoded provider enums, absence of automated testing, Large Object Heap (LOH) fragmentation from naive log reading, and **physical memory ballooning well beyond 200MB+** over extended sessions.  
  This repository completely decoupled the Windows client into an **independent, top-level .NET 8 C# solution**, delivering a ground-up modernization engineered specifically for the Windows desktop environment with minimal resource consumption, high reliability, and a capability-driven provider architecture.

---

## 🧩 Supported Agent Matrix & Windows Integration

AgentIsland strictly adheres to a **Local-First** privacy commitment: session state sniffing and token parsing occur entirely on your local machine. Your source code, prompts, and chat transcripts **are never uploaded to any third-party servers**. Quota queries communicate directly with official provider APIs using credentials already present on your machine.

| Agent Provider | Activity Monitoring | Token Statistics | Quota / Balance | Cost Ledger | Windows Local Discovery Mechanism |
| :--- | :---: | :---: | :---: | :---: | :--- |
| **Claude Code** | ✅ Real-time | ✅ Full tracking | ✅ Official API | ✅ Ledger analysis | `%USERPROFILE%\.claude\projects` |
| **OpenAI Codex** | ✅ Status sensor | ✅ Full tracking | ✅ Session metadata | ✅ Ledger analysis | `%USERPROFILE%\.codex\sessions` |
| **DeepSeek Harness** | ✅ Multi-gateway | ✅ Event parser | ✅ Official Balance API | ✅ Cost calculation | `%USERPROFILE%\.dsh\sessions` (zstd streams) |
| **Google Antigravity**| ✅ Turn capture | ✅ Token counting | ✅ Dedicated fetcher | ✅ Ledger analysis | `%USERPROFILE%\.gemini` (IDE & CLI transcripts) |
| **xAI Grok** | ✅ Sensor hooked | ✅ Token counting | ✅ Usage fetch | ✅ Ledger analysis | `%USERPROFILE%\.grok` |
| **Cursor IDE** | ✅ Sensor hooked | ✅ Aggregated tokens | ✅ Quota status | ✅ Ledger analysis | `%APPDATA%\Cursor\...\state.vscdb` (SQLite WAL) |

---

## 🔌 Architectural Modernization: Extensibility & Adding New Agents

The legacy prototype relied on rigid enums (`enum TriggerTool`) and cascading `switch-case` statements across numerous modules, while erroneously assuming every agent conforms to an identical "5-hour quota + 7-day period" structure.

Our modernization transformed this into a flexible, **Capability-Driven** provider architecture. Built-in providers are registered in source and expose only the capability contracts they support; dynamic DLL loading is intentionally not part of the current design:

### 1. Fine-Grained Capability Flags (`AgentCapabilities`)
Different agents provide different levels of integration without forced coupling:
- `AgentCapabilities.Activity`: Session status sniffing & smart turn alerts (Working / Your Turn / Idle)
- `AgentCapabilities.Usage`: Official reset cycles, quotas, and account balances
- `AgentCapabilities.Cost`: Local historical token ledger parsing and expense aggregation
- `AgentCapabilities.SessionNavigation`: Jump directly to the active terminal process or working directory

### 2. Standardized Adapter Contracts
- **`ISessionSensor`**: Scans local session files and evaluates current turn status;
- **`IUsageFetcher`**: Retrieves official quotas and balances on-demand;
- **`ICostLedgerReader`**: Reads and incrementally parses local billing ledgers.

### 3. Registering Providers and Adding New Agent Keys
The sketch below shows capability implementation and registration. A completely new key also requires updating the fixed runtime mappings: `DisplayProvider`, `DisplayProviders.Parse/All`, visibility and slot selection, and (for activity monitoring) `TriggerTool` and its conversions. Add the corresponding UI name/logo and navigation mapping where supported. Registration alone does not enable an unknown key: current coordinators filter it out. Test the full enabled-provider pipeline as well as parsing.

```csharp
// Step 1: Implement one provider and only the capabilities it supports
public sealed class DoubaoProvider : IAgentProvider, ISessionSensor, ICostLedgerReader
{
    public AgentDescriptor Descriptor { get; } = new(
        new AgentKey("doubao"), "Doubao MarsCode",
        AgentCapabilities.Activity | AgentCapabilities.Cost,
        "marscode");

    public ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(...) { ... }
    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(...) { ... }
}

// Step 2: Register the provider in the WPF composition root
services.AddSingleton<IAgentProvider, DoubaoProvider>();

// Step 3: Register the matching descriptor/module in BuiltInAgentCatalog
catalog.Register(new BuiltInAgentModule(
    new AgentDescriptor(new AgentKey("doubao"), "Doubao MarsCode",
        AgentCapabilities.Activity | AgentCapabilities.Cost, "marscode")));

// Step 4: Extend runtime/selection/UI mappings described above and test end-to-end
```

---

## ⚡ Windows-Native 24/7 Low-Footprint Performance Tuning

As a desktop utility designed to run continuously in the background, minimizing system footprint is a paramount goal:

### 1. Win32 Kernel Physical Memory Trimming (From 200MB+ Down to ~100MB)
* **The Problem**: The original Windows prototype frequently bloated past **200MB+** of physical working set under long-running sessions or when parsing dense logs from agents like Codex.
* **The Solution**: We introduced `MemoryReclaimer`, an asynchronous debounced memory collector. When a high-overhead provider is disabled, historical caches are purged, or the application enters an idle state, it triggers Gen 2 + LOH compaction, followed by a direct call to the Win32 kernel API `SetProcessWorkingSetSize` to immediately release unreferenced physical memory pages back to Windows.
* **The Result**: Idle physical working set drops significantly from **200MB+** and remains firmly stabilized at **around 100MB**.

### 2. Backward Stream Slicing & Memory Pooling (LOH Zero Allocation)
* **`ArrayPool<byte>` Slicing**: Reads only trailing log lines via backward stream slicing, preventing allocations larger than 85KB from ever entering the Large Object Heap (LOH) and eliminating heap fragmentation.
* **Two-Tier Fingerprint Caching**: `CodexMetaCache` and `LogParseCache` skip deserialization when file mtimes and sizes are unchanged, achieving **zero redundant parsing** and reducing warm polling cycles to **< 1ms**.

### 3. Dual-Agent Concurrency Limit & 1-Year Worst-Case Stress Benchmark
* **Strict Concurrency Contract**: In typical intensive workflows, developers rarely run more than 2 active concurrent agents (e.g. Claude + Codex). A `SemaphoreSlim(2, 2)` gate prevents background discovery from saturating disk I/O.
* **1-Year (365-Day) Worst-Case I/O Benchmark**:
  - Validated against a fixture containing 365 days and 730+ dense historical session files;
  - **Cold Scan**: Full historical discovery completes in **< 30ms**;
  - **Warm Scan**: Incremental periodic polling completes in **< 1ms**;
  - CPU delta remains **< 5%** throughout scans, preserving uninterrupted 60fps UI fluidity.
* **One-Click Stress UI Launcher**: Run [`Launch-StressUI.bat`](Launch-StressUI.bat) or [`scripts/Launch-StressTestUI.ps1`](scripts/Launch-StressTestUI.ps1) to launch the app against an isolated 1-year sandbox fixture, automatically auto-expanding the Dynamic Island and central dashboard.

---

## 🏗️ Modern Enterprise C# Standards

This codebase strictly adheres to modern .NET design standards:

* **DI-first composition**: Business Stores no longer expose the legacy `.Shared` singleton pattern. The WPF host registers the runtime graph through `Microsoft.Extensions.DependencyInjection`; a small number of static platform helpers and UI compatibility accessors remain intentionally.
* **Generic Host Orchestration**: Leverages `Microsoft.Extensions.Hosting` to manage the application lifecycle, with all background polling executed via `PeriodicTimer`-driven hosted workers (`ActivityMonitoringWorker`, `UsagePollingWorker`, `CostAggregationWorker`, `UpdateCheckWorker`).
* **Resilient Network Pipeline & Fast-Fail**: Combines `IHttpClientFactory` with **Polly** resilience policies (exponential backoff, circuit breaking) and `INetworkConnectivityService` for instant fast-failing during offline states without freezing UI or worker threads.
* **Strongly-Typed Options Pattern**: Utilizes `IOptions<T>` and `IOptionsMonitor<T>` for reactive settings reloads, paired with `AtomicJsonSettingsStorage` for thread-safe file persistence.
* **Service Abstraction**: Extracted `IWindowService` and `IDialogService`, isolating ViewModels from direct WPF window dependencies to ensure testability.

---

## 🧪 Automated Testing & Quality Assurance

The repository includes a comprehensive, deterministic test suite covering core domain logic, concurrency safety, and STA UI dispatcher interactions:

* **56 Automated Tests Passing (ALL GREEN)** — 55 regular tests plus 1 stress/resource test;
* **Coverage Scope**: Domain calculators, reverse stream slicers, circuit breaker faults, MVVM ViewModels, STA UI rendering, and 1-year worst-case stress benchmarks;
* **Sandboxed Test Isolation**: Tests execute in isolated temporary data directories, preventing interference with running application instances.

Run all tests via:
```powershell
dotnet test AgentIsland.sln
```

---

## 🛠️ Getting Started & Build Instructions

### 1. End Users (Portable Standalone Executable)
1. Navigate to the **[Releases](../../releases)** tab;
2. Download the latest `AgentIsland-x.x.x-win-x64.zip`;
3. Extract anywhere and launch `AgentIsland.exe` directly (Self-contained, no .NET SDK required).

### 2. Developers (Build from Source)

#### Prerequisites
* Windows 10 / 11 x64
* [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

#### Common Commands

```powershell
# 1. Restore and build solution
dotnet build AgentIsland.sln

# 2. Run full test suite (56 tests: 55 regular + 1 stress/resource)
dotnet test AgentIsland.sln

# 3. Launch 1-Year Stress Test UI
.\Launch-StressUI.bat

# 4. Package self-contained release executable
.\build.ps1 -Runtime win-x64 -Version 1.2.1
```

The compiled release artifact will be output to `dist/AgentIsland-1.2.1-win-x64.zip`.

---

## 📂 Solution Structure

```text
AgentIsland-Csharp/
├─ src/
│  ├─ AgentIsland.Core/         # Cross-platform models, AgentCapabilities, calculation policies
│  ├─ AgentIsland.Providers/    # Provider payload parsers, built-in catalog descriptors
│  ├─ AgentIsland.Windows/      # Windows path resolution, winsqlite3 driver, kernel memory APIs
│  └─ AgentIsland/              # WPF presentation host, IslandWindow, ViewModels, dashboard
├─ tests/
│  └─ AgentIsland.Tests/        # 56 automated tests (55 regular + 1 stress/resource test)
├─ scripts/
│  ├─ Launch-StressTestUI.ps1   # 1-Year worst-case stress launcher script
│  └─ Measure-ProcessResources.ps1 # Process CPU and working set profiling script
├─ Launch-StressUI.bat          # One-click stress testing batch launcher
├─ build.ps1                    # Self-contained packaging build script
└─ AgentIsland.sln              # Visual Studio solution
```

---

## 📄 License & Acknowledgements

* This project is licensed under the same open-source terms as the upstream project. See the [LICENSE](LICENSE) file for details.
* Heartfelt thanks to the original upstream project [Agent Island](https://github.com/agent-island/agent-island) and its creators for their pioneering vision of the Dynamic Island interaction model for AI coding agents.
