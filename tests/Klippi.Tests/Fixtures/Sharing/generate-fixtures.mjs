// Run from the repository root after this LOCAL, script-free install:
// npm install --prefix local/sharing-fixtures --ignore-scripts --no-audit --no-fund @file-kiwi/node@1.0.9
// node tests/Klippi.Tests/Fixtures/Sharing/generate-fixtures.mjs
// No network/API call occurs in this generator. All keys and media bytes are synthetic test data.
import { encryptChunk } from '../../../../local/sharing-fixtures/node_modules/@file-kiwi/node/dist/index.js';
import { createHash } from 'node:crypto';
import { writeFile } from 'node:fs/promises';

const key = Buffer.from('000102030405060708090a0b0c0d0e0f', 'hex');
const specimens = [
  { name: 'unicode', plaintext: Buffer.from('KeaClip — kūmara 🥝.mp4', 'utf8'), encoding: 'utf8', value: 'KeaClip — kūmara 🥝.mp4' },
  { name: 'two-records', plaintext: Buffer.from(Array.from({ length: 65_520 }, (_, i) => (i * 31 + 7) % 256)), encoding: 'pattern', value: 'byte[i] = (i * 31 + 7) % 256' },
];
const fixtures = [];
for (const specimen of specimens) {
  const encrypted = Buffer.from(await encryptChunk(specimen.plaintext, key.toString('base64url')));
  fixtures.push({ name: specimen.name, encoding: specimen.encoding, value: specimen.value, plaintextLength: specimen.plaintext.length, plaintextSha256: createHash('sha256').update(specimen.plaintext).digest('hex'), ciphertextBase64: encrypted.toString('base64') });
}
const output = { description: 'SYNTHETIC public test key; independent official File.Kiwi SDK interoperability fixtures. Not a share key or uploaded media.', producer: '@file-kiwi/node 1.0.9 / wormhole-crypto 0.3.1 / encryptChunk', keyHex: key.toString('hex'), recordSize: 65_536, fixtures };
await writeFile(new URL('sdk-encryption.json', import.meta.url), JSON.stringify(output, null, 2) + '\n');
