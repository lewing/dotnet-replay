# dotnet-replay

Interactive terminal viewer for Copilot CLI sessions and [waza](https://github.com/spboyer/waza) evaluation transcripts. Built as a single-file .NET 10 app


<img width="2655" height="1555" alt="image" src="https://github.com/user-attachments/assets/5465fe1e-0647-4ccb-8fda-e17809790d80" />
<img width="2655" height="1555" alt="image" src="https://github.com/user-attachments/assets/5465fe1e-0647-4ccb-8fda-e17809790d80" />






## Try it out

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dnx dotnet-replay              # Browse your recent Copilot CLI sessions
```

No install needed — `dnx` downloads the tool to the NuGet cache, runs it, and cleans up.

## Install

### Global tool (recommended)

```bash
dotnet tool install -g dotnet-replay
```

Once installed globally, run `replay` directly from any directory.

### Local tool manifest

```bash
dotnet new tool-manifest   # if you don't already have one
dotnet tool install dotnet-replay
```

With a local install, use `dotnet replay` instead.

## Usage

### Session Browser

```bash
replay                      # Browse recent Copilot CLI and Claude Code sessions
replay <session-id>         # Open a session by GUID
```

Run with no arguments to interactively browse your Copilot CLI and Claude Code sessions. Sessions are loaded asynchronously and can be filtered with `/`.

### Interactive Mode (Default)

```bash
replay <file>               # Interactive pager with keybindings
replay <file> --tail 10     # Show only the last 10 turns
replay <file> --expand-tools # Show tool args, results, and thinking
replay <file> --full        # Don't truncate long content
replay <file> --filter user # Filter by event type (user, assistant, tool, error)
replay <file> --no-color    # Disable ANSI colors
replay session.jsonl --no-follow  # Disable auto-follow for JSONL files
```

> **Note:** `dotnet replay` also works if you prefer the explicit prefix.

> **Auto-follow:** JSONL files automatically watch for new content (like `tail -f`). Use `--no-follow` to disable this behavior.

### Stream Mode (Non-Interactive)

```bash
replay <file> --stream      # Output entire transcript and exit
replay <file> | less        # Auto-switches to stream mode when piped
```

## Supported Formats

- **Copilot CLI events** (`.jsonl`) — Session transcripts from GitHub Copilot CLI
- **Claude Code sessions** (`.jsonl`) — Session transcripts from Claude Code (`~/.claude/projects/`)
- **Waza evaluation results** (`.json`) — EvaluationOutcome format with `tasks[].runs[].transcript[]`
- **Waza task transcripts** (`.json`) — Flat JSON with `transcript[]` array and optional `session_digest`

## Keybindings

| Key(s) | Action |
|--------|--------|
| **↑** or **k** | Scroll up one line |
| **↓** or **j** | Scroll down one line |
| **←** or **h** | Scroll left (pan horizontally) |
| **→** or **l** | Scroll right (pan horizontally) |
| **PgUp** | Page up |
| **PgDn** | Page down |
| **0** | Reset horizontal scroll |
| **Space** | Page down |
| **g** or **Home** | Jump to start of transcript |
| **G** or **End** | Jump to end of transcript |
| **t** | Toggle tool expansion (show/hide args, results, and thinking) |
| **f** | Cycle filter: all → user → assistant → tool → error |
| **i** | Toggle session info overlay |
| **/** | Enter search mode |
| **n** | Jump to next search match |
| **N** | Jump to previous search match |
| **q** or **Esc** | Quit |

## Examples

View an interactive transcript:
```bash
replay session-events.jsonl
```

View the last 5 turns with tool expansion:
```bash
replay results.json --tail 5 --expand-tools
```

Stream the entire transcript without interaction:
```bash
replay transcript.json --stream
```

Pipe to a file:
```bash
replay session.jsonl --expand-tools > output.txt
```

## About

`dotnet-replay` was built by a [squad](https://github.com/lewing/arena) currently in stealth mode.

## License

MIT
