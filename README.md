# AgentIsland for Windows (C# .NET 8 + WPF)

[English](README.md) | [简体中文](README_zh.md)

AgentIsland for Windows is a modern, ultra-responsive desktop Dynamic Island monitoring tool tailored for AI coding agents on Windows. Pinned to the top edge of your screen with minimal CPU and memory footprints, it visualizes real-time session activity, token consumption, remaining quota, balance, and usage statistics for Claude Code, OpenAI Codex, DeepSeek Harness, Google Antigravity, xAI Grok, and Cursor.

---

## 🌟 Upstream Origin & Evolution Background

* **Baseline Origin (v2.1.2)**:
  This project originally originated from upstream [Agent Island](https://github.com/agent-island/agent-island) at baseline **v2.1.2**. Upstream was structured as a multi-platform repository centered around a macOS (Swift) application with an early, incomplete Windows prototype (`windows/`).
* **Complete Reimplementation in Modern C#**:
  This repository completely decoupled the Windows implementation into an **independent, top-level .NET 8 C# solution**. Addressing legacy platform coupling, hardcoded enums, pervasive static singletons, lack of test coverage, and memory leak vulnerabilities found in early prototypes, we conducted an **end-to-end, multi-phase enterprise C# refactoring**, delivering a rock-solid, production-grade native Windows desktop experience.

---

## 🚀 Core Evolution & Architectural Milestones

Based on actual project developments and technical iterations, the repository has achieved several foundational milestones:

### 1. Modular Layer Decoupling
The codebase is structured into four distinct, loosely coupled layers:
* **`AgentIsland.Core`**: Cross-platform domain models, token calculation policies, capability contracts, and enum definitions.
* **`AgentIsland.Providers`**: Agent adapter plugins handling local session discovery, incremental transcript parsing, and quota/cost fetching.
* **`AgentIsland.Windows`**: Windows native infrastructure, handling cross-process path discovery, Cursor SQLite database queries, and Windows kernel memory APIs.
* **`AgentIsland`**: WPF presentation host featuring the Dynamic Island floating window (`IslandWindow`), settings and usage dashboard, system tray integration, and background hosted workers.

### 2. 10-Phase Enterprise C# Modernization
To establish an extensible, highly testable, and maintainable architecture, we completed a 10-phase transformation:
1. **Session Sensor Contracts**: Abstracted agent status sniffing into standard `ISessionSensor`, `IUsageFetcher`, and `ICostLedgerReader` contracts.
2. **Generic Host Orchestration**: Integrated `Microsoft.Extensions.Hosting` (`IHost`) for unified lifecycle management, accompanied by `AtomicJsonSettingsStorage`.
3. **MVVM Pattern**: Implemented CommunityToolkit-aligned ViewModels while preserving high-speed 60fps spring animation rendering pipelines.
4. **Modernized Test Suite & STA Runner**: Built a dedicated xUnit test project with `StaFact` dispatcher scheduling for reliable UI testing.
5. **100% Pure Dependency Injection**: Completely eradicated global static singletons (`_instance` / `Shared`), wiring all stores and services through `Microsoft.Extensions.DependencyInjection`.
6. **Provider Plugin Architecture**: Replaced hardcoded provider enums with `IAgentProvider` plugins and dynamic `IAgentCatalog` registration.
7. **Hosted Background Services**: Transitioned background polling to `PeriodicTimer`-driven `BackgroundService` workers (Activity Monitoring, Usage Polling, Cost Aggregation, and Update Checks).
8. **Window & Dialog Abstractions**: Extracted `IWindowService` and `IDialogService` to isolate presentation layers and enable headless testing.
9. **Strongly-Typed Options Pattern**: Implemented `IOptions<T>` and `IOptionsMonitor<T>` for reactive configuration reloads.
10. **Resilient Network Pipeline**: Configured `IHttpClientFactory` with Polly resilience handlers (exponential backoff, circuit breaker) and `INetworkConnectivityService` for fast-failing during offline states.

### 3. v1.0.1 Kernel-Level Memory Reclamation & Latency Optimizations
Fine-tuned for 24/7 background operation:
* **Windows Kernel Physical Memory Trimming**: Introduced `MemoryReclaimer`. After disabling intensive providers (like Codex) or clearing historical caches, an asynchronous 600ms debounced Gen 2 + LOH compaction executes, followed by a call to the Windows kernel API `SetProcessWorkingSetSize`. This immediately releases unreferenced physical memory pages back to the OS, keeping idle working sets firmly **under 100MB**.
* **Backward Stream Slicing & Memory Pooling**: `SessionScanner` uses `ArrayPool<byte>` and backward stream slicing when reading lengthy JSONL files to prevent Large Object Heap (LOH) fragmentation; `CodexMetaCache` and `LogParseCache` fingerprinting bypass redundant JSON deserialization for unchanged transcripts.
* **AABB Screen-Space Coarse Culling**: Performs rapid screen-space bounding box tests before calculating complex island geometry, reducing idle hover CPU overhead.

### 4. Real-World Concurrency Limit & 1-Year Worst-Case I/O Stress Testing
Engineered to withstand extreme workloads without compromising system responsiveness:
* **Strict Dual-Agent Concurrency Contract**: In practical programming workflows, developers realistically run at most 2 active concurrent agents (e.g., Claude + Codex running side-by-side). A `SemaphoreSlim(2, 2)` gate prevents disk I/O starvation during simultaneous scans.
* **1-Year Historical Dataset Simulation (Worst-Case I/O)**:
  - Generates 365 days of transcript history spanning 730+ dense session files;
  - **Cold Scan**: Full discovery and calculation of 1-year history completes in **< 30ms**;
  - **Warm Scan**: Incremental periodic polling completes in **< 1ms**;
  - CPU utilization remains **< 5%** during deep scans, with minimal memory impact.
* **One-Click Stress UI Launcher**: Run [`Launch-StressUI.bat`](Launch-StressUI.bat) or [`scripts/Launch-StressTestUI.ps1`](scripts/Launch-StressTestUI.ps1) to launch the app against an isolated 1-year sandbox fixture, automatically auto-expanding the Dynamic Island and opening the central Usage Dashboard.

### 5. Out-of-the-Box Fresh Install Defaults
Configured for maximum developer convenience upon first launch:
* **Quota Display**: Defaults to **Remaining Quota**;
* **Cost Tracking**: Defaults to **Off** (local privacy first);
* **Visual Appearance**: Defaults to **Follow Model** (automatically themes the island to the active agent);
* **Always Show Usage**: Defaults to **On** (persistent compact percentage metrics).

---

## 🧩 Supported AI Agents & Capability Matrix

| Agent Provider | Activity Monitoring | Token Statistics | Quota / Balance | Cost Ledger | Local Storage Location |
| :--- | :---: | :---: | :---: | :---: | :--- |
| **Claude Code** | ✅ Real-time | ✅ Full tracking | ✅ Official API | ✅ Ledger analysis | `%USERPROFILE%\.claude\projects` |
| **OpenAI Codex** | ✅ Status sensor | ✅ Full tracking | ✅ Session metadata | ✅ Ledger analysis | `%USERPROFILE%\.codex\sessions` |
| **DeepSeek Harness** | ✅ Multi-gateway | ✅ Event parser | ✅ Official Balance API | ✅ Cost calculation | `%USERPROFILE%\.dsh\sessions` (zstd streams) |
| **Google Antigravity**| ✅ Turn capture | ✅ Token counting | ➖ (Platform bound) | ➖ | `%USERPROFILE%\.antigravity` |
| **xAI Grok** | ✅ Sensor hooked | ✅ Token counting | ✅ Usage fetch | ➖ | `%USERPROFILE%\.grok` |
| **Cursor IDE** | ✅ Sensor hooked | ✅ Aggregated tokens | ✅ Quota status | ➖ | `%APPDATA%\Cursor\...\state.vscdb` (SQLite) |

> **Privacy & Security Commitment**: AgentIsland strictly follows a **Local-First** philosophy. Your source code, local transcripts, and prompt contents are never sent to external servers. Quota and balance requests only communicate directly with official provider APIs using credentials already present on your machine.

---

## 📂 Project Architecture

```text
AgentIsland-Csharp/
├─ src/
│  ├─ AgentIsland.Core/         # Domain models, AgentCatalog contracts, calculation rules
│  ├─ AgentIsland.Providers/    # 6 Agent adapter plugins, transcript parsers, registry
│  ├─ AgentIsland.Windows/      # Windows path resolution, SQLite reader, kernel memory manager
│  └─ AgentIsland/              # WPF presentation host, IslandWindow, ViewModels, dashboard
├─ tests/
│  └─ AgentIsland.Tests/        # 42 automated tests (Unit, STA UI, and 1-Year Stress tests)
├─ scripts/
│  ├─ Launch-StressTestUI.ps1   # 1-Year worst-case stress UI launch script
│  └─ Measure-ProcessResources.ps1 # Process resource profiling script (CPU / Working Set)
├─ Launch-StressUI.bat          # One-click stress testing batch launcher
├─ build.ps1                    # Self-contained executable build script
└─ AgentIsland.sln              # Visual Studio solution
```

---

## 🛠️ Building, Testing & Running

### Prerequisites
* Windows 10 / 11 x64
* [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Quick Commands

```powershell
# 1. Build the solution
dotnet build AgentIsland.sln

# 2. Run full test suite (All 42 tests passing)
dotnet test AgentIsland.sln

# 3. Launch 1-Year Stress Test UI (Inspect live Dynamic Island & Dashboard)
.\Launch-StressUI.bat

# 4. Package self-contained release executable
.\build.ps1 -Runtime win-x64 -Version 1.2.0
```

The compiled release artifact will be placed at `dist/AgentIsland-1.2.0-win-x64.zip`.

---

## 📄 License

This project is licensed under the same open-source terms as the upstream project. See the [LICENSE](LICENSE) file for details.
