import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { BREADCRUMB_BUFFER_CAPACITY, clear, getAll, push } from "@/core/diagnostics/BreadcrumbBuffer";

beforeEach(() => clear());
afterEach(() => clear());

describe("breadcrumb ring buffer", () => {
	it("stamps id + timestamp on push", () => {
		const crumb = push({ category: "navigation", to: "/home" });
		expect(crumb.id).toBeTruthy();
		expect(crumb.timestamp).toBeGreaterThan(0);
		expect(getAll()).toHaveLength(1);
	});

	it("evicts the oldest crumbs once over capacity", () => {
		const overflow = 5;
		for (let index = 0; index < BREADCRUMB_BUFFER_CAPACITY + overflow; index += 1) {
			push({ category: "navigation", to: `/route-${index}` });
		}

		const all = getAll();
		expect(all).toHaveLength(BREADCRUMB_BUFFER_CAPACITY);

		// The first `overflow` crumbs were dropped; the oldest survivor is `/route-5`.
		const first = all[0];
		expect(first?.category).toBe("navigation");
		expect(first && first.category === "navigation" ? first.to : undefined).toBe(`/route-${overflow}`);
	});

	it("redacts sensitive data defensively on push (network URL token)", () => {
		push({
			category: "network",
			entry: { transport: "axios", method: "GET", url: "/api/local/v1/x?token=leak-me" },
		});

		const all = getAll();
		expect(JSON.stringify(all)).not.toContain("leak-me");
	});

	it("clear() empties the ring", () => {
		push({ category: "navigation", to: "/a" });
		clear();
		expect(getAll()).toHaveLength(0);
	});
});
