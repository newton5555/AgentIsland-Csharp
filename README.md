# AgentIsland for Windows

[English](README.md) | [简体中文](README_zh.md)

AgentIsland for Windows is a desktop floating-island monitoring tool for AI coding agents on Windows, built with C# (.NET 8 + WPF). It stays on your desktop to display real-time activity status, token consumption, quota limits, and usage analytics for Claude, Codex, DeepSeek Harness, Antigravity, Grok, and Cursor.

---

## Relationship with Upstream

* **Origin & Baseline**: This project is derived from [Agent Island](https://github.com/agent-island/agent-island) (baseline v2.1.2), which was structured as a multi-platform repository containing a primary macOS application and a `windows/` subproject directory.
* **Standalone Engineering**: This repository extracted the Windows implementation into an independent, top-level C# project, removing macOS-specific build configurations and legacy dependencies.
* **Independent Evolution**: Operating outside the upstream monorepo, this repository maintains its own build and release pipelines, continuously iterating on stability, extensibility, and resource efficiency specifically tailored to the Windows environment.

---

## What This Repository Adds & Updates

Building upon the standalone extraction, this project has implemented several key architectural refactorings and feature adaptations:

1. **Modular Layered Architecture**:
   - Split into `Core` (domain models and cross-platform business rules), `Providers` (agent adapters and parsers), `Windows` (system paths, low-level OS APIs, and memory management), and `AgentIsland` (WPF presentation and application host).
   - Introduced an explicit `AgentCatalog` capability registration mechanism to replace hardcoded enums. Agents declare support for usage, cost, activity, or navigation capabilities; unpopulated data is clearly designated as unsupported rather than populated with placeholder zeros.
2. **Agent Support & Alignment**:
   - Fully aligned session and log path discovery on Windows with upstream agents (Claude, Codex, Antigravity, Grok, Cursor), including direct SQLite reads from Cursor's global storage.
   - Added native support for DeepSeek Harness (parsing local `.dsh` compressed zstd event streams and querying the official balance API).
3. **Local-First Offline Aggregation**:
   - Token statistics, cost metrics, periodic usage cards, and duel matrices are aggregated entirely from local logs already on your machine, without uploading code or credentials.

---

## v1.0.1 Performance & Memory Optimizations

The v1.0.1 release focuses on reducing background idle resource usage and recurring poll overhead:

* **Physical Memory Reclamation (Idle Working Set < 100MB)**:
  - Addressed memory retention where disabling high-history providers (such as Codex) left working sets high. Added the `MemoryReclaimer` utility.
  - Implements a 600ms debounce before triggering Gen 2 and Large Object Heap (LOH) compaction after providers are disabled or large caches are cleared.
  - Invokes the Windows `SetProcessWorkingSetSize` kernel API to promptly trim unreferenced physical pages back to the operating system, dropping idle resident memory from peaks of 200MB+ to under 100MB.
  - Fixed an omission in `SessionScanner` where static metadata caches were retained after provider deactivation.
* **Stream Slicing with Reduced LOH Allocations**:
  - `SessionScanner` uses `ArrayPool<byte>` buffers and backward stream slicing to extract the first (`session_meta`) and last lines of long JSONL files, preventing oversized strings from entering the Large Object Heap.
  - Introduced `CodexMetaCache` file fingerprint checks (mtime + length), bypassing redundant JSON parsing for unchanged session files.
* **In-Memory Event Graph Caching**:
  - `LogParseCache` retains deserialized `TokenEvent` instances in memory, achieving zero additional heap allocations during routine polling when files remain unchanged.
* **Tray Icon Handle Reuse**:
  - `TrayIconRenderer` pre-renders a clean 32×32 logo and maintains singleton `Icon` handles for all 6 visual states, eliminating repeated GDI+ icon allocation and disposal on timer ticks.
* **AABB Hit-Test Coarse Culling**:
  - `IslandWindow` performs an axis-aligned bounding box (AABB) screen-space test prior to complex geometry hit-testing, reducing CPU overhead during frequent mouse movements.

---

## Project Structure

```text
src/
├─ AgentIsland.Core       # Core: domain models, calculation rules, cache policies, contracts
├─ AgentIsland.Providers  # Adapters: agent log parsing, token aggregation, catalog registry
├─ AgentIsland.Windows    # Windows: paths, SQLite readers, memory management
└─ AgentIsland             # WPF Host: UI presentation, tray icon, settings, polling backend
assets/                    # Visual assets, branding logos, documentation diagrams
tests/
└─ AgentIsland.Tests      # Test suite entry point (25 test suites)
```

---

## Building and Running

### Prerequisites
- Windows 10 / 11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Commands

```powershell
# Build solution
dotnet build .\AgentIsland.sln

# Run test suites
dotnet run --project .\tests\AgentIsland.Tests\AgentIsland.Tests.csproj

# Package self-contained executable bundle
.\build.ps1 -Runtime win-x64 -Version 1.0.1
```

The release asset will be generated at `dist/AgentIsland-1.0.1-win-x64.zip`.
