# v0.1.0-preview.2 servicing

Services the existing .NET 10 LTS line: SDK 10.0.404, runtime and
Microsoft.Data.Sqlite 10.0.15. No major framework migration or new profile.

The original input stays immutable and only the trial is opened read-only.
Integrity and foreign-key checks remain separate. A self-consistent stale input
can still pass the seven profile checks; freshness/external completeness stay
NOT_CHECKED. Incomplete input/worker/cleanup evidence remains exit1, never pass.
No backup, repair, WAL replay, SQL/plugin API, freshness or production guarantee.

GitHub Actions: NOT_RUN by owner instruction. Local Windows and Ubuntu tests and
actual self-contained package checks are separate evidence; no empty-OS claim.
Preserve the original preview.1 tag/assets/signatures. Verify the signed checksum
file and the trusted public key before consuming this release.

Technology sources: https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
and https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.15 .
