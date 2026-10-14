# Security

## Report a vulnerability

Please report security problems **privately**, not in a public issue.

Write to **hello@agentivity.io** and include:

- what you found and where
- the steps to reproduce it
- the version or commit you tested

We will read your report and tell you what we plan to do.

## A known limit

The engine has **no built-in login** yet. Anyone who can reach the server can use it, including the stored AI credentials and the
steps that run code. **Never expose it to the internet.** Run it only on a network you trust or behind your own access controls.

The missing login itself is a known limit. Anything beyond it is welcome as a report, for example a way around your own access
controls, a way out of a code step, or a way to read data you should not reach.

## Supported versions

Agentivity is **pre-release**. Only the latest commit is supported.
