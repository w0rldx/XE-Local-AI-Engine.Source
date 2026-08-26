import assert from "node:assert/strict";
import http from "node:http";
import https from "node:https";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { createLogger, createServer } from "vite";

const reactRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const requestTimeoutMilliseconds = 5_000;

function request(url) {
	const client = url.protocol === "https:" ? https : http;
	return new Promise((resolve, reject) => {
		const request = client.get(
			url,
			{ rejectUnauthorized: false, signal: AbortSignal.timeout(requestTimeoutMilliseconds) },
			(response) => {
				const chunks = [];
				response.on("data", (chunk) => chunks.push(chunk));
				response.on("error", reject);
				response.on("end", () => {
					resolve({ statusCode: response.statusCode, body: Buffer.concat(chunks).toString("utf8") });
				});
			},
		);
		request.on("error", reject);
	});
}

function importSpecifiers(source) {
	return [...source.matchAll(/\b(?:from\s*|import\s*)["']([^"']+)["']/gu)].map((match) => match[1]);
}

function requiredImport(source, description, predicate) {
	const specifier = importSpecifiers(source).find(predicate);
	assert.ok(specifier, `Transformed module did not import ${description}`);
	return specifier;
}

test("Vite dev server traverses Mantine through the light Chroma facade without optimizer errors", { timeout: 20_000 }, async () => {
	const viteErrors = [];
	const logger = createLogger("info", { allowClearScreen: false });
	const forwardError = logger.error.bind(logger);
	logger.error = (message, options) => {
		viteErrors.push(message);
		forwardError(message, options);
	};

	const server = await createServer({
		root: reactRoot,
		configFile: path.join(reactRoot, "vite.config.ts"),
		customLogger: logger,
		server: { host: "127.0.0.1", port: 0, strictPort: true },
	});

	try {
		await server.listen();
		const baseUrl = server.resolvedUrls?.local[0];
		assert.ok(baseUrl, "Vite did not publish a local development URL");

		const colorUtils = await request(new URL("/src/modules/theme-configurator/components/ColorUtils.ts", baseUrl));
		assert.equal(colorUtils.statusCode, 200, colorUtils.body);
		const mantineSpecifier = requiredImport(
			colorUtils.body,
			"the optimized @mantine/colors-generator dependency",
			(specifier) => specifier.includes("@mantine_colors-generator"),
		);

		const mantine = await request(new URL(mantineSpecifier, baseUrl));
		assert.equal(mantine.statusCode, 200, mantine.body);
		const facadeSpecifier = requiredImport(
			mantine.body,
			"the virtual Mantine Chroma facade",
			(specifier) => specifier.includes("mantine-chroma-generator"),
		);

		const facade = await request(new URL(facadeSpecifier, baseUrl));
		assert.equal(facade.statusCode, 200, facade.body);
		const chromaSpecifiers = [
			requiredImport(facade.body, "Chroma's light entry", (specifier) => specifier.includes("index.umd.light.js")),
			requiredImport(facade.body, "Chroma's LCH support", (specifier) => specifier.includes("/src/io/lch/index.js")),
			requiredImport(facade.body, "Chroma's saturation support", (specifier) =>
				specifier.includes("/src/ops/saturate.js"),
			),
		];

		const chromaModules = await Promise.all(chromaSpecifiers.map((specifier) => request(new URL(specifier, baseUrl))));
		for (const chromaModule of chromaModules) {
			assert.equal(chromaModule.statusCode, 200, chromaModule.body);
		}

		await server.waitForRequestsIdle();
		assert.deepEqual(viteErrors, []);
	} finally {
		await server.close();
	}
});
