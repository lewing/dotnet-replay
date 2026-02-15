# NuGet Trusted Publishing Setup Instructions

The GitHub Actions workflow for publishing `dotnet-replay` to NuGet.org is now configured. To complete the setup, you need to configure **Trusted Publishing** on NuGet.org and add a GitHub secret.

## What is Trusted Publishing?

Trusted Publishing uses OpenID Connect (OIDC) to authenticate GitHub Actions with NuGet.org. No API keys are stored or exposed—GitHub provides short-lived tokens that NuGet validates and exchanges for temporary credentials. This is the secure, modern way to publish packages.

## Setup Steps

### 1. Configure Trusted Publishing on NuGet.org

1. **Log in to [nuget.org](https://nuget.org)**

2. **Navigate to Trusted Publishing**
   - Click your username in the top-right corner
   - Select **Trusted Publishing** from the menu

3. **Add a New Trusted Publishing Policy**
   - Click **Add Trusted Publishing Policy**
   - Fill in the following fields:

   | Field | Value | Notes |
   |-------|-------|-------|
   | **Repository Owner** | `lewing` | Case-insensitive |
   | **Repository** | `dotnet-replay` | Case-insensitive |
   | **Workflow File** | `publish.yml` | **Filename ONLY** — do NOT include `.github/workflows/` path |
   | **Environment** | _(leave empty)_ | The workflow doesn't use GitHub environments |

4. **Choose Policy Owner**
   - Select yourself (individual) or an organization you belong to
   - This determines who owns the packages published via this policy

5. **Save the Policy**

### 2. Add GitHub Secret

1. **Go to your GitHub repository**
   - Navigate to https://github.com/lewing/dotnet-replay

2. **Add Repository Secret**
   - Go to **Settings** → **Secrets and variables** → **Actions**
   - Click **New repository secret**
   - Name: `NUGET_USERNAME`
   - Value: Your NuGet.org **username** (NOT your email address)
     - Find this at https://nuget.org/account → Profile → Username
   - Click **Add secret**

## How to Publish

Once setup is complete, you can publish in two ways:

### Option 1: Push a Version Tag (Recommended)

```bash
# Create and push a version tag
git tag v0.1.0
git push origin v0.1.0
```

The workflow automatically:
- Extracts the version (`v0.1.0` → `0.1.0`)
- Builds and packs the project
- Publishes to NuGet.org

### Option 2: Manual Workflow Dispatch

1. Go to **Actions** tab in GitHub
2. Select **Publish to NuGet** workflow
3. Click **Run workflow**
4. Enter the version (e.g., `0.1.0` without the `v` prefix)
5. Click **Run workflow**

## Verifying the First Publish

1. **Check Actions Tab**
   - Monitor the workflow run at https://github.com/lewing/dotnet-replay/actions
   - All steps should complete successfully

2. **Policy Activation**
   - The first successful publish provides GitHub repo IDs to NuGet
   - Your policy becomes **permanently active** after the first publish
   - Until then, it's "temporarily active for 7 days"

3. **Check NuGet.org**
   - Your package will appear at https://www.nuget.org/packages/dotnet-replay
   - It may take a few minutes to index

## Troubleshooting

### "Policy not found" or "unauthorized"
- Verify the Trusted Publishing policy is created on NuGet.org
- Check that Repository Owner, Repository, and Workflow File match exactly
- Ensure the `NUGET_USERNAME` secret is set correctly

### "Package already exists"
- The workflow includes `--skip-duplicate` to handle this gracefully
- If you need to republish the same version, delete it on NuGet first (not recommended for production)

### Workflow fails on "NuGet login"
- Check that `NUGET_USERNAME` is your NuGet.org username, NOT email
- Verify you have permissions to publish packages under the chosen policy owner

## Workflow Details

**Triggers:**
- `push` on tags matching `v*` (e.g., `v0.1.0`, `v1.2.3-beta`)
- `workflow_dispatch` (manual trigger with version input)

**Process:**
1. Checkout code
2. Setup .NET 9.0
3. Extract version from tag or manual input
4. Restore, build, and pack
5. Login to NuGet via OIDC (gets 1-hour temp key)
6. Push package to NuGet.org
7. Upload artifact to GitHub Actions

**Security:**
- Uses `id-token: write` permission for OIDC
- No API keys stored in secrets
- Temporary credentials valid for 1 hour only
- Each workflow run gets a fresh token

## Next Steps

After first successful publish:
1. Users can install with: `dotnet tool install -g dotnet-replay`
2. For updates, increment version in `dotnet-replay.csproj` and push a new tag
3. Consider semantic versioning (MAJOR.MINOR.PATCH)

## References

- [Microsoft Docs: Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
- [GitHub Actions: OIDC](https://docs.github.com/en/actions/security-guides/security-hardening-your-deployments/about-security-hardening-with-openid-connect)
- [NuGet/login Action](https://github.com/NuGet/login)
