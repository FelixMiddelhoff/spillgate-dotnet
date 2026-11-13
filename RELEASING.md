# Releasing to nuget.org

The workflow `.github/workflows/release.yml` builds, tests, packs and publishes the four packages `Spillgate`, `Spillgate.Brotli`, `Spillgate.Zstd` and `Spillgate.Http` when a tag `vX.Y.Z` is pushed, from a GitHub environment named `nuget` that needs your approval. Publishing uses NuGet **trusted publishing** (OIDC): the job exchanges its GitHub token for a short-lived API key with the `NuGet/login` action, so no long-lived key is stored. The workflow has never run on GitHub.

Checked on 2026-11-13 against <https://learn.microsoft.com/nuget/nuget-org/trusted-publishing>: the job needs `id-token: write`, the `NuGet/login` step takes your nuget.org profile name in `user` (not an email), and the policy is created under your nuget.org user menu, Trusted Publishing. Re-check the page before the first release.

## One-time setup (maintainer)

1. **nuget.org account** with two-factor authentication. Note your profile name.
2. **Package ids.** On 2026-11-13 the ids `Spillgate`, `Spillgate.Brotli`, `Spillgate.Zstd` and `Spillgate.Http` were free (404 on `https://api.nuget.org/v3-flatcontainer/<id>/index.json`). A trusted publishing policy for a package that does not exist yet can only be created for the first publish in the **pending** state; if the pending policy is not offered, do the first publish with an API key (below) and add the policy afterwards. Optionally reserve the prefix `Spillgate.` (nuget.org, Account settings, Reserve prefix).
3. **GitHub environment.** Repository **Settings, Environments, New environment**, name `nuget`, add yourself as *Required reviewer*, restrict *Deployment branches and tags* to `v*`. No secrets are needed for trusted publishing.
4. **Trusted publisher policy** (nuget.org, Trusted Publishing): owner `FelixMiddelhoff`, repository `spillgate-dotnet`, workflow file `release.yml`, environment `nuget`. Put your profile name into the repository variable `NUGET_USER` (Settings, Secrets and variables, Variables).
5. **Fallback for the first release** if the policy cannot be created before the packages exist: create an API key (scope: push new packages, glob `Spillgate*`, short expiry), store it as the environment secret `NUGET_API_KEY` of `nuget` and run the release; the workflow uses it only when `NUGET_USER` is empty. Delete the key afterwards and create the trusted publisher policy.
6. **Tag protection.** Add a tag ruleset for `v*` (only maintainers create or delete them), and protect `main`.

## Each release

1. Update `CHANGELOG.md` (move the Unreleased entries under the version and date), set `<Version>` in `src/Directory.Build.props`, commit.
2. Run everything locally: `dotnet build -c Release`, tests on both frameworks with `SPILLGATE_FUZZ_SECONDS=300`, `dotnet format --verify-no-changes`, `dotnet pack -c Release`.
3. Tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`. The workflow checks that the tag equals the package version, runs the full test suite, packs, installs the packed packages into a throw-away consumer project from a local feed, and waits for your approval of the `nuget` environment before it pushes. `--skip-duplicate` is not used: a version that exists is an error.
4. After the push: install the new version into a clean project, check the package page and the symbol package.
5. A bad release is never overwritten. Unlist it on nuget.org and release a new version.

Never tag `v1.0.0` without a deliberate decision of the maintainer.
