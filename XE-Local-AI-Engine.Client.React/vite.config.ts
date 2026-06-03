/// <reference types="vitest/config" />

import { devtools } from "@tanstack/devtools-vite";
import { tanstackRouter } from "@tanstack/router-plugin/vite";
import viteReact from "@vitejs/plugin-react";
import UnoCSS from "unocss/vite";
import { defineConfig, type ProxyOptions } from "vite";

import aspNetCoreDevelopmentCertificate from "./vite-plugins/aspnetcore-development-certificate";
import tablerDevelopmentBugfix from "./vite-plugins/tabler-development-bugfix";
import path from "node:path";

const coverageThresholds = process.env.VITEST_COVERAGE_CHECK === "true"
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
		],
		optimizeDeps: {
			include: ["@tanstack/react-form", "@tanstack/react-form-devtools"],
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
						// before the generic "/api" entry or Vite matches "/api" first and the upgrade falls through as plain HTTP.
						"/api/local/v1/chat/hub": localProxy(proxyTarget, true),
						"/api/local/v1/runtime/hub": localProxy(proxyTarget, true),
						"/api/local/v1/scheduler/hub": localProxy(proxyTarget, true),
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
