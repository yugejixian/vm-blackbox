// Prepack guard: DSH validates an installed plugin's package.json with a bare
// JSON.parse(readFileSync(path, 'utf8')) — a UTF-8 BOM (EF BB BF) or a UTF-16
// file makes that throw "Unexpected token", so DSH rejects the whole install
// and rolls the profile back. 0.1.7 shipped a BOM'd manifest and every market
// install failed; this script makes `npm pack` / `npm publish` fail instead of
// shipping another broken manifest.
import { readFileSync, existsSync, statSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const manifestPath = join(root, 'package.json');
const problems = [];

const bytes = readFileSync(manifestPath);
const head = [...bytes.subarray(0, 8)].map((b) => b.toString(16).padStart(2, '0')).join(' ');
if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
  problems.push(`package.json starts with a UTF-8 BOM (${head}) — DSH rejects BOM'd manifests`);
} else if (bytes[0] === 0xff && bytes[1] === 0xfe) {
  problems.push(`package.json is UTF-16LE (${head}) — DSH rejects non-UTF-8 manifests`);
} else if (bytes[0] === 0x7b) {
  // fine: '{'
} else {
  problems.push(`package.json does not start with '{' (${head})`);
}

let manifest = null;
try {
  manifest = JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/u, ''));
} catch (error) {
  problems.push(`JSON.parse(package.json) failed: ${String(error)}`);
}

if (manifest !== null) {
  if (typeof manifest.version !== 'string' || manifest.version.length === 0) {
    problems.push('package.json has no version');
  }
  const patch = manifest.dsh?.bundle?.patch;
  if (typeof patch !== 'string') {
    problems.push('package.json is missing dsh.bundle.patch');
  } else {
    const patchPath = join(root, patch);
    if (!existsSync(patchPath)) problems.push(`dsh.bundle.patch file does not exist: ${patch}`);
  }
}

const agentExe = join(root, 'agent', 'QaTunnel.exe');
if (!existsSync(agentExe)) {
  problems.push('agent/QaTunnel.exe is missing');
} else if (statSync(agentExe).size < 1024) {
  problems.push(`agent/QaTunnel.exe looks truncated (${statSync(agentExe).size} bytes)`);
}

if (problems.length > 0) {
  console.error('prepack manifest check FAILED:');
  for (const problem of problems) console.error(`  - ${problem}`);
  process.exit(1);
}

console.error(`prepack manifest check OK (version ${manifest.version}, no BOM, dsh.bundle.patch present)`);
