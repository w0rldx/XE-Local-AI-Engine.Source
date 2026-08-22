import { describe, expect, it } from "vitest";

import { buildClientParams } from "@/core/api/generated/client";

describe("generated client parameter security", () => {
	it("keeps encoded __proto__ option keys as own properties on null-prototype parameter maps", () => {
		const payload = { adoptedByPrototype: true };
		const encodedPrototypeKeys = ["$body___proto__", "$headers___proto__", "$path___proto__", "$query___proto__"];
		const options = Object.fromEntries(encodedPrototypeKeys.map((key) => [key, payload]));

		const params = buildClientParams([options], [{ allowExtra: { body: true, headers: true, path: true, query: true } }]);

		for (const slot of ["body", "headers", "path", "query"] as const) {
			const parameterMap = params[slot] as Record<string, unknown>;
			expect(Object.getPrototypeOf(parameterMap), slot).toBeNull();
			expect(Object.hasOwn(parameterMap, "__proto__"), slot).toBe(true);
			expect(parameterMap["__proto__"], slot).toBe(payload);
		}
	});
});
