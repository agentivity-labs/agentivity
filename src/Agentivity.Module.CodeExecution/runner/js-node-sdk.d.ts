// Type declarations for the globals available inside an Agentivity JavaScript code node
// (`core.code.javascript` / JSNode). Not a published npm package — this documents the sandbox
// contract implemented by runner.js for editor intellisense only.
//
// The script body itself has no module system: no `import`, no `require`. Everything it can
// reach is declared here as an ambient global.

/**
 * The resolved value of the node's `input` parameter (whatever JSON was wired into it via the
 * `{{nodeId.field}}` binding UI, or typed directly).
 */
declare const input: any;

/**
 * Appends a line to the node's `logs` output. Each argument is joined with a single space;
 * non-string arguments are JSON-stringified.
 */
declare function log(...args: any[]): void;

/** Curated npm allowlist — grown deliberately, package by package. Scripts cannot require()
 * anything themselves; only what's declared here is available. */
declare const _: import("lodash");
declare const dayjs: import("dayjs");

/**
 * The script's last evaluated top-level expression becomes the node's `output`
 * (JSON-serialized) — no `return` needed. A script that wants early-return control flow can
 * wrap itself in an IIFE with an explicit `return`, since the IIFE's own completion value
 * becomes the script's completion value:
 *
 *   (function () {
 *     if (!input.ok) return null;
 *     return input.value * 2;
 *   })();
 */
