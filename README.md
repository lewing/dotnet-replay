# dotnet-replay

Interactive terminal viewer for Copilot CLI sessions and waza evaluation transcripts.

<img width="2750" height="1555" alt="image" src="https://github.com/user-attachments/assets/75114d1a-965b-44da-bbda-33583486ba93" />


## Install

```bash
dotnet tool install -g dotnet-replay
```

## Usage

### Interactive Mode (Default)

```bash
dotnet replay <file>               # Interactive pager with keybindings
dotnet replay <file> --tail 10     # Show only the last 10 turns
dotnet replay <file> --expand-tools # Show tool call arguments and results
dotnet replay <file> --full        # Don't truncate long content
dotnet replay <file> --filter user # Filter by event type (user, assistant, tool, error)
dotnet replay <file> --no-color    # Disable ANSI colors
```

### Stream Mode (Non-Interactive)

```bash
dotnet replay <file> --stream      # Output entire transcript and exit
dotnet replay <file> | less        # Auto-switches to stream mode when piped
```

## Supported Formats

- **Copilot CLI events** (`.jsonl`) — Session transcripts from GitHub Copilot CLI
- **Waza evaluation results** (`.json`) — EvaluationOutcome format with `tasks[].runs[].transcript[]`
- **Waza task transcripts** (`.json`) — Flat JSON with `transcript[]` array and optional `session_digest`

## Keybindings

| Key(s) | Action |
|--------|--------|
| **↑** or **k** | Scroll up one line |
| **↓** or **j** | Scroll down one line |
| **←**, **h**, **PgUp** | Page up |
| **→**, **l**, **PgDn** | Page down |
| **Space** | Page down |
| **g** or **Home** | Jump to start of transcript |
| **G** or **End** | Jump to end of transcript |
| **t** | Toggle tool expansion (show/hide call args and results) |
| **f** | Cycle filter: all → user → assistant → tool → error |
| **i** | Toggle session info overlay |
| **/** | Enter search mode |
| **n** | Jump to next search match |
| **N** | Jump to previous search match |
| **q** or **Esc** | Quit |

## Examples

View an interactive transcript:
```bash
dotnet replay session-events.jsonl
```

View the last 5 turns with tool expansion:
```bash
dotnet replay results.json --tail 5 --expand-tools
```

Stream the entire transcript without interaction:
```bash
dotnet replay transcript.json --stream
```

Pipe to a file:
```bash
dotnet replay session.jsonl --expand-tools > output.txt
```

## License

MIT
