# Work Routing

How to decide who handles what.

## Routing Table

| Work Type | Route To | Examples |
|-----------|----------|----------|
| Architecture & design | Deckard | File structure, component boundaries, new feature design |
| Code review | Deckard | Review PRs, check quality, approve/reject implementations |
| C# implementation | Batty | New features, parsing, TUI, SQLite, Spectre.Console, file format support |
| Bug fixes | Batty | Fix issues, address regressions, handle edge cases |
| Refactoring | Batty | Record types, code organization, performance |
| Tests | Rachael | Write tests, find edge cases, verify fixes, test coverage |
| Quality & edge cases | Rachael | Input validation, error handling, format compatibility |
| Scope & priorities | Deckard | What to build next, trade-offs, decisions |
| Session logging | Scribe | Automatic — never needs routing |

## Issue Routing

| Label | Action | Who |
|-------|--------|-----|
| `squad` | Triage: analyze issue, assign `squad:{member}` label | Deckard |
| `squad:deckard` | Architecture review, design decisions | Deckard |
| `squad:batty` | Implementation work | Batty |
| `squad:rachael` | Test coverage, quality review | Rachael |

### How Issue Assignment Works

1. When a GitHub issue gets the `squad` label, **Deckard** triages it — analyzing content, assigning the right `squad:{member}` label, and commenting with triage notes.
2. When a `squad:{member}` label is applied, that member picks up the issue in their next session.
3. Members can reassign by removing their label and adding another member's label.
4. The `squad` label is the "inbox" — untriaged issues waiting for Deckard's review.

## Rules

1. **Eager by default** — spawn all agents who could usefully start work, including anticipatory downstream work.
2. **Scribe always runs** after substantial work, always as `mode: "background"`. Never blocks.
3. **Quick facts → coordinator answers directly.** Don't spawn an agent for "what branch are we on?"
4. **When two agents could handle it**, pick the one whose domain is the primary concern.
5. **"Team, ..." → fan-out.** Spawn all relevant agents in parallel as `mode: "background"`.
6. **Anticipate downstream work.** If a feature is being built, spawn Rachael to write test cases from requirements simultaneously.
