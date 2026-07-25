/// <reference types="vitest/config" />

import { devtools } from "@tanstack/devtools-vite";
import { tanstackRouter } from "@tanstack/router-plugin/vite";
import viteReact from "@vitejs/plugin-react";
import UnoCSS from "unocss/vite";
import { defineConfig, normalizePath, type ProxyOptions } from "vite";
import { viteStaticCopy } from "vite-plugin-static-copy";

import aspNetCoreDevelopmentCertificate from "./vite-plugins/aspnetcore-development-certificate";
import tablerDevelopmentBugfix from "./vite-plugins/tabler-development-bugfix";
import fs from "node:fs";
import path from "node:path";

// onnxruntime-web ships the WASM/JSEP binaries the in-browser TTS runtime (Kokoro via @huggingface/transformers)
// loads at runtime. It is a transitive dependency, so under pnpm it is NOT hoisted to the top-level node_modules —
// it lives under node_modules/.pnpm/onnxruntime-web@<version-hash>/. We resolve its absolute `dist` directory (no
// wildcard in the directory path) so the static-copy glob's only wildcard is the filename and the binaries land FLAT
// in the served `/ort` dir (the runtime points ORT's `wasm.wasmPaths` at `/ort/` — see KokoroProvider/TtsWorker).
function resolveOnnxRuntimeWebDistDir(): string {
	const hoisted = path.resolve(__dirname, "node_modules/onnxruntime-web/dist");
	if (fs.existsSync(hoisted)) {
		return hoisted;
	}

	const pnpmDir = path.resolve(__dirname, "node_modules/.pnpm");
	const pnpmEntry = fs.readdirSync(pnpmDir).find((name) => name.startsWith("onnxruntime-web@"));
	if (!pnpmEntry) {
		throw new Error("onnxruntime-web not found in node_modules — install dependencies before building.");
	}

	return path.join(pnpmDir, pnpmEntry, "node_modules/onnxruntime-web/dist");
}

const ortWasmCopySource = normalizePath(path.join(resolveOnnxRuntimeWebDistDir(), "*.{wasm,mjs}"));

// The version shown in the About dialog must match the released artifact without a second hand-maintained constant.
// Directory.Build.props is the single source of truth for the .NET assembly version AND `vpk --packVersion`; parse it
// here and compose `VersionPrefix[-VersionSuffix]` (e.g. "0.1.0-rc.1").
function resolveAppVersion(): string {
	const propsPath = path.resolve(__dirname, "../Directory.Build.props");
	const xml = fs.readFileSync(propsPath, "utf8");
	const prefix = xml.match(/<VersionPrefix>([^<]+)<\/VersionPrefix>/)?.[1]?.trim();
	const suffix = xml.match(/<VersionSuffix>([^<]+)<\/VersionSuffix>/)?.[1]?.trim();
	if (!prefix) {
		throw new Error("VersionPrefix not found in Directory.Build.props — cannot derive the client app version.");
	}

	return suffix ? `${prefix}-${suffix}` : prefix;
}

// Set it on process.env BEFORE Vite resolves env so it lands in `import.meta.env` (Vite exposes VITE_-prefixed
// process.env vars to the client). `??=` lets CI/release override with an explicit value if ever needed.
process.env.VITE_APP_VERSION ??= resolveAppVersion();

const coverageThresholds =
	process.env.VITEST_COVERAGE_CHECK === "true"
		? {
				branches: 35,
				functions: 34,
				lines: 39,
				statements: 38,
			}
		: undefined;

const localProxyHosts = new Set(["localhost", "127.0.0.1", "[::1]", "::1"]);

function requireLocalProxyTarget(value: string | undefined): string {
	if (!value) {
		throw new Error("VITE_PROXY_TARGET is required when running the XE local dev server.");
	}

	const target = new URL(value);
	if (target.protocol !== "http:" && target.protocol !== "https:") {
		throw new Error("VITE_PROXY_TARGET must use http or https.");
	}

	if (!localProxyHosts.has(target.hostname.toLowerCase())) {
		throw new Error("VITE_PROXY_TARGET must point at a localhost backend.");
	}

	return target.toString().replace(/\/$/, "");
}

const stripOrigin: NonNullable<ProxyOptions["configure"]> = (proxy) => {
	proxy.on("proxyReq", (proxyReq) => proxyReq.removeHeader("origin"));
	proxy.on("proxyReqWs", (proxyReq) => proxyReq.removeHeader("origin"));
};

