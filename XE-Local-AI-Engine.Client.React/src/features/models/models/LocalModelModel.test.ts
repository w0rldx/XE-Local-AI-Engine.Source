import { describe, expect, it } from "vitest";

import { emptyModelValue, formatModelModifiedDate, formatModelSize } from "@/features/models/models/LocalModelModel";

describe("local model model helpers", () => {
	it("formats model sizes", () => {
		expect(formatModelSize(1_073_741_824)).toBe("1.0 GB");
		expect(formatModelSize(1_048_576)).toBe("1.0 MB");
		expect(formatModelSize(1024)).toBe("1.0 KB");
		expect(formatModelSize(null)).toBe(emptyModelValue);
	});

	it("formats UTC modified dates deterministically", () => {
		expect(formatModelModifiedDate(Date.UTC(2026, 4, 24))).toBe("2026-05-24");
		expect(formatModelModifiedDate(undefined)).toBe(emptyModelValue);
	});
});
