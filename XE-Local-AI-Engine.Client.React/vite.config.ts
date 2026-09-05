/// <reference types="vitest/config" />

import { devtools } from "@tanstack/devtools-vite";
import { tanstackRouter } from "@tanstack/router-plugin/vite";
import viteReact from "@vitejs/plugin-react";
import UnoCSS from "unocss/vite";
import { defineConfig, loadEnv, type Plugin, type ProxyOptions } from "vite";

import signalrProxyPaths from "./config/signalr-proxy-paths.json" with { type: "json" };
import { createFrontendComponentManifestPlugin } from "./scripts/FrontendComponentManifest.mjs";
import aspNetCoreDevelopmentCertificate from "./vite-plugins/aspnetcore-development-certificate.ts";
import tablerDevelopmentBugfix from "./vite-plugins/tabler-development-bugfix.ts";
import fs from "node:fs";
import { createRequire } from "node:module";
import path from "node:path";

// The version shown in the About dialog must match the released artifact without a second hand-maintained constant.
// eng/ReleaseVersion.props is the single source of truth for the .NET assembly version AND `vpk --packVersion`; parse it
// here and compose `VersionPrefix[-VersionSuffix]` (e.g. "0.1.0-rc.1").
function resolveAppVersion(): string {
	const propsPath = path.resolve(import.meta.dirname, "../eng/ReleaseVersion.props");
	const xml = fs.readFileSync(propsPath, "utf8");
	const prefix = xml.match(/<VersionPrefix>([^<]+)<\/VersionPrefix>/)?.[1]?.trim();
	const suffix = xml.match(/<VersionSuffix>([^<]+)<\/VersionSuffix>/)?.[1]?.trim();
	if (!prefix) {
		throw new Error("VersionPrefix not found in eng/ReleaseVersion.props — cannot derive the client app version.");
	}

	return suffix ? `${prefix}-${suffix}` : prefix;
}

// Set it on process.env BEFORE Vite resolves env so it lands in `import.meta.env` (Vite exposes VITE_-prefixed
// process.env vars to the client). `??=` lets CI/release override with an explicit value if ever needed.
process.env.VITE_APP_VERSION ??= resolveAppVersion();
// Keep clean-clone production builds usable without requiring contributors to materialize `.env.template` first.
// The same default is enforced by Environment.ts; setting it here also lets Vite resolve the index.html placeholder.
process.env.VITE_APP_TITLE ??= "XE Local AI Engine";

// Coverage floor — a RATCHET: raise it as coverage grows, never lower it to make a red run green. Kept ~10 points
// under the current actuals (70.5 / 65.3 / 62.3 / 71.2 at the last raise) so ordinary refactors do not trip it.
// Last raised 2026-08-15.
const coverageThresholds =
	process.env.VITEST_COVERAGE_CHECK === "true"
		? {
				branches: 55,
				functions: 52,
				lines: 60,
				statements: 60,
			}
		: undefined;

const defaultProxyTarget = "https://localhost:50722";
const localProxyHosts = new Set(["localhost", "127.0.0.1", "[::1]", "::1"]);
// The palette generator needs only Chroma's light entry plus LCH saturation. Resolving its peer from the package that
// owns it keeps the application bundle from loading Chroma's unrelated color catalogs and generators.
const requireFromColorsGenerator = createRequire(createRequire(import.meta.url).resolve("@mantine/colors-generator"));
const chromaJsRoot = path.dirname(path.dirname(requireFromColorsGenerator.resolve("chroma-js")));
const chromaFacadeId = "virtual:mantine-chroma-generator";
const resolvedChromaFacadeId = `\0${chromaFacadeId}`;
const chromaLightId = `${chromaFacadeId}/light`;
const chromaLchId = `${chromaFacadeId}/lch`;
const chromaSaturateId = `${chromaFacadeId}/saturate`;

function chromaColorsGeneratorPlugin(): Plugin {
	return {
		name: "chroma-colors-generator-light",
		enforce: "pre",
		resolveId(source) {
			switch (source) {
				case "chroma-js":
				case chromaFacadeId:
					return resolvedChromaFacadeId;
				case chromaLightId:
					return path.join(chromaJsRoot, "index.umd.light.js");
				case chromaLchId:
					return path.join(chromaJsRoot, "src/io/lch/index.js");
				case chromaSaturateId:
					return path.join(chromaJsRoot, "src/ops/saturate.js");
				default:
					return null;
			}
		},
		load(id) {
			if (id !== resolvedChromaFacadeId) {
				return null;
			}

			return `
import chroma from "${chromaLightId}";
import "${chromaLchId}";
import "${chromaSaturateId}";

export default chroma;
`;
		},
	};
}

