// Pack a Chromium extension directory/zip into a signed CRX3 file.
//
// CRX3 layout (Chromium components/crx_file):
//   magic "Cr24" | version 3 | uint32 LE header size | protobuf header | ZIP payload
//
// The signed message is:
//   "CRX3 SignedData\0" | uint32 LE signed-header size | signed-header data | ZIP payload
//
// Private keys are local-only and never written into this repository.
//
// Usage:
//   node tools/force-install/pack-crx.mjs --zip <file.zip> --key <key.pem> --out <file.crx>

import fs from 'node:fs';
import crypto from 'node:crypto';

function parseArgs(argv) {
  const args = {};
  for (let i = 2; i < argv.length; i += 2) {
    if (!argv[i].startsWith('--')) {
      throw new Error(`Unexpected argument: ${argv[i]}`);
    }
    const key = argv[i].slice(2);
    if (i + 1 >= argv.length) {
      throw new Error(`Missing value for --${key}`);
    }
    args[key] = argv[i + 1];
  }
  if (!args.zip || !args.key || !args.out) {
    throw new Error('Required arguments: --zip <file> --key <pem> --out <crx>');
  }
  return args;
}

function varint(value) {
  let v = value >>> 0;
  const out = [];
  while (v >= 0x80) {
    out.push((v & 0x7f) | 0x80);
    v >>>= 7;
  }
  out.push(v);
  return Buffer.from(out);
}

function lengthPrefixed(bytes) {
  return Buffer.concat([varint(bytes.length), bytes]);
}

function field(number, bytes) {
  // All protobuf fields used here are wire type 2 (length-delimited).
  const tag = Buffer.from(varint((number << 3) | 2));
  return Buffer.concat([tag, lengthPrefixed(bytes)]);
}

function chromiumIdFromHash(hash) {
  const alphabet = 'abcdefghijklmnop';
  let id = '';
  for (let i = 0; i < 16; i += 1) {
    id += alphabet[(hash[i] >> 4) & 0x0f];
    id += alphabet[hash[i] & 0x0f];
  }
  return id;
}

const args = parseArgs(process.argv);

if (!fs.existsSync(args.zip)) {
  throw new Error(`ZIP file not found: ${args.zip}`);
}

let pem = fs.readFileSync(args.key, 'utf8');
if (fs.existsSync(args.key)) {
  // Normalize any accidental BOM or CRLF; PEM parsers accept both but we keep it tidy.
  pem = pem.replace(/^\uFEFF/, '').replace(/\r\n/g, '\n');
}

const privateKey = crypto.createPrivateKey({ key: pem, format: 'pem', type: 'pkcs8' });
const publicKey = crypto.createPublicKey(privateKey);
const publicKeyDer = publicKey.export({ format: 'der', type: 'spki' });
const publicKeyBase64 = publicKeyDer.toString('base64');

const hash = crypto.createHash('sha256').update(publicKeyDer).digest();
const crxIdBytes = hash.subarray(0, 16);
const chromiumExtensionId = chromiumIdFromHash(hash);

const zip = fs.readFileSync(args.zip);

// SignedData { field 1: bytes crx_id = 16-byte hash prefix }
const signedHeaderData = Buffer.concat([
  Buffer.from([0x0a, 0x10]),
  crxIdBytes,
]);

const message = Buffer.concat([
  Buffer.from('CRX3 SignedData\0', 'ascii'),
  Buffer.from([signedHeaderData.length & 0xff, (signedHeaderData.length >> 8) & 0xff,
    (signedHeaderData.length >> 16) & 0xff, (signedHeaderData.length >> 24) & 0xff]),
  signedHeaderData,
  zip,
]);

const signature = crypto.sign('sha256', message, {
  key: privateKey,
  padding: crypto.constants.RSA_PKCS1_PADDING,
});

// AsymmetricKeyProof { field 1: public_key, field 2: signature }
const proofMessage = Buffer.concat([
  field(1, publicKeyDer),
  field(2, signature),
]);

const header = Buffer.concat([
  field(2, proofMessage),
  field(10000, signedHeaderData),
]);

const headerSize = Buffer.alloc(4);
headerSize.writeUInt32LE(header.length, 0);

const crx = Buffer.concat([
  Buffer.from('Cr24', 'ascii'),
  Buffer.from([3, 0, 0, 0]),
  headerSize,
  header,
  zip,
]);

fs.writeFileSync(args.out, crx);

process.stdout.write(`${JSON.stringify({
  chromiumExtensionId,
  publicKeyBase64,
  zipBytes: zip.length,
  crxBytes: crx.length,
}, null, 2)}\n`);
