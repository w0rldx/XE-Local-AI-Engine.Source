import { describe, expect, it } from "vitest";

import { buildKindOptions, kindBadgeColor, kindLabel } from "@/features/models/models/ModelKindFormatters";

const t = (_key: string, fallback: string): string => fallback;

describe("ModelKindFormatters", () => {
	it("labels and colours the Decision kind the backend emits for native decision models", () => {
		expect(kindLabel(t, "Decision")).toBe("Decision");
		expect(kindBadgeColor("Decision")).toBe("cyan");
	});

	it("offers Decision as an operator override, once, whatever the current kind", () => {
		const expected = ["Chat", "Embedding", "Reranker", "Decision", "Unknown"];
		expect(buildKindOptions(t, "Chat").map((option) => option.value)).toEqual(expected);
		expect(buildKindOptions(t, "Decision").map((option) => option.value)).toEqual(expected);
		// A kind outside the overridable set (Draft) is appended so the Select still shows the current value.
		expect(buildKindOptions(t, "Draft").map((option) => option.value)).toEqual([...expected, "Draft"]);
	});
});
