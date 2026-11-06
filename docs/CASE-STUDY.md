# A readable snapshot can still fail its application contract

This independent synthetic example separates three questions: can SQLite read
the artifact, do its structure/relationships match a trusted profile, and do the
stored values satisfy that profile? A passing result still cannot prove that the
artifact is current or contains every original row.

| Decision | Evidence in the test runner |
| --- | --- |
| Structural integrity is not FK validation | fixture/foreign-key |
| A fixed synthetic page corruption produces a structural failure | fixture/corrupt-page; b-tree fragmentation count255 exceeds the documented limit60 |
| Declared INTEGER affinity is insufficient | fixture/null-value, text-integer, blob-integer, real-integer |
| Int64 values are range-checked without narrowing conversions | fixture/range |
| Stored count/status rules are explicit application assumptions | fixture/batch-count, bad-status |
| Self-consistent older data can pass | fixture/stale-valid; NOT_CHECKED fields |
| WAL/SHM/journal presence is not silently repaired | sidecar/-wal, -shm, -journal; wal-header-is-unsupported-not-corrupt |
| Byte limit applies to actual streaming, not stat alone | actual-stream-byte-cap, initial-file-cap |
| Copied bytes and source identity are rechecked | source-rehash-detects-same-size-change, copy-rehash-detects-tamper, source-change-during-inspection-never-passes |
| A new sidecar prevents a successful result | sidecar-created-during-copy, trial-sidecar-preserved-and-cleanup-incomplete |
| Child exit0 is not proof of complete verification | worker-protocol/*, oversized-receipt-before-json-parse |
| Pipes cannot bypass the inspection deadline | owned-process/hang, stdout-flood, stderr-flood, failure |
| Cleanup is part of success | cleanup-failure-is-incomplete |
| New output paths do not overwrite caller data | existing-output-and-collision-preserved, report-collision-does-not-publish-pass-json |
| HTML output must not execute data | html-escapes-dynamic-text |
| Paths are observed conservatively | symlink-source-refused, symlink-output-component-refused, network-style-path-refused-before-io |
| A real packaged execution path is distinct from in-process tests | actual-cli-demo-and-argument-rejection; separate extracted-package smoke checks |

Local Windows and Ubuntu WSL results and actual package manifests are recorded
in the candidate delivery receipt, not inferred from this table. A missing local
Windows symlink privilege is disclosed as a skip; Ubuntu tests exercise it. No
benchmark, live application, real customer backup, power-loss experiment or
hostile-filesystem/native-engine security proof is included.

The separate child and closed input policy reduce accidental side effects. They
do not make arbitrary SQLite files safe, restore lost data, guarantee native
memory bounds or prove production operational recovery. No custom SQL/plugin,
external service, source database connection or private project contract is used.

The deliberate corruption touches byte7 of the newly generated jobs root b-tree
page, as defined in the [SQLite file format](https://www.sqlite.org/fileformat2.html#b_tree_pages).
This is a tiny deterministic fault fixture, not a general corruption catalog.