function localProxy(target: string, websocket = false): ProxyOptions {
	return {
		target,
		secure: false,
		changeOrigin: true,
		ws: websocket,
		configure: stripOrigin,
	};
}

export default defineConfig(({ command, mode }) => {
	const proxyTarget = command === "serve" && mode !== "test" ? requireLocalProxyTarget(process.env.VITE_PROXY_TARGET) : undefined;

	return {
		base: "/",
		plugins: [
			devtools(),
			tanstackRouter({ target: "react", autoCodeSplitting: true }),
			viteReact(),
			UnoCSS(),
			aspNetCoreDevelopmentCertificate({ certificateName: "c0re.client.react.web" }),
			tablerDevelopmentBugfix(),
			viteStaticCopy({
				// `stripBase: true` flattens the matched files into `dist/ort/` (otherwise the deep node_modules path is
				// reconstructed under the dest).
				targets: [{ src: ortWasmCopySource, dest: "ort", rename: { stripBase: true } }],
			}),
		],
		// WebGPU needs NO COOP/COEP / cross-origin isolation (only multithreaded SharedArrayBuffer WASM does), so no
		// cross-origin headers are configured here.
		assetsInclude: ["**/*.onnx"],
		// Emit hidden source maps so production stacks captured by the diagnostics snapshot subsystem
		// symbolicate, without exposing a `//# sourceMappingURL` to end users.
		build: { sourcemap: "hidden" },
		// Dedicated ES-module Web Worker (TtsWorker.ts) runs Kokoro synthesis off the main thread; the WebGPU ORT
		// execution provider forbids ORT's `wasm.proxy`, so an own worker is mandatory and must be emitted as ESM.
		worker: { format: "es" },
		optimizeDeps: {
			include: ["@tanstack/react-form-devtools"],
			// esbuild's dep pre-bundling chokes on the WASM/dynamic imports inside these packages; exclude them so
			// Vite serves their real ESM (the worker + Kokoro load onnxruntime-web/transformers at runtime).
			exclude: ["@huggingface/transformers", "onnxruntime-web"],
		},
		resolve: {
			alias: [
				{
					find: "@",
					replacement: path.resolve(__dirname, "./src"),
				},
			],
		},
		server: proxyTarget
			? {
					proxy: {
						// SignalR hubs need the WebSocket upgrade proxied (ws: true); each more-specific hub path MUST come
						// before the generic "/api" entry or Vite matches "/api" first and the upgrade falls through as
						// plain HTTP. A hub missing here connects via the ws:false "/api" entry, whose mishandled upgrade
						// attempts wedge Vite's WebSocket proxy and stall the other (correctly-proxied) hubs too — so EVERY
						// hub mapped in Program.cs (LocalApiRoutes) must be listed. Keep this set in sync with MapHub<…>.
						"/api/local/v1/chat/hub": localProxy(proxyTarget, true),
						"/api/local/v1/scheduler/hub": localProxy(proxyTarget, true),
						"/api/local/v1/preview/hub": localProxy(proxyTarget, true),
						"/api/local/v1/model-fit/gguf/downloads/hub": localProxy(proxyTarget, true),
						"/api/local/v1/model-fit/llamacpp/cuda-build/hub": localProxy(proxyTarget, true),
						"/api/local/v1/model-fit/llamacpp/source-build/hub": localProxy(proxyTarget, true),
						"/api/local/v1/knowledge-base/hub": localProxy(proxyTarget, true),
						"/api/local/v1/images/hub": localProxy(proxyTarget, true),
						"/api/local/v1/images/runtime/source-build/hub": localProxy(proxyTarget, true),
						"/api/local/v1/development/hub": localProxy(proxyTarget, true),
						"/api": localProxy(proxyTarget),
						"/openapi": {
							target: proxyTarget,
							secure: false,
							changeOrigin: true,
						},
					},
				}
			: undefined,
		test: {
			include: ["src/**/*.test.{ts,tsx}"],
			exclude: ["node_modules/**", "dist/**"],
			coverage: {
				provider: "v8",
				reportsDirectory: "coverage/vitest",
				reporter: ["text", "html", "json-summary", "lcov"],
				include: ["src/**/*.{ts,tsx}"],
				exclude: [
					"src/**/*.test.{ts,tsx}",
					"src/**/*.d.ts",
					"src/routeTree.gen.ts",
					"src/core/api/generated/**",
					"src/locales/**",
					"src/test/**",
				],
				thresholds: coverageThresholds,
			},
		},
	};
});
