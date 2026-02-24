### 2025-02-24: replay.cs Split Strategy Analysis

**Author:** Deckard
**Status:** Proposed
**Requested by:** Larry Ewing

#### Context

replay.cs has grown to **4,542 lines** (up from ~3,300 documented). Larry asked if we can split it without breaking the dotnet tool nature.

#### Research: .NET 10 File-Based App Constraints

1. **No `#:file` directive exists.** File-based apps are explicitly single-file: "the single C# file is included" by default.
2. **`#:project` directive IS supported.** File-based apps can reference class library projects via `#:project ../lib/Lib.csproj`. This is the officially supported way to decompose a file-based app.
3. **`#:package` and `#:property`** stay in the main `.cs` file.
4. **Tests invoke `dotnet run replay.cs`** — any split must keep `replay.cs` as the entry point. Tests reference it by path.

#### Current Structure (Major Sections)

| Section | Lines | Size | Movable? |
|---------|-------|------|----------|
| Directives + Usings | 1–24 | 24 | No — must stay |
| CLI Arg Parsing | 36–117 | 82 | No — top-level statements |
| Stats Command Dispatch | 118–290 | 173 | No — top-level statements |
| Color Helpers | 291–305 | 15 | Yes — trivial utility class |
| **Markdown Rendering** | 306–903 | **598** | **Yes — biggest win** |
| OpenFile / Pager | 904–1042 | 139 | Partial — orchestration stays |
| LaunchResume | 1043–1165 | 123 | Yes — standalone |
| Format Detection | 1166–1323 | 158 | Yes — pure functions |
| **Eval Processing** | 1324–1706 | **383** | **Yes** |
| Info Bars + Headers | 1707–1876 | 170 | Yes |
| Eval Stream/Summary | 1877–1974 | 98 | Yes |
| **Content Rendering** | 1975–2936 | **962** | **Yes — largest section** |
| Stream Events | 2937–2994 | 58 | Yes |
| **Session Browser** | 2995–3690 | **696** | **Yes** |
| Utilities | 3691–3973 | 283 | Yes |
| Stats/Glob/Analysis | 3974–4365 | 392 | Yes |
| PrintHelp | 4366–4434 | 69 | Stays — references all options |
| Record/Data Types | 4435–4542 | 108 | Yes — easiest to move |

#### Key Challenge: Captured State

All functions are currently **local functions** that capture top-level variables (`noColor`, `expandTools`, `markdownPipeline`, etc.). Moving them to a library requires:
1. Bundling options into a `ReplayOptions` record
2. Converting local functions to instance/static methods on classes
3. Passing options explicitly

#### Recommended Split Strategy

**Approach: `#:project` referencing a class library**

```
dotnet-replay/
├── replay.cs                    # Entry point (~500-700 lines)
│   #:project lib/Replay.Lib.csproj
├── lib/
│   ├── Replay.Lib.csproj       # Class library, net10.0
│   ├── Models.cs               # Record types, enums (~110 lines)
│   ├── ReplayOptions.cs        # Shared options record (~30 lines)
│   ├── MarkdownRenderer.cs     # Markdig rendering (~600 lines)
│   ├── ContentRenderer.cs      # JSONL/Waza/Eval content rendering (~1200 lines)
│   ├── SessionBrowser.cs       # Session discovery + TUI browser (~700 lines)
│   ├── StatsAnalyzer.cs        # Stats command + glob (~400 lines)
│   └── Utilities.cs            # Text helpers, formatting (~300 lines)
```

**replay.cs retains:**
- All `#:` directives
- CLI argument parsing (top-level statements)
- Main dispatch logic (stats command, file open, browser launch)
- PrintHelp
- OpenFile orchestration (calls into library classes)

**Library gets:**
- ~3,500 lines moved out
- replay.cs drops from 4,542 → ~700–1,000 lines

#### What NOT to Split

1. **`#:` directives** — must be in the entry point file
2. **Top-level statements** — CLI parsing, main dispatch flow
3. **PrintHelp** — tightly coupled to CLI options (or move it and keep options in sync)
4. **OpenFile orchestration** — the routing logic that decides which renderer to use

#### Migration Steps (Proposed Order)

1. Create `lib/` with class library project
2. Move record types to `Models.cs` (zero-risk, no behavior change)
3. Create `ReplayOptions` record to replace captured state
4. Extract `MarkdownRenderer` class (biggest complexity due to Markdig dependency)
5. Extract `ContentRenderer` (JSONL, Waza, Eval rendering)
6. Extract `SessionBrowser`
7. Extract `StatsAnalyzer`
8. Extract utilities
9. Update tests if needed (should be transparent — same `dotnet run replay.cs`)

#### Risks

- **`#:project` + `#:package`**: Need to verify packages referenced in replay.cs are available to the library (library's .csproj would have its own PackageReferences)
- **Build time**: Adding a project reference may slow `dotnet run` slightly on first build
- **NuGet tool packaging**: `dotnet pack replay.cs` must still produce a working global tool — need to verify `#:project` works with `dotnet pack`

#### Decision Needed

This is a significant refactoring. Recommend doing it incrementally: start with Models.cs (safest), then one renderer at a time. Each step should pass all 35 existing tests before proceeding.
