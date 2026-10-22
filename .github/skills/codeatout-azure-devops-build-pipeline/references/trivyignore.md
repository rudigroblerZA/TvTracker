# Trivy Ignore File

Trivy reads `.trivyignore` from the repository root during both filesystem and
image scans. No task input is needed to enable it.

Add the file only when a scan reports a finding that has been reviewed and
accepted. Never add an entry to silence a finding that has not been assessed.

## Format

One vulnerability identifier per line. Comments start with `#`. Group entries
under a comment that names the affected package and records why the finding is
accepted, so the suppression can be re-evaluated later.

## AutoMapper and transitive exceptions

The AutoMapper advisory has been reviewed and accepted across backend
repositories. Include it when a scan reports it:

```text
# AutoMapper 14.0.0 vulnerability - reviewed and accepted
GHSA-rvv3-g6hj-g44x
CVE-2026-32933

# Adding since as of 16 Mar 2026, there is no resolution yet. See
#	https://avd.aquasec.com/nvd/2026/cve-2026-26127/
CVE-2026-26127
```

Repositories using the OpenTelemetry host resource detector also commonly
carry a macOS-only finding that does not affect Linux containers:

```text
# `OpenTelemetry.Resources.Host`: https://nvd.nist.gov/vuln/detail/cve-2026-81192 macOS only
CVE-2026-81192
```

## Rules

- Keep every entry already present in the repository. Removing a suppression
  turns a previously green build red for reasons unrelated to the upgrade.
- Do not copy an entire `.trivyignore` from another repository. Add only the
  identifiers this repository's scans actually report.
- Prefer upgrading the vulnerable package over suppressing the finding.
- Keep the comment explaining why each group is accepted, including a date or
  advisory link where one exists.
- Suppressions are a temporary measure. Re-check them when dependencies are
  next upgraded.

## Trivy task options

Both filesystem and image scans use the internal database mirror:

```yaml
options: --db-repository outitdev.azurecr.io/aquasecurity/trivy-db
```

Because the database is pulled from the registry, the ACR login step must run
before the first Trivy task. For an image scan on an agent where the Docker
credential helper is needed, `loginDockerConfig: true` can be added; keep it
where a pipeline already sets it.
