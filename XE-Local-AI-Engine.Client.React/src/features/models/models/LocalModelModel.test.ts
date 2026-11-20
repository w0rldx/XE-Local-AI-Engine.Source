import i18next from "i18next";
import { afterEach, describe, expect, it } from "vitest";

import { emptyModelValue, formatModelModifiedDate, formatModelSize } from "@/features/models/models/LocalModelModel";

afterEach(async () => {
	await i18next.changeLanguage("en");
});

describe("local model model helpers", () => {
	it("formats model sizes", () => {
		expect(formatModelSize(1_073_741_824)).toBe("1.0 GiB");
		expect(formatModelSize(1_048_576)).toBe("1.0 MiB");
		expect(formatModelSize(1024)).toBe("1.0 KiB");
		// A sub-KiB file reads in whole bytes, not as a fraction of a KiB.
		expect(formatModelSize(512)).toBe("512 B");
	});

	it("shows the placeholder for a missing or impossible size", () => {
		expect(formatModelSize(null)).toBe(emptyModelValue);
		expect(formatModelSize(undefined)).toBe(emptyModelValue);
		expect(formatModelSize(-1)).toBe(emptyModelValue);
		expect(formatModelSize(Number.NaN)).toBe(emptyModelValue);
	});

	it("uses the German decimal comma in a German session", async () => {
		await i18next.changeLanguage("de");

		expect(formatModelSize(4.5 * 1_073_741_824)).toBe("4,5 GiB");
	});

	it("formats UTC modified dates deterministically", () => {
		expect(formatModelModifiedDate(Date.UTC(2026, 4, 24))).toBe("2026-05-24");
		expect(formatModelModifiedDate(undefined)).toBe(emptyModelValue);
	});
});
