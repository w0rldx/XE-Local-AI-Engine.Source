import { mkdirSync, writeFileSync } from "node:fs";
import { get as httpGet } from "node:http";
import { Agent as HttpsAgent, get as httpsGet } from "node:https";
import { resolve } from "node:path";

const url = process.env.OPENAPI_SPEC_URL ?? "https://localhost:5001/openapi/v1/v1.json";
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

try {
	const json = await fetchJson(url);
	mkdirSync(resolve(process.cwd(), "openapi"), { recursive: true });
	writeFileSync(out, `${JSON.stringify(json, null, 2)}\n`);
	process.stdout.write(`wrote ${out}\n`);
} catch (error) {
	process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
	process.exit(1);
}
