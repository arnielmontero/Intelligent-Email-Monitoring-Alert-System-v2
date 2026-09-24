#!/usr/bin/env node
/**
 * One-off migration: re-encrypts every row in email_credentials from the old
 * CredentialEncryption key to a new one, verifying each round-trip before
 * writing, and independently re-verifying after the write using only the new
 * key. Never logs plaintext or ciphertext — only lengths and row identifiers.
 *
 * Usage:
 *   OLD_KEY=<base64> NEW_KEY=<base64> node scripts/rotate-credential-key.js --dry-run
 *   OLD_KEY=<base64> NEW_KEY=<base64> node scripts/rotate-credential-key.js --apply
 *
 * Requires: `pg` npm package (npm install pg --no-save), and a reachable
 * Postgres matching docker-compose.yml (defaults below match the dev compose
 * setup; override via PG_* env vars if needed).
 */

const crypto = require("crypto");
const { Client } = require("pg");

const NONCE_LEN = 12;
const TAG_LEN = 16;
const NEW_KEY_ID = "v2";

function requireEnv(name) {
  const v = process.env[name];
  if (!v) {
    console.error(`Missing required env var: ${name}`);
    process.exit(1);
  }
  return v;
}

function decryptGcm(keyB64, ciphertext, nonce, tag) {
  const key = Buffer.from(keyB64, "base64");
  if (key.length !== 32) throw new Error("Key must decode to 32 bytes");
  const decipher = crypto.createDecipheriv("aes-256-gcm", key, nonce);
  decipher.setAuthTag(tag);
  return Buffer.concat([decipher.update(ciphertext), decipher.final()]);
}

function encryptGcm(keyB64, plaintext) {
  const key = Buffer.from(keyB64, "base64");
  if (key.length !== 32) throw new Error("Key must decode to 32 bytes");
  const nonce = crypto.randomBytes(NONCE_LEN);
  const cipher = crypto.createCipheriv("aes-256-gcm", key, nonce);
  const ciphertext = Buffer.concat([cipher.update(plaintext), cipher.final()]);
  const tag = cipher.getAuthTag();
  return { ciphertext, nonce, tag };
}

async function main() {
  const apply = process.argv.includes("--apply");
  const dryRun = process.argv.includes("--dry-run") || !apply;

  const oldKey = requireEnv("OLD_KEY");
  const newKey = requireEnv("NEW_KEY");

  const client = new Client({
    host: process.env.PG_HOST || "localhost",
    port: Number(process.env.PG_PORT || 5432),
    user: process.env.PG_USER || "iemas",
    password: requireEnv("PG_PASSWORD"),
    database: process.env.PG_DATABASE || "iemas",
  });

  await client.connect();
  console.log(`Connected. Mode: ${dryRun ? "DRY RUN (no writes)" : "APPLY (will write)"}`);

  try {
    const { rows } = await client.query(
      `SELECT "Id", "EmailAccountId", "EncryptedSecret", "Nonce", "Tag", "KeyId" FROM email_credentials ORDER BY "Id"`
    );
    console.log(`Found ${rows.length} email_credentials row(s).`);

    const plans = [];
    for (const row of rows) {
      const ciphertext = row.EncryptedSecret;
      const nonce = row.Nonce;
      const tag = row.Tag;

      let plaintext;
      try {
        plaintext = decryptGcm(oldKey, ciphertext, nonce, tag);
      } catch (err) {
        console.error(`Row ${row.Id}: FAILED to decrypt with OLD_KEY — aborting, no writes made. (${err.message})`);
        process.exit(1);
      }
      console.log(`Row ${row.Id}: decrypted OK with old key (plaintext length ${plaintext.length}).`);

      const { ciphertext: newCt, nonce: newNonce, tag: newTag } = encryptGcm(newKey, plaintext);

      // Round-trip verify in-memory before touching the DB.
      const verify = decryptGcm(newKey, newCt, newNonce, newTag);
      if (!verify.equals(plaintext)) {
        console.error(`Row ${row.Id}: round-trip verification FAILED — aborting, no writes made.`);
        process.exit(1);
      }
      console.log(`Row ${row.Id}: round-trip verified OK with new key.`);

      plans.push({ id: row.Id, newCt, newNonce, newTag });
      plaintext.fill(0);
      verify.fill(0);
    }

    if (dryRun) {
      console.log(`\nDry run complete. ${plans.length} row(s) would be updated. No writes made.`);
      return;
    }

    await client.query("BEGIN");
    let affected = 0;
    for (const plan of plans) {
      const res = await client.query(
        `UPDATE email_credentials
         SET "EncryptedSecret" = $1, "Nonce" = $2, "Tag" = $3, "KeyId" = $4,
             "RotatedAt" = now(), "UpdatedAt" = now()
         WHERE "Id" = $5`,
        [plan.newCt, plan.newNonce, plan.newTag, NEW_KEY_ID, plan.id]
      );
      affected += res.rowCount;
    }

    if (affected !== plans.length) {
      console.error(`Affected-row mismatch (expected ${plans.length}, got ${affected}) — rolling back.`);
      await client.query("ROLLBACK");
      process.exit(1);
    }

    await client.query("COMMIT");
    console.log(`Committed. ${affected} row(s) updated to KeyId=${NEW_KEY_ID}.`);

    // Independent post-write verification, reading fresh from the DB and
    // decrypting with ONLY the new key (never touches OLD_KEY again).
    const { rows: after } = await client.query(
      `SELECT "Id", "EncryptedSecret", "Nonce", "Tag", "KeyId" FROM email_credentials ORDER BY "Id"`
    );
    for (const row of after) {
      if (row.KeyId !== NEW_KEY_ID) {
        console.error(`Row ${row.Id}: KeyId is "${row.KeyId}", expected "${NEW_KEY_ID}" — INCONSISTENT STATE.`);
        process.exit(1);
      }
      try {
        const pt = decryptGcm(newKey, row.EncryptedSecret, row.Nonce, row.Tag);
        console.log(`Row ${row.Id}: post-write verification OK (new-key-only decrypt, length ${pt.length}).`);
        pt.fill(0);
      } catch (err) {
        console.error(`Row ${row.Id}: post-write verification FAILED with new key — INCONSISTENT STATE. (${err.message})`);
        process.exit(1);
      }
    }

    console.log("\nAll rows independently re-verified with the new key only. Rotation complete.");
    console.log("Next: update CREDENTIAL_ENCRYPTION_KEY in .env to NEW_KEY, restart iemas-api, and confirm email account test-connection / intake still work.");
  } finally {
    await client.end();
  }
}

main().catch((err) => {
  console.error("Unhandled error:", err.message);
  process.exit(1);
});
