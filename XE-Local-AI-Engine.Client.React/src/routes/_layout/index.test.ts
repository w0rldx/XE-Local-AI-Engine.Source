// @vitest-environment jsdom

import { readdir, readFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it, vi } from "vitest";

// The page is Home.test.tsx's subject; the route is only checked for what it mounts.
vi.mock("@/features/home/pages/Home", () => ({ Home: () => null }));

import { Route as IndexRoute } from "@/routes/_layout/index";

const srcDir = join(dirname(fileURLToPath(import.meta.url)), "..", "..");

// Mount points are read from source rather than rendered: `autoCodeSplitting` turns a route's `component` into a lazy
// wrapper whose `?tsr-split` import never resolves under this suite's network stubs (see RouteGuards.test.ts).
async function productSources(): Promise<Map<string, string>> {
	const entries = await readdir(srcDir, { recursive: true, withFileTypes: true });
	const files = entries
		.filter((entry) => entry.isFile() && entry.name.endsWith(".tsx") && !entry.name.includes(".test."))
		.map((entry) => join(entry.parentPath, entry.name));
	return new Map(await Promise.all(files.map(async (path) => [path, await readFile(path, "utf8")] as const)));
}

describe("the root route", () => {
	it("renders Home at / instead of redirecting", async () => {
		expect(IndexRoute.options.beforeLoad).toBeUndefined();
		const source = await readFile(join(srcDir, "routes", "_layout", "index.tsx"), "utf8");
		expect(source).toMatch(/component: Home\b/);
	});
});

describe("AI acknowledgement placement", () => {
	// AppLayout.test.tsx proves the shell renders it on every route; this proves nothing else mounts a second one.
	it("is mounted in the authenticated shell only, not on a page", async () => {
		const mountedIn = [...(await productSources())]
			.filter(([, source]) => source.includes("<AiAcknowledgementGate"))
			.map(([path]) => path);

		expect(mountedIn).toEqual([join(srcDir, "AppLayout.tsx")]);
	});
});
