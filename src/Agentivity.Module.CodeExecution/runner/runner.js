'use strict';

// Sandboxed execution loop for Agentivity.Module.CodeExecution.
//
// Spawned by NodeJsWorker.Start with `--permission --allow-fs-read=<this directory>` and
// `--disallow-code-generation-from-strings`, in an environment emptied of the instance's: the
// process can only read files under here (this script + its own node_modules), writes nothing,
// starts no process, and no code is made from strings, so a script does not get out of its context
// through the Function of this process. The network is not closed by Node itself: the host closes
// it where scripts are not trusted. Reads newline-delimited JSON requests from stdin, writes
// newline-delimited JSON responses to stdout; the host gives each worker one script, then replaces it.

const vm = require('vm');
const readline = require('readline');

// The curated allowlist — deliberately small, grown package by package, never "whatever the
// script asks for". Scripts cannot require() anything themselves; these are the only libraries
// available, injected as blessed globals into the sandbox below.
const blessed = {
  _: require('lodash'),
  dayjs: require('dayjs'),
};

// What a script may send back: beyond it, the output is refused rather than carried to the host.
const MAX_RESPONSE_BYTES = 2 * 1024 * 1024;

// A script loads nothing (no package manager, no network, nothing to read outside this folder, and loading code at run time is
// how a supply chain gets in): what it has is the curated list above, already there. Adding a library is a decision, and a new
// image — never a script's request.
const NO_IMPORT = `import is not available in a Code step: use the libraries already there: ${Object.keys(blessed)
  .map((name) => (name === '_' ? '_ (lodash)' : name))
  .join(', ')}.`;

// A promise a script left rejected, unawaited, would otherwise end this process: it is only noted.
process.on('unhandledRejection', (reason) => {
  process.stderr.write(`unhandled rejection in a script: ${describeError(reason)}\n`);
});

const rl = readline.createInterface({ input: process.stdin, terminal: false });

rl.on('line', async (line) => {
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
  // Inside the context too, no code from strings (eval, new Function) and no WebAssembly.
  vm.createContext(sandbox, { codeGeneration: { strings: false, wasm: false } });

  try {
    // No function wrapper: run the code as plain top-level statements in its own context, with
    // `input`/`log`/the blessed globals already present. This is what makes "the last evaluated
    // expression becomes output" work at all — vm's completion-value semantics (same as the
    // REPL) only apply to top-level script evaluation, never to a function body (a function with
    // no `return` always yields undefined, regardless of its last statement). A script that wants
    // early-return control flow can still get it by wrapping itself in an IIFE with an explicit
    // `return` — that's a single top-level expression, so ITS completion value (whatever the
    // IIFE returns) becomes the script's own completion value.
    // No module is given to import(): Node refuses it (ERR_VM_DYNAMIC_IMPORT_CALLBACK_MISSING), told as NO_IMPORT below.
    const script = new vm.Script(code, { filename: 'code-node.js' });
    let output = script.runInContext(sandbox, { timeout: timeoutMs });
    // A script that gives a promise (an async function it calls) gives what the promise resolves to, within its time.
    if (output !== null && typeof output === 'object' && typeof output.then === 'function') {
      output = await withinTime(output, timeoutMs);
    }
    respond({ id, ok: true, output: toPlainValue(output), logs });
  } catch (err) {
    respond({ id, ok: false, error: describeError(err), logs });
  }
});

function withinTime(promise, timeoutMs) {
  let timer;
  const late = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(`Script execution timed out after ${timeoutMs}ms`)), timeoutMs);
  });
  return Promise.race([promise, late]).finally(() => clearTimeout(timer));
}

function respond(message) {
  let text = JSON.stringify(message);
  if (Buffer.byteLength(text) > MAX_RESPONSE_BYTES) {
    const size = (Buffer.byteLength(text) / 1048576).toFixed(1);
    text = JSON.stringify({ id: message.id, ok: false, error: `The output is too large (${size} MB): a script sends back at most ${MAX_RESPONSE_BYTES / 1048576} MB.`, logs: [] });
  }
  process.stdout.write(text + '\n');
}

function describeError(err) {
  if (err && err.code === 'ERR_VM_DYNAMIC_IMPORT_CALLBACK_MISSING') return NO_IMPORT;
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
