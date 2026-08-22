import { checkLiveOpenapi, firstDifference } from "./CheckLiveOpenapi.mjs";
import {
	fetchJson,
	isLoopbackHostname,
	normalizeInt64ToNumber,
	normalizeOpenapiDocument,
	serializeOpenapi,
} from "./FetchOpenapi.mjs";
import assert from "node:assert/strict";
import { generateKeyPairSync, sign } from "node:crypto";
import { readFileSync } from "node:fs";
import http from "node:http";
import https from "node:https";
import test from "node:test";

test("normalizes nested int64 integer schemas without changing other formats", () => {
	const document = {
		components: {
			schemas: {
				Envelope: {
					type: "object",
					properties: {
						createdAtUtc: { type: "integer", format: "int64" },
						id: { type: "string", format: "uuid" },
						items: { type: "array", items: { type: "integer", format: "int64" } },
					},
				},
			},
		},
	};

	normalizeInt64ToNumber(document);

	assert.deepEqual(document.components.schemas.Envelope.properties.createdAtUtc, { type: "integer" });
	assert.deepEqual(document.components.schemas.Envelope.properties.items.items, { type: "integer" });
	assert.deepEqual(document.components.schemas.Envelope.properties.id, { type: "string", format: "uuid" });
});

test("reports the first deterministic snapshot difference", () => {
	const committed = serializeOpenapi({ openapi: "3.1.0", info: { title: "Committed" } });
	const live = serializeOpenapi({ openapi: "3.1.0", info: { title: "Live" } });

	assert.deepEqual(firstDifference(committed, live), {
		line: 4,
		expected: '    "title": "Committed"',
		actual: '    "title": "Live"',
	});
});

test("requires an explicit URL for the live OpenAPI gate", async () => {
	await assert.rejects(checkLiveOpenapi({ specUrl: "" }), /OPENAPI_SPEC_URL is required/);
});

test("recognizes only literal loopback hostnames and addresses", () => {
	for (const hostname of ["localhost", "LOCALHOST", "127.0.0.1", "127.24.1.9", "[::1]"]) {
		assert.equal(isLoopbackHostname(hostname), true, hostname);
	}
	for (const hostname of ["example.test", "localhost.example.test", "192.168.1.5", "::2"]) {
		assert.equal(isLoopbackHostname(hostname), false, hostname);
	}
});

test("uses the standard Node HTTPS client without a certificate-validation bypass", () => {
	const source = readFileSync(new URL("./FetchOpenapi.mjs", import.meta.url), "utf8");
	assert.match(source, /import \{ get as httpsGet \} from "node:https"/);
	assert.match(source, /parsedUrl\.protocol === "http:" \? httpGet : httpsGet/);
	for (const bypass of ["OPENAPI_INSECURE", "rejectUnauthorized", "NODE_TLS_REJECT_UNAUTHORIZED", "HttpsAgent"]) {
		assert.equal(source.includes(bypass), false, bypass);
	}
});

test("removes dynamic top-level loopback servers while preserving non-loopback servers", () => {
	const document = {
		servers: [{ url: "http://127.0.0.1:38527" }, { url: "https://api.example.test/v1", description: "stable" }],
	};

	normalizeOpenapiDocument(document);

	assert.deepEqual(document.servers, [{ url: "https://api.example.test/v1", description: "stable" }]);
	const loopbackOnly = { servers: [{ url: "https://localhost:50722" }] };
	normalizeOpenapiDocument(loopbackOnly);
	assert.equal("servers" in loopbackOnly, false);
});

async function listen(handler) {
	const server = http.createServer(handler);
	await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
	const address = server.address();
	if (!address || typeof address === "string") {
		throw new Error("Could not allocate test server port.");
	}
	return { server, url: `http://127.0.0.1:${address.port}/openapi.json` };
}

async function close(server) {
	server.closeAllConnections();
	await new Promise((resolve, reject) => server.close((error) => (error ? reject(error) : resolve())));
}

function derLength(length) {
	if (length < 128) {
		return Buffer.from([length]);
	}
	const bytes = [];
	for (let remaining = length; remaining > 0; remaining >>= 8) {
		bytes.unshift(remaining & 0xff);
	}
	return Buffer.from([0x80 | bytes.length, ...bytes]);
}

function der(tag, ...parts) {
	const content = Buffer.concat(parts);
	return Buffer.concat([Buffer.from([tag]), derLength(content.length), content]);
}

function oid(...values) {
	const bytes = [values[0] * 40 + values[1]];
	for (const value of values.slice(2)) {
		const encoded = [value & 0x7f];
		for (let remaining = value >> 7; remaining > 0; remaining >>= 7) {
			encoded.unshift(0x80 | (remaining & 0x7f));
		}
		bytes.push(...encoded);
	}
	return der(0x06, Buffer.from(bytes));
}

