import { spawn } from 'node:child_process';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';

const dotnet = process.argv[2] || 'dotnet';
const dll = fileURLToPath(new URL('./bin/Release/net10.0/EntLimit.NativeHost.dll', import.meta.url));
async function check(bytes, closeInput, expectTimeout = false) {
  const child = spawn(dotnet, [dll], {stdio: ['pipe', 'pipe', 'pipe']});
  let output = 0, diagnostics = 0;
  child.stdout.on('data', data => { output += data.length; });
  child.stderr.on('data', data => { diagnostics += data.length; });
  const start = Date.now();
  const completion = new Promise((resolve, reject) => {
    child.on('error', reject);
    child.on('close', code => resolve(code));
  });
  const guard = setTimeout(() => child.kill(), 36000);
  child.stdin.write(bytes);
  if (closeInput) child.stdin.end();
  const code = await completion;
  clearTimeout(guard);
  assert.equal(code, 0, 'invalid or stalled frames exit cleanly');
  assert.equal(output, 0, 'no partial/oversized input response or stdout diagnostic');
  assert.equal(diagnostics, 0, 'no unhandled exceptions');
  if (expectTimeout) assert.ok(Date.now() - start >= 29000 && Date.now() - start < 35000, 'stalled frame bounded to 30 seconds');
}
await check(Buffer.from([0xff, 0xff, 0xff, 0x7f]), true);
await check(Buffer.from([20, 0, 0, 0, 0x7b]), true);
await check(Buffer.from([1]), false, true);
console.log('Native framing: oversized, truncated and stalled frame checks passed.');
