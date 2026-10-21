# Security Policy

## Reporting a vulnerability

**Please do not report security vulnerabilities through public issues, discussions or pull requests.**

Report them privately with GitHub's private vulnerability reporting:

1. Open the **Security** tab of this repository.
2. Choose **Report a vulnerability**.

Only the maintainers can see the report. Please include as much as you can of:

- the affected package or image and its version (or the commit);
- a description of the issue and its impact;
- steps to reproduce, a proof of concept or the affected code;
- any known workaround.

We will acknowledge the report as soon as possible, usually within a week. We will keep you informed and agree on a
disclosure date with you. Fixes ship as a new patch version, with a GitHub Security Advisory that credits you, unless you
prefer to remain anonymous.

## Supported versions

| Version | Supported |
|---------|-----------|
| 0.1.x | ✅ |

Security fixes go to the latest minor version: the NuGet packages, the `psr-worker` and `psr-dashboard` images, and the
matching `psrunner` release.

## Security model

PythonScriptRunner runs Python code written by **trusted authors**. A stage is arbitrary code running with the
permissions of the worker's user, and it is **not a sandbox** for untrusted code. Anyone with the `Author` role can
publish code that runs on the workers, so give that role only to people you would let deploy code.

What the runner guarantees:

- Each attempt runs in its own process. That process gets a clean environment (no variables from the host), its own
  work folder, a timeout and a memory limit.
- A secret reaches only the processes of the stages that declare it. Secrets are never written to `input.json`, logs,
  metrics or exceptions, and their values are masked (`***`) in the captured stdout and stderr.
- Published dependencies are locked with hashes (`uv pip compile --generate-hashes`) and installed with
  `--require-hashes`.
- In the dashboard, every action checks the user's role. Requests that change something require the `X-PSR-Request`
  header, and fixed login stores only PBKDF2 hashes, with lockout and rate limiting.

Examples of vulnerabilities we want to hear about:

- getting past the dashboard's authentication or roles;
- a secret leaking to another stage, to the logs or to the database;
- breaking the guarantees above, such as a process that outlives its timeout or exceeds its memory limit;
- an issue in the container images.

A stage author running code on the workers is not a vulnerability: it is how the product works.

## Hardening

- Run workers as an unprivileged user with access only to what the stages need. The images run as the non-root user
  `psr` with a read-only root file system.
- Keep secrets in the platform's secret store, and pass them as `PythonScriptRunner__Secrets__<NAME>`.
- Serve the dashboard over HTTPS. Behind a proxy, set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`.
- Do not expose the development setup of the AppHost or of the generated compose: its users share the password
  `psr-dev`, and it includes Mailpit and test SMTP secrets.
