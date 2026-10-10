# License FAQ

This FAQ is a plain-language guide. It does not change the license: if anything here conflicts with the
[LICENSE](LICENSE) file, the license wins. It is not legal advice — if your situation is not covered, or the
stakes are high, ask us (see the end of this page) or your own lawyer.

The repository is under the [Elastic License 2.0](LICENSE). The SDKs and showcase apps are MIT. See
[LICENSING.md](LICENSING.md) for the full picture.

## The basics

**Is Agentivity open source?**
No. The source is available and you can use, modify and redistribute it, but one restriction (no hosted or
managed service for third parties) means it is not "open source" as the Open Source Initiative defines the
term. We call it *source-available*. It follows the [fair-code](https://faircode.io) model, and the Elastic
License 2.0 is on the list of licenses that meet the fair-code requirements.

**Can my company use it in production, for free?**
Yes. Running Agentivity for your own business — internal tools, automations, agents for your own staff or
customers' interactions with *your* products — is free and needs no agreement with us.

**Can I modify it?**
Yes. Keep the license and copyright notices, and if you distribute a modified copy, say prominently that you
changed it.

**Can I redistribute it or publish a fork?**
Yes, with a copy of the license, and subject to the same limits (no hosted/managed service for third
parties, notices kept). A fork is not a way around the license.

**Can I use it for research, teaching or personal projects?**
Yes.

## What you can't do

**What exactly is forbidden?**
Three things:
1. Providing the software to third parties as a **hosted or managed service** where the service gives users
   access to a substantial set of its features. In other words: you cannot run "Agentivity as a service" for
   other people.
2. Moving, changing, disabling or circumventing **license-key functionality**, or removing/hiding features
   protected by a license key.
3. Altering, removing or obscuring **license, copyright or other notices**.

**Why this restriction?**
It stops a third party from reselling our own work as a service while contributing nothing back, which is what
lets us keep developing the project and offer a free core.

## Common situations

**I'm an agency/consultancy. Can I deploy Agentivity for my clients?**
- **Installed on the client's own infrastructure, then used by the client** — yes. The client is the user and
  needs to receive the license terms (see the Notices section of the license).
- **Run on *your* servers and operated for the client** (they log in to something you host) — that is a
  managed service. You need a commercial license.
- **Building client solutions *with* Agentivity as your engine**, where each client's end users only use the
  finished solution — usually fine; if the client can build or run their own agents and workflows through
  your hosted offering, treat it as a managed service.

When in doubt, ask us first.

**Can I embed Agentivity inside my own product?**
If your product uses Agentivity as an internal engine and your users do **not** get access to a substantial set
of Agentivity's own features (designing agents, workflows, teams…), that is generally fine. If your product
essentially exposes Agentivity to your users, that is a hosted service: you need a commercial (OEM) license.

**Can I offer a hosted version of Agentivity (SaaS)?**
Not without a commercial license from us.

**Can I use the SDKs in my commercial app?**
Yes. The React and Flutter SDKs are MIT: no restrictions beyond keeping their license notice. They talk to an
Agentivity server, and *that* server remains under the Elastic License 2.0 (or a commercial license).

**Can I copy code from the showcase apps into my own?**
Yes, they are MIT.

## Enterprise features and the Docker image

**What are the Enterprise features?**
Some modules — for example versioning and restore of workspace objects, the full version of team
orchestration and other modules marked *Enterprise* — are **not** part of this repository and are not published. They are
licensed separately, under a commercial agreement with us.

**Does the Docker image contain Enterprise code?**
The image published for the community edition does not contain the Enterprise modules. It contains the open core,
the Agentivity Studio web interface and a basic form of AI teams. If you have a commercial agreement, you receive the matching image and a license key.

**Under which terms may I use the Studio shipped in the image?**
The Studio is proprietary software that ships *with* Agentivity. You may use it together with an Agentivity
server under the same conditions as the server itself. You may not extract and redistribute it on its own,
modify it, or reverse-engineer it.

**Can I remove license-key checks to unlock Enterprise features?**
No — that is exactly what the license forbids.

## Contributing

**Can I contribute code?**
Yes, and thank you. Pull requests require signing the short [Contributor License Agreement](CLA.md) once.

**What is the CLA, and why?**
It confirms that you wrote what you contribute and lets us distribute it under the project's licenses —
including future licenses and a commercial one. You keep ownership of your contribution. Details in
[CLA.md](CLA.md).

**Will my pull request be merged directly?**
This repository is a published mirror of our source repository. Accepted contributions are applied there and
reach this repository with the next release, with attribution to you.

## Trademarks

**Can I use the Agentivity name or logo?**
You may say your product "works with" or "is built on" Agentivity. You may not name your product or company
in a way that suggests it is ours or endorsed by us, and you may not use the logo as your own. The licenses do
not grant trademark rights.

## Still unsure?

Describe what you want to do — who the users are, where it runs, who operates it — and write to
**hello@agentivity.io**. We would rather answer a question than have a disagreement.
