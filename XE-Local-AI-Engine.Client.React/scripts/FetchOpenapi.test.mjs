import assert from "node:assert/strict";
import http from "node:http";
import test from "node:test";

import { fetchJson, normalizeInt64ToNumber, normalizeOpenapiDocument, serializeOpenapi } from "./FetchOpenapi.mjs";
import { checkLiveOpenapi, firstDifference } from "./CheckLiveOpenapi.mjs";

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

test("removes dynamic top-level loopback servers while preserving non-loopback servers", () => {
	const document = {
		servers: [
			{ url: "http://127.0.0.1:38527" },
			{ url: "https://api.example.test/v1", description: "stable" },
		],
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
