# Contributing to Agentivity

Thanks for your interest! Bug reports, ideas and pull requests are all welcome.

> **Please note:** pull requests are applied to our source repository and closed as "applied", not merged. See [Pull requests](#pull-requests).

## Before you start

- **Read the license.** Agentivity is source-available under the [Elastic License 2.0](LICENSE); see
  [LICENSING.md](LICENSING.md) and the [License FAQ](LICENSE-FAQ.md).
- **Open an issue first** for anything bigger than a small fix, so we can agree on the approach before you spend
  time on it.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Keep the change focused: one fix or one feature per pull request.
3. Make sure the solution builds (`dotnet build`) and follow the existing code style (see `.editorconfig`).
4. Describe *what* changes and *why*.

### Sign the CLA (once)

Your first pull request will get a comment from the CLA bot. To sign the
[Contributor License Agreement](CLA.md), reply with:

> I have read the CLA Document and I hereby sign the CLA

You only do it once; it covers your later contributions. The CLA lets us keep distributing the project under
its licenses (including a commercial one) while you keep ownership of your work.

## How contributions reach the code

This repository is a published mirror of our internal source repository, which also holds the test suite and
the Enterprise modules. We review your pull request here, apply the accepted change to the source repository,
and it appears in this repository with the next release — with you credited as the author. A pull request may
therefore be closed as "applied" rather than merged.

## What we do not accept

- Code you do not have the right to contribute (see the CLA, section 5).
- Changes that remove or weaken license notices or license-key functionality.
- Secrets, credentials or personal data. Never include API keys in an issue or a pull request.

## Security

Please report security problems privately, not in a public issue. See [SECURITY.md](SECURITY.md) for how to
reach us.
