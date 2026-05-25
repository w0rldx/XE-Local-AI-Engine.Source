/// <reference types="vitest/config" />

import { devtools } from "@tanstack/devtools-vite";
import { tanstackRouter } from "@tanstack/router-plugin/vite";
import viteReact from "@vitejs/plugin-react";
import UnoCSS from "unocss/vite";
import { defineConfig } from "vite";

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

export default defineConfig({
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
});
