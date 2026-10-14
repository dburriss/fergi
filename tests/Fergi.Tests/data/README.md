# IDP defaults-plus-overrides example

Three layers produce one deploy config for the `billing` service (`deploy/billing.yaml`, hand-written expected output).

| Input | File | Role |
|---|---|---|
| org | `defaults/org.yaml` | lowest layer: replicas, resources (anchor + merge key), ports, labels, logLevel, `internal` |
| team | `teams/energy.yaml` (or `.json`) | `team`, `oncall`, a `metrics` port override, `labels.team`, `logLevel: null` |
| service | `services/billing/service.yaml` | `name`, `replicas: 4`, ports (adds grpc, overrides http), `labels.tier`, `lastReviewed` |

Variants for error paths: `zero-replicas.yaml` (Ensure fails), `bad-port.yaml` (ByKey conflict), `broken.yaml` (malformed YAML).

## Rules to output

- `Layer org team service`: later wins; `logLevel: null` in team deletes the inherited key.
- `ListsBy /ports name`: http 80 -> 8080 (service), metrics 9090 -> 9091 (team), grpc appended.
- `Copy service:/name -> /metadata/name`, `Copy team:/oncall -> /contacts/oncall`: read from the named input.
- `Rename /team -> /owner`, `Drop /oncall`, `Drop /internal`: clean up the merged document.
- `DefaultTo /replicas 2`: no-op (service says 4). `Set /env "prod"`.
- Ensure: `replicas >= 1` and `metadata.name` present.

Adapter behaviour exercised: YAML anchors are expanded, comments are not preserved, and the timestamp `lastReviewed: 2026-10-14` is printed back verbatim.