function resolveLocalProxyTarget(value: string | undefined): string {
	const target = new URL(value || defaultProxyTarget);
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
	const environment = { ...loadEnv(mode, process.cwd(), ""), ...process.env };
	const proxyTarget = command === "serve" && mode !== "test" ? resolveLocalProxyTarget(environment.VITE_PROXY_TARGET) : undefined;

	return {
		base: "/",
		plugins: [
			chromaColorsGeneratorPlugin(),
			createFrontendComponentManifestPlugin(),
			devtools(),
			tanstackRouter({ target: "react", autoCodeSplitting: true }),
			viteReact(),
			UnoCSS(),
			aspNetCoreDevelopmentCertificate({ certificateName: "c0re.client.react.web" }),
			tablerDevelopmentBugfix(),
		],
		// Emit hidden source maps so production stacks captured by the diagnostics snapshot subsystem
		// symbolicate, without exposing a `//# sourceMappingURL` to end users.
		build: {
			sourcemap: "hidden",
			rolldownOptions: {
				output: {
					codeSplitting: {
						groups: [
							// Everything from monaco-editor (core + the lazily-loaded Monarch grammars) lands in ONE chunk named
							// `monaco-editor-*`, which is only fetched when CodeEditor first mounts. `scripts/CheckBundleBudget.mjs`
							// measures that name (plus the editor worker) under `lazyEditorJavaScriptBytes` instead of the app budget.
							{ name: "monaco-editor", test: /node_modules[\\/]monaco-editor[\\/]/ },
						],
					},
				},
			},
		},
		optimizeDeps: {
			exclude: ["chroma-js"],
			include: ["@tanstack/react-form-devtools"],
		},
		resolve: {
			alias: [
				{
					find: "@",
					replacement: path.resolve(import.meta.dirname, "./src"),
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
						...Object.fromEntries(signalrProxyPaths.map((hubPath) => [hubPath, localProxy(proxyTarget, true)])),
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
			// Raised from the 5s default because several store-hydration tests must re-import a component graph
			// per test (`vi.resetModules()` + `await import(...)`) to exercise the read-localStorage-at-module-init
			// path — see ChatSamplingOptionsDialog.test.tsx and NodeSettings.sampling.test.tsx. That import pays a
			// cold transform + evaluation of Mantine, @tabler/icons-react, react-i18next and the feature graph, and
			// it is charged to the test's own timeout. It costs ~2s on an idle box but the packaging run executes
			// 200+ files with coverage across parallel workers, where it intermittently exceeded 5s and failed the
			// tester build on a timeout unrelated to the assertion. 20s still catches a genuinely hung test.
			testTimeout: 20_000,
			// Pins the ICU default locale. Without it the suite passes only on an en-US machine — see
			// src/test/PinLocale.ts for the failure this prevents on a non-en-US packaging box.
			// NoNetwork.ts replaces the suite-wide MSW server that used to sit here: it stubs fetch,
			// XMLHttpRequest, WebSocket and node:http/https to fail, so a request no test stubbed still fails loudly
			// instead of hitting the network. The 14 files that stub HTTP routes install MSW themselves with
			// `setupMswServer()` (src/test/UseMswServer.ts), so the other 325 no longer pay for it.
			// Cleanup.ts unmounts every React tree a test mounted; without globals RTL never registers its own
			// afterEach(cleanup), so mounted components used to survive into the next test — see src/test/Cleanup.ts.
			setupFiles: ["src/test/PinLocale.ts", "src/test/Cleanup.ts", "src/test/NoNetwork.ts"],
			// Undoes `vi.spyOn` before each test, so a spy a test forgot to restore (console, Date, a module export)
			// cannot silently stay installed for the rest of the file.
			restoreMocks: true,
			// Undoes `vi.stubEnv`, so an env override leaks no further than the test that set it.
			unstubEnvs: true,
			// Undoes `vi.stubGlobal`, so a replaced global (matchMedia, ResizeObserver, crypto) is not inherited.
			unstubGlobals: true,
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
