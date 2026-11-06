# SQLite Recovery Drill

**First preview: v0.1.0-preview.1.**

Check whether a supplied, closed SQLite artifact matches one known .NET
application profile. The drill makes a fresh trial copy, checks SQLite structure,
foreign keys, schema/version and application invariants, and produces JSON plus
a local HTML report. It does not repair a database or prove that every original
row was recovered.

## Try the synthetic demo

Download a bundle from the [preview release](https://github.com/pouriajamali/sqlite-recovery-drill/releases/tag/v0.1.0-preview.1),
then [verify its signed checksums](PROVENANCE.md). Keep the whole extracted directory
together. Windows x64 needs no installed .NET SDK/runtime:

```powershell
.\drill.exe --version
.\drill.exe demo "demo output"
```

Ubuntu 24.04/glibc x64:

```sh
./drill --version
./drill demo "demo output"
```

Native OS prerequisites still apply. This package/configuration is self-contained;
tests on an existing development host are not an empty-OS installation experiment.
Windows x64 and Ubuntu 24.04/glibc x64 are the supported preview targets.

From a source checkout with .NET SDK 10.0.403:

```sh
dotnet run --project src/RecoveryDrill -c Release -- demo "demo output"
```

The directory must not exist. Five small generated snapshots show:

| Example | Expected result |
| --- | --- |
| valid | pass / exit0 |
| foreign-key | fail / exit2; structural integrity alone is insufficient |
| null-value | fail / exit2; schema affinity does not validate stored values |
| batch-count | fail / exit2; application count invariant is broken |
| stale-valid | pass / exit0, with freshness and external completeness NOT_CHECKED |

The demo command exits0 only if all five expected outcomes occur. It is not a
claim that all five inputs are valid. Open each `report.html` with its complete
`summary.json`; the reports contain no database row values. The demo should take
well under five minutes on an ordinary supported development machine; no speed
or latency benchmark is claimed.

## Check one artifact

```sh
dotnet run --project src/RecoveryDrill -c Release -- check "demo output/valid.sqlite" "new report" --profile synthetic-jobs-v1
```

Exit0 means all seven checks passed. Exit2 means a completed profile assessment
failed; subsequent checks are explicitly `not_checked` after a prerequisite fails.
Exit1 means incomplete/unsupported input, I/O, worker/protocol/deadline/cleanup or
report-write failure. Read the overall outcome and `complete`, not one green row.
Reports are not a transactionally atomic pair after forced host loss; a missing
or invalid complete `summary.json` must never be treated as a successful drill.

The first release has **one compiled synthetic profile**, not arbitrary SQL,
scripts, plugins or a generic production-schema validator. It uses two small
tables, `batches` and `jobs`, a fixed application ID/user_version and exact schema
declarations. It separately validates real NULL/storage-class/type/range values,
status/attempt rules and batch counts. A smaller self-consistent stale database
can still pass; source freshness, original completeness and live-source
consistency are not established. Input SHA256 is identity, not provenance.

## Safety boundary

- Supply only a closed **standalone** local artifact. Adjacent `-wal`, `-shm` or
  `-journal`, or a WAL-mode header, is unsupported. WAL mode may remain after a
  checkpoint; unsupported does not mean corrupt. Never delete journals to make
  an input acceptable. The original is never opened with a SQLite connection.
- Use a fresh report directory. Existing destinations, collisions and observed
  symlink/reparse path components are refused. UNC/network-drive paths are
  refused on Windows; remote-mounted filesystems are unsupported and not
  universally detectable. There is no overwrite or live-backup command.
- Actual copying is capped at 16 MiB, with repeated source/copy hashes, metadata
  and sidecar checks. No hostile-filesystem race protection is claimed. Profile
  rows are capped at 1,000 batches /10,000 jobs before application reads.
- Only the owned trial copy is opened read-only, with private cache, pooling
  disabled, extension loading disabled, query-only and trusted-schema off.
  Constant compiled SQL is used; no uploaded SQL/code executes.
- Inspection uses an owned child with a 10-second parent deadline and bounded
  concurrent stdout/stderr drains. Process termination/drain and filesystem I/O
  have separate handling; this is not a hard deadline for every disk operation,
  a native-memory bound or a sandbox for malicious SQLite files.
- A valid, complete, versioned worker receipt is required; exit0 alone is
  insufficient. Cleanup failure forces incomplete/exit1 and reports that owned
  temporary data may remain. Source/journals and caller-owned data are not removed.
  Forced process/host loss can leave the trial copy; automatic recovery is not promised.

No network service, cloud account, live source backup, WAL replay, corruption
repair, encryption bypass, production restoration or data upload is included.
Use only synthetic inputs when reporting problems. No customer evidence,
production readiness, data-freshness or recoverability guarantee is claimed.

## Reproduce the checks

```sh
dotnet restore tests/RecoveryDrill.Tests --locked-mode
dotnet run --project tests/RecoveryDrill.Tests -c Release --no-restore
```

Tests exercise fixed invalid snapshots, real NULL/TEXT/BLOB/REAL and integer-range
failures, late input/copy/sidecar changes, path collisions, owned cleanup failure,
strict child receipt parsing, actual child timeout/pipe flooding, HTML escaping
and the real CLI demo. Local Windows may lack symbolic-link creation privilege;
that is reported as a skip without changing security settings. Ubuntu exercises
those cases. The [Windows/Ubuntu workflow](https://github.com/pouriajamali/sqlite-recovery-drill/actions/workflows/verify.yml)
tests the exact source and extracted packages; check the run for the selected commit.

## What this adds

[Microsoft.Data.Sqlite BackupDatabase](https://learn.microsoft.com/dotnet/standard/data/sqlite/backup)
already creates backups. [Litestream restore](https://litestream.io/reference/restore/)
already restores replicas and can run integrity checks and emit JSON. This drill
adds a small, explicit application-contract exercise; it is not another backup
engine. SQLite's [integrity check](https://www.sqlite.org/pragma.html#pragma_integrity_check)
does not replace its separate foreign-key check. See the
[contract](docs/CONTRACT.md), [case study](docs/CASE-STUDY.md) and
[contribution guide](CONTRIBUTING.md).

## Attribution and distribution

Independent synthetic implementation created with AI assistance for Pouria
Jamali in November 2026. No employer/private/shared implementation, database,
fixture or proprietary contract is included. Original source is distributed under
the [MIT license](LICENSE). Third-party dependencies retain their own licenses and
notices. SSH signing establishes provenance under a trusted key, not a separate
distribution license. This is a downloadable local tool, with no hosted service,
NuGet-library publication, customer or deployment claim.
