# How to build, test and release

## Build and test

```
dotnet build Fergi.slnx -c Release
dotnet test Fergi.slnx -c Release
scripts/fable-test.sh all        # JS (bun) and Python; slow, needs network the first time
```

`examples/readme.fsx` is a runnable script of the library snippets (`dotnet fsi examples/readme.fsx` after a
build). It uses toy data, so a few runs print an error on purpose.

## Release

Releases are tag-driven (`.github/workflows/publish-nuget.yml`, NuGet Trusted Publishing, no API key). The
`Fergi` and `Fergi.Tool` packages share one version, `<Version>` in `Directory.Build.props`.

- `scripts/publish.sh` (interactive; `--dry-run` to rehearse) rolls `CHANGELOG.md`, bumps that version,
  commits, tags `v<version>` and offers to push. The tag push publishes. CI fails if the tag differs from the
  repo version.
- `scripts/install-alpha.sh` packs and installs a local alpha of the tool for trying it out.
- The `Fnode*` packages are released separately.
