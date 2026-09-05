# AgentIsland for Windows

[English](README.md) | [简体中文](README_zh.md)

AgentIsland for Windows is a desktop floating-island monitoring tool for AI coding agents on Windows, built with C# (.NET 8 + WPF). It stays on your desktop to display provider-specific activity, token, quota/balance, and usage information for Claude, Codex, DeepSeek Harness, Antigravity, Grok, and Cursor.

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
   - Implemented Windows session and log path discovery for the current storage layouts of Claude, Codex, Antigravity, Grok, and Cursor, including direct SQLite reads from Cursor's global storage.
   - Added native support for DeepSeek Harness (parsing local `.dsh` compressed zstd event streams and querying the official balance API).
3. **Local-First Offline Aggregation**:
   - Token statistics, cost metrics, periodic usage cards, and duel matrices are aggregated from local logs already on your machine. AgentIsland does not upload local code or logs; quota and balance features may call the corresponding official provider APIs with the configured credentials.

---

## v1.0.1 Performance & Memory Optimizations

The v1.0.1 release focuses on reducing background idle resource usage and recurring poll overhead:

* **Physical Memory Reclamation (Idle Working Set < 100MB)**:
  - Addressed memory retention where disabling high-history providers (such as Codex) left working sets high. Added the `MemoryReclaimer` utility.
  - Implements a 600ms debounce before triggering Gen 2 and Large Object Heap (LOH) compaction after providers are disabled or large caches are cleared.
  - Invokes the Windows `SetProcessWorkingSetSize` kernel API to promptly trim unreferenced physical pages back to the operating system, dropping idle resident memory from peaks of 200MB+ to under 100MB.
  - Fixed an omission in `SessionScanner` where static metadata caches were retained after provider deactivation.
* **Stream Slicing with Reduced LOH Allocations**:
  - `SessionScanner` uses `ArrayPool<byte>` buffers and backward stream slicing to reduce temporary byte-buffer allocations while reading long JSONL files; first-line metadata is decoded only when needed.
  - Introduced `CodexMetaCache` file fingerprint checks (mtime + length), bypassing redundant JSON parsing for unchanged session files.
* **In-Memory Event Graph Caching**:
  - `LogParseCache` reuses deserialized `TokenEvent` instances for unchanged files, avoiding repeated JSON parsing and event-object churn during routine polling.
* **Tray Icon Handle Reuse**:
  - `TrayIconRenderer` pre-renders a clean 32×32 logo and caches `Icon` handles by visual state, reducing repeated GDI+ rendering when the state is unchanged.
* **AABB Hit-Test Coarse Culling**:
  - `IslandWindow` performs an axis-aligned bounding box (AABB) screen-space test before complex geometry hit-testing, reducing unnecessary hit-test work during mouse movement.

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
└─ AgentIsland.Tests      # Test suite entry point (27 test suites)
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
