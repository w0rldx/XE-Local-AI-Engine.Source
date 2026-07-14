import { mkdirSync, writeFileSync } from "node:fs";
import { get as httpGet } from "node:http";
import { Agent as HttpsAgent, get as httpsGet } from "node:https";
import { resolve } from "node:path";

const url = process.env.OPENAPI_SPEC_URL ?? "https://localhost:50722/openapi/local/v1/v1.json";
const out = resolve(process.cwd(), "openapi/v1.json");

function fetchJson(specUrl) {
	return new Promise((resolveJson, reject) => {
		const parsedUrl = new URL(specUrl);
		const get = parsedUrl.protocol === "http:" ? httpGet : httpsGet;
		const agent =
			parsedUrl.protocol === "https:" && process.env.OPENAPI_INSECURE === "1"
				? new HttpsAgent({ rejectUnauthorized: false })
				: undefined;

		const request = get(parsedUrl, { agent, headers: { accept: "application/json" } }, (response) => {
			let body = "";
			response.setEncoding("utf8");
			response.on("data", (chunk) => {
				body += chunk;
			});
			response.on("end", () => {
				if (!response.statusCode || response.statusCode < 200 || response.statusCode >= 300) {
					reject(new Error(`fetch ${specUrl} failed: ${response.statusCode ?? "unknown"}`));
					return;
				}

				try {
					resolveJson(JSON.parse(body));
				} catch (error) {
					reject(error);
				}
			});
		});

		request.on("error", reject);
	});
}

// The backend serializes C# long as a plain JSON number, and every int64 field in this API is a
// millisecond timestamp, duration, count, or byte size — far below Number.MAX_SAFE_INTEGER (2^53).
// Leaving `format: int64` in the spec makes the zod plugin emit `z.coerce.bigint()` validators while
// the generated TypeScript types say `number`, splitting the client contract in two. Normalizing the
// format here (the single place the committed spec is materialized) pins one consistent wire
// representation: integer → number, validated as a safe integer by the generated schemas.
function normalizeInt64ToNumber(node) {
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

try {
	const json = await fetchJson(url);
	normalizeInt64ToNumber(json);
	mkdirSync(resolve(process.cwd(), "openapi"), { recursive: true });
	writeFileSync(out, `${JSON.stringify(json, null, 2)}\n`);
	process.stdout.write(`wrote ${out}\n`);
} catch (error) {
	process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
	process.exit(1);
}
