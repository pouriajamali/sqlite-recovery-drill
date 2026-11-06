# First-profile contract

The supported artifact is a local closed SQLite file, at most16 MiB, in rollback
journal format with no adjacent WAL/SHM/journal. A WAL header is unsupported even
after checkpoint, not classified as corruption. The tool copies bytes; it does
not perform a live backup, repair or WAL replay.

`synthetic-jobs-v1` has application_id=0x44524C31 and user_version=1, with these exact
schema declarations (no additional objects):

```sql
CREATE TABLE batches (id INTEGER PRIMARY KEY, expected_jobs INTEGER);
CREATE TABLE jobs (id INTEGER PRIMARY KEY, batch_id INTEGER REFERENCES batches(id), status TEXT, attempts INTEGER);
```

Schema text is intentionally exact for this toy profile, not a general semantic
schema comparer. Column affinity does not certify row values. Required fields
must be non-NULL with actual SQLite storage classes INTEGER or TEXT as declared
by the profile checks. IDs are positive; expected_jobs is0..10000 and attempts
is0..1000000. Status is queued/running/done; running requires attempts>0. Each
batch's expected_jobs must equal its actual job count. Row caps:1,000 batches and
10,000 jobs. The stored expected count is not independent evidence of completeness.

Ordered checks: sqlite-integrity, schema, row-counts, foreign-keys,
storage-classes, value-ranges, application-invariants. First failure stops later
checks as not_checked/PREREQUISITE_NOT_MET. All checks must pass for overall pass.
Full integrity_check is capped at 20 result rows; only generic reason codes are
returned. FK presence is reported without disclosing rows or claiming a total.

The child produces exactly one JSON object with schema=1, complete=true,
profile, outcome and all seven ordered checks. Extra/duplicate fields, unknown
codes/statuses, wrong IDs/order/version, incomplete/missing checks, inconsistent
outcome or extra non-whitespace output are rejected. Stdout16 KiB and stderr 4 KiB
are drained concurrently; any stderr, overflow, nonzero exit, crash or deadline
is incomplete. The ten-second deadline applies to inspection; source/copy disk
I/O is not a hard-timed security sandbox. Test-only executable/protocol probes
belong only to the separate test assembly, which is excluded from delivery.

Report schema recovery-drill-report-v1 records version, profile, byte count/hash,
checks and overall outcome/complete/exit, cleanup and generic reason. Freshness
and external completeness are NOT_CHECKED; input provenance UNVERIFIED and live
source consistency NOT_PROVEN. It never includes row values, raw exceptions or
input/temporary absolute paths. HTML encodes dynamic values, uses embedded CSS
and a restrictive CSP, with no external script/asset. Visual browser rendering
has not yet been independently tested; automated checks cover escaping/content.

Input source/copy hashes and metadata/sidecars are rechecked before and after
inspection. Any observed change is incomplete. Only the exact fresh owned trial
file is cleaned, never a source/journal/caller path. Failed cleanup overrides
even successful checks to incomplete/exit1 and preserves reports. Unexpected
trial sidecars are retained and reported, not deleted to force success.

Reports use fresh temporary files and publish summary.json after HTML. Forced
host/process loss or disk failures can leave partial reports/trial data; this is
not a power-loss transaction. Treat only a valid complete JSON report as final.

Primary background: [SQLite WAL](https://www.sqlite.org/wal.html),
[SQLite security](https://www.sqlite.org/security.html),
[PRAGMAs](https://www.sqlite.org/pragma.html),
[typed connection settings](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)
and [synchronous SQLite async APIs](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async).
