### 2025-07-22: CI workflow updated for csproj build + squad state cleanup

**Author:** Batty (Core Developer)
**Status:** Completed

**Changes:**
1. **CI workflow fix**: `.github/workflows/ci.yml` now uses `dotnet build dotnet-replay.csproj` (was `dotnet build replay.cs`). Added `dotnet test tests/ReplayTests.csproj` step.
2. **Squad state removed from tracking**: `git rm --cached -r .squad/` and `.squad-templates/` on `refactor/csproj-split` branch. 58 files removed from git index. Files remain on disk for local squad use.
3. **Squad workflows preserved**: All 11 `.github/workflows/squad-*.yml` files remain tracked — they're real CI workflows, not squad state.

**Rationale:** The `squad-main-guard` workflow blocks `.squad/` from merging to main. Cleaning these proactively on the PR branch prevents merge conflicts and guard failures. CI was broken since the csproj conversion because the build step still referenced the old file-based app command.

**Impact:** CI will now pass on this branch. PR can merge to main without squad guard blocking it.
