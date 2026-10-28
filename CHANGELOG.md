# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `fergi` command line, packaged as the .NET tool `Fergi.Tool` (needs the .NET 8 runtime or newer): `validate` and
  `run` for mapping files, with named inputs, input lists with globs, one output, `--dry-run` and `--version`.
- `Fergi` library: mapping builders, plans, policies and errors with positions, over the `Fnode` document core.
- JSONata computed values and `ensure` predicates for mappings, bundled in `Fergi.Tool` (`Fergi.Jsonata` is not published yet).
- Releases are tag-driven: a `v*` tag publishes `Fergi` and `Fergi.Tool` to NuGet through Trusted Publishing.
