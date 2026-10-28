# How to run a mapping from the command line

`fergi` runs a mapping file. Flags and exit codes are in the [CLI reference](../reference/cli.md).

## Install it

The package is `Fergi.Tool` and the command is `fergi`. It needs the .NET 8 runtime or newer.

```
dotnet tool install --global Fergi.Tool --prerelease     # while the version is 0.1.0-alpha.*
dotnet tool update --global Fergi.Tool --prerelease
```

From source:

```
dotnet run --project src/Fergi.Cli -c Release -- run ...
dotnet pack src/Fergi.Cli -c Release -o out
dotnet tool install --tool-path tools --add-source out Fergi.Tool --prerelease
```

## Run it

```
fergi validate mapping.yaml
fergi run mapping.yaml -i defaults/org.yaml -i team=teams/energy.yaml -i service=svc/billing.yaml -o deploy/billing.yaml
fergi run catalog.yaml -l repos="repos/*/service.yaml" -o catalog/services.yaml
fergi run mapping.yaml -i org=defaults/org.yaml -o out.yaml --dry-run
```

`--dry-run` prints the changes against the current output and writes nothing.

`fergi --version` prints the package version and the commit it was built from.
