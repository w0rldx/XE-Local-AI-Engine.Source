import { mkdirSync, writeFileSync } from "node:fs";
import { get as httpGet } from "node:http";
import { get as httpsGet } from "node:https";
import { isIP } from "node:net";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const defaultOpenapiSpecUrl = "https://localhost:50722/openapi/local/v1/v1.json";
export const defaultOpenapiTimeoutMs = 10_000;
export const defaultOpenapiMaxResponseBytes = 8 * 1024 * 1024;

export function isLoopbackHostname(hostname) {
	const normalizedHostname = hostname.toLowerCase().replace(/^\[|\]$/g, "");
	if (normalizedHostname === "localhost") {
		return true;
	}

	const addressFamily = isIP(normalizedHostname);
	return (
		(addressFamily === 4 && normalizedHostname.split(".")[0] === "127") || (addressFamily === 6 && normalizedHostname === "::1")
	);
}

export function fetchJson(
	specUrl,
	{ timeoutMs = defaultOpenapiTimeoutMs, maxResponseBytes = defaultOpenapiMaxResponseBytes } = {},
) {
	return new Promise((resolveJson, reject) => {
		let settled = false;
		let request;
		let activeResponse;
		let deadline;
		const clearDeadline = () => {
			if (deadline !== undefined) {
				clearTimeout(deadline);
				deadline = undefined;
			}
		};
		const rejectOnce = (error) => {
			if (!settled) {
				settled = true;
				clearDeadline();
				reject(error);
			}
		};
		const abortAndReject = (error) => {
			if (settled) {
				return;
			}
			settled = true;
			clearDeadline();
			activeResponse?.destroy();
			request?.destroy();
			reject(error);
		};
		const resolveOnce = (json) => {
			if (!settled) {
				settled = true;
				clearDeadline();
				resolveJson(json);
			}
		};

		deadline = setTimeout(() => abortAndReject(new Error(`OpenAPI request timed out after ${timeoutMs} ms.`)), timeoutMs);

		let parsedUrl;
		try {
			parsedUrl = new URL(specUrl);
		} catch (error) {
			rejectOnce(error);
			return;
		}
		if (parsedUrl.protocol !== "http:" && parsedUrl.protocol !== "https:") {
			rejectOnce(new Error("OPENAPI_SPEC_URL must use http or https."));
			return;
		}
		const get = parsedUrl.protocol === "http:" ? httpGet : httpsGet;

		try {
			request = get(parsedUrl, { headers: { accept: "application/json" } }, (response) => {
				activeResponse = response;
				const contentLength = Number(response.headers["content-length"] ?? 0);
				if (Number.isFinite(contentLength) && contentLength > maxResponseBytes) {
					abortAndReject(new Error(`OpenAPI response exceeds ${maxResponseBytes} bytes.`));
					return;
				}
				let body = "";
				let receivedBytes = 0;
				response.setEncoding("utf8");
				response.on("data", (chunk) => {
					receivedBytes += Buffer.byteLength(chunk);
					if (receivedBytes > maxResponseBytes) {
						abortAndReject(new Error(`OpenAPI response exceeds ${maxResponseBytes} bytes.`));
						return;
					}
					body += chunk;
				});
				response.on("end", () => {
					if (settled) {
						return;
					}
					if (!response.statusCode || response.statusCode < 200 || response.statusCode >= 300) {
						rejectOnce(new Error(`fetch ${specUrl} failed: ${response.statusCode ?? "unknown"}`));
						return;
					}

					try {
						const json = JSON.parse(body);
						resolveOnce(json);
					} catch (error) {
						rejectOnce(error);
					}
				});
				response.on("error", rejectOnce);
			});
		} catch (error) {
			rejectOnce(error);
			return;
		}

		request.on("error", rejectOnce);
	});
}

// The backend serializes C# long as a plain JSON number. Leaving `format: int64` in the spec makes the
// zod plugin emit `z.coerce.bigint()` validators while the generated TypeScript types say `number`,
// splitting the client contract in two. Normalizing the format here (the single place the committed spec
// is materialized) pins one consistent wire representation: integer → number, validated as a safe
// integer by the generated schemas.
//
// This blanket normalization is SOUND ONLY BECAUSE every remaining int64 field in this API is a value
// provably below Number.MAX_SAFE_INTEGER (2^53): a unix-ms timestamp (`*AtUtc`/`*Utc`), an elapsed
// duration (`*Ms`/`*Seconds`), a discrete count (downloads, parameter count), or a byte size
// (`*Bytes`/RAM/VRAM/disk). The one class of int64 that is NOT safe — an unconstrained 64-bit RNG seed a
// caller can set to any value — is deliberately NOT an int64 on the wire: SamplingOptions.Seed and the
// image request/response seed are represented as STRINGS (see backend SeedValue), so a large seed
// survives the round-trip exactly instead of being silently rounded here and then rejected by z.int().
// If a future int64 field is added that can exceed 2^53, give it the same string treatment rather than
// widening this normalizer.
export function normalizeInt64ToNumber(node) {
	if (Array.isArray(node)) {
		for (const item of node) {
			normalizeInt64ToNumber(item);
		}
		return;
	}
	if (node === null || typeof node !== "object") {
		return;
	}
	if (node.type === "integer" && node.format === "int64") {
		delete node.format;
	}
	for (const value of Object.values(node)) {
		normalizeInt64ToNumber(value);
	}
}