function createUntrustedLocalCertificate() {
	const { privateKey, publicKey } = generateKeyPairSync("rsa", { modulusLength: 2048 });
	const signatureAlgorithm = der(0x30, oid(1, 2, 840, 113549, 1, 1, 11), der(0x05));
	const commonName = der(0x30, der(0x31, der(0x30, oid(2, 5, 4, 3), der(0x0c, Buffer.from("localhost")))));
	const validity = der(0x30, der(0x17, Buffer.from("200101000000Z")), der(0x17, Buffer.from("491231235959Z")));
	const subjectAlternativeName = der(
		0xa3,
		der(0x30, der(0x30, oid(2, 5, 29, 17), der(0x04, der(0x30, der(0x87, Buffer.from([127, 0, 0, 1])))))),
	);
	const tbsCertificate = der(
		0x30,
		der(0xa0, der(0x02, Buffer.from([2]))),
		der(0x02, Buffer.from([1])),
		signatureAlgorithm,
		commonName,
		validity,
		commonName,
		publicKey.export({ type: "spki", format: "der" }),
		subjectAlternativeName,
	);
	const certificate = der(
		0x30,
		tbsCertificate,
		signatureAlgorithm,
		der(0x03, Buffer.from([0]), sign("sha256", tbsCertificate, privateKey)),
	);
	const encodedCertificate = certificate
		.toString("base64")
		.match(/.{1,64}/g)
		?.join("\n");
	return {
		key: privateKey.export({ type: "pkcs8", format: "pem" }),
		cert: `-----BEGIN CERTIFICATE-----\n${encodedCertificate}\n-----END CERTIFICATE-----\n`,
	};
}

async function listenHttps(handler) {
	const server = https.createServer(createUntrustedLocalCertificate(), handler);
	await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
	const address = server.address();
	if (!address || typeof address === "string") {
		throw new Error("Could not allocate test HTTPS server port.");
	}
	return { server, url: `https://127.0.0.1:${address.port}/openapi.json` };
}

test("fetches a live OpenAPI document with the standard Node HTTP client", async () => {
	const expected = { openapi: "3.1.0", info: { title: "Live" } };
	const { server, url } = await listen((_request, response) => {
		response.writeHead(200, { "content-type": "application/json" });
		response.end(JSON.stringify(expected));
	});
	try {
		assert.deepEqual(await fetchJson(url), expected);
	} finally {
		await close(server);
	}
});

test("rejects an untrusted local HTTPS certificate through normal Node validation", async () => {
	const { server, url } = await listenHttps((_request, response) => {
		response.writeHead(200, { "content-type": "application/json" });
		response.end('{"openapi":"3.1.0"}');
	});
	try {
		await assert.rejects(fetchJson(url), (error) => {
			assert.equal(error.code, "DEPTH_ZERO_SELF_SIGNED_CERT");
			return true;
		});
	} finally {
		await close(server);
	}
});

test("rejects an OpenAPI response above the byte limit", async () => {
	const { server, url } = await listen((_request, response) => {
		response.write('{"value":"');
		response.write("x".repeat(100));
		response.end('"}');
	});
	try {
		await assert.rejects(fetchJson(url, { maxResponseBytes: 32 }), /exceeds 32 bytes/);
	} finally {
		await close(server);
	}
});

test("aborts an OpenAPI response whose declared length exceeds the byte limit", async () => {
	let resolveResponseClosed;
	const responseClosed = new Promise((resolve) => {
		resolveResponseClosed = resolve;
	});
	const { server, url } = await listen((_request, response) => {
		response.writeHead(200, { "content-length": "1024", "content-type": "application/json" });
		response.flushHeaders();
		response.once("close", resolveResponseClosed);
	});
	try {
		await assert.rejects(fetchJson(url, { maxResponseBytes: 32 }), /exceeds 32 bytes/);
		await Promise.race([
			responseClosed,
			new Promise((_, reject) => setTimeout(() => reject(new Error("Oversized response was not aborted.")), 250)),
		]);
	} finally {
		await close(server);
	}
});

test("rejects an OpenAPI request that exceeds the timeout", async () => {
	const { server, url } = await listen(() => undefined);
	try {
		await assert.rejects(fetchJson(url, { timeoutMs: 25 }), /timed out after 25 ms/);
	} finally {
		await close(server);
	}
});

test("applies the timeout as a total deadline during a slow streaming response", async () => {
	const { server, url } = await listen((_request, response) => {
		response.writeHead(200, { "content-type": "application/json" });
		response.write('{"value":"');
		const interval = setInterval(() => response.write("x"), 5);
		response.once("close", () => clearInterval(interval));
	});
	try {
		await assert.rejects(fetchJson(url, { timeoutMs: 30 }), /timed out after 30 ms/);
	} finally {
		await close(server);
	}
});
