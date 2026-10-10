'use strict';

// Sandboxed execution loop for Agentivity.Module.CodeExecution.
//
// Spawned by NodeJsWorker.Start with `--permission --allow-fs-read=<this directory>` — the
// process itself can only read files under here (this script + its own node_modules), nothing
// else on disk, no network, no child_process. Reads newline-delimited JSON requests from stdin,
// writes newline-delimited JSON responses to stdout. One request is fully handled (including any
// vm timeout) before the next line is read, matching the host side's one-execution-at-a-time
// usage of each worker.

const vm = require('vm');
const readline = require('readline');

// The curated allowlist — deliberately small, grown package by package, never "whatever the
// script asks for". Scripts cannot require() anything themselves; these are the only libraries
// available, injected as blessed globals into the sandbox below.
const blessed = {
  _: require('lodash'),
  dayjs: require('dayjs'),
};

const rl = readline.createInterface({ input: process.stdin, terminal: false });

rl.on('line', (line) => {
  let request;
  try {
    request = JSON.parse(line);
  } catch {
    // Can't even parse the request — nothing sensible to respond with an id for; drop it. The
    // host side will see this as "worker exited without responding" (via its own timeout) rather
    // than hang forever, since a genuinely malformed request from our own host would be a bug on
    // that side, not something a user's script could ever trigger.
    return;
  }

  const { id, code, input, timeoutMs } = request;
  const logs = [];
  const log = (...args) => logs.push(args.map((a) => (typeof a === 'string' ? a : safeStringify(a))).join(' '));

  const sandbox = { input, log, ...blessed };
  vm.createContext(sandbox);

  try {
    // No function wrapper: run the code as plain top-level statements in its own context, with
    // `input`/`log`/the blessed globals already present. This is what makes "the last evaluated
    // expression becomes output" work at all — vm's completion-value semantics (same as the
    // REPL) only apply to top-level script evaluation, never to a function body (a function with
    // no `return` always yields undefined, regardless of its last statement). A script that wants
    // early-return control flow can still get it by wrapping itself in an IIFE with an explicit
    // `return` — that's a single top-level expression, so ITS completion value (whatever the
    // IIFE returns) becomes the script's own completion value.
    const script = new vm.Script(code, { filename: 'code-node.js' });
    const output = script.runInContext(sandbox, { timeout: timeoutMs });
    respond({ id, ok: true, output: toPlainValue(output), logs });
  } catch (err) {
    respond({ id, ok: false, error: describeError(err), logs });
  }
});

function respond(message) {
  process.stdout.write(JSON.stringify(message) + '\n');
}

function describeError(err) {
  if (err instanceof Error) return err.message;
  return String(err);
}

function safeStringify(value) {
  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

// vm's runInContext can return values that aren't directly JSON-serializable as-is in edge cases
// (e.g. undefined) — normalize to something JSON.stringify (used by the host side) always handles
// the same way a plain object would.
function toPlainValue(value) {
  if (value === undefined) return null;
  return value;
}