// The document generator emits `paths`, each path item's operations, and `components.schemas` in ENDPOINT and
// TYPE DISCOVERY order — assembly type order, and therefore compile order, and therefore the order the .cs files
// sort in. Renaming or moving a single endpoint file reshuffles thousands of lines of a document whose contract is
// unchanged: measured on 2026-09-21 by renaming two endpoint files and touching nothing else, which moved 777
// lines and flipped `post`/`get` on one path. That buries a real contract change in noise and makes the committed
// document look stale when it is not. Key order carries no meaning in any of those three maps, so sort all three
// here — the single place the committed document is materialized, shared by the live drift check — and the
// committed file then changes only when the contract does.
//
// Exactly these three maps, and NOT a blanket recursive key sort. The order of an ARRAY can be semantic or can
// feed the generated TypeScript: `required`, `enum`, `parameters`, `oneOf`/`anyOf`, `tags` and a schema's
// `properties` order all reach the generated types and schemas, and none of them was measured to drift. Sorting
// them would be a large one-time churn of the generated client bought with no stability gained.
//
// The comparator is code-unit order (`<`), not `localeCompare`: locale-sensitive collation would make the
// committed document depend on the machine's locale, which is the same class of bug one level down.
function sortMapKeys(map) {
	return Object.fromEntries(
		Object.entries(map).sort(([left], [right]) => {
			if (left < right) {
				return -1;
			}
			return left > right ? 1 : 0;
		}),
	);
}

function isPlainObject(value) {
	return value !== null && typeof value === "object" && !Array.isArray(value);
}

export function sortOpenapiDiscoveryOrder(document) {
	if (!isPlainObject(document)) {
		return document;
	}
	if (isPlainObject(document.paths)) {
		const sortedPaths = sortMapKeys(document.paths);
		for (const [route, pathItem] of Object.entries(sortedPaths)) {
			if (isPlainObject(pathItem)) {
				sortedPaths[route] = sortMapKeys(pathItem);
			}
		}
		document.paths = sortedPaths;
	}
	if (isPlainObject(document.components) && isPlainObject(document.components.schemas)) {
		document.components.schemas = sortMapKeys(document.components.schemas);
	}
	return document;
}

function isLoopbackServerUrl(value) {
	try {
		const url = new URL(value);
		return isLoopbackHostname(url.hostname);
	} catch {
		return false;
	}
}

export function normalizeOpenapiDocument(document) {
	normalizeInt64ToNumber(document);
	sortOpenapiDiscoveryOrder(document);
	if (document && typeof document === "object" && !Array.isArray(document) && Array.isArray(document.servers)) {
		const stableServers = document.servers.filter(
			(server) => !server || typeof server !== "object" || !isLoopbackServerUrl(server.url),
		);
		if (stableServers.length > 0) {
			document.servers = stableServers;
		} else {
			delete document.servers;
		}
	}
	return document;
}

export function serializeOpenapi(json) {
	return `${JSON.stringify(json, null, 2)}\n`;
}

export async function fetchNormalizedOpenapi(specUrl, fetchOptions) {
	return normalizeOpenapiDocument(await fetchJson(specUrl, fetchOptions));
}

export async function writeOpenapiSnapshot({
	specUrl = process.env.OPENAPI_SPEC_URL ?? defaultOpenapiSpecUrl,
	outputPath = resolve(process.cwd(), "openapi/v1.json"),
} = {}) {
	const json = await fetchNormalizedOpenapi(specUrl);
	mkdirSync(dirname(outputPath), { recursive: true });
	writeFileSync(outputPath, serializeOpenapi(json));
	return outputPath;
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	writeOpenapiSnapshot()
		.then((outputPath) => process.stdout.write(`wrote ${outputPath}\n`))
		.catch((error) => {
			process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
			process.exitCode = 1;
		});
}
