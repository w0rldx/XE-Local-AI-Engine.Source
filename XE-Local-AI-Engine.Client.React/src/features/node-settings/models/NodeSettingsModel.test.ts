import { describe, expect, it } from "vitest";

import { toValidNodeSettingsTimeoutSeconds } from "@/features/node-settings/models/NodeSettingsModel";

describe("node settings model", () => {
	it("accepts integer timeout values inside the server range", () => {
		expect(toValidNodeSettingsTimeoutSeconds(300)).toBe(300);
		expect(toValidNodeSettingsTimeoutSeconds("600")).toBe(600);
	});

	it("rejects empty, fractional, and out-of-range timeout values", () => {
		expect(toValidNodeSettingsTimeoutSeconds("")).toBeUndefined();
		expect(toValidNodeSettingsTimeoutSeconds(4)).toBeUndefined();
		expect(toValidNodeSettingsTimeoutSeconds(3601)).toBeUndefined();
		expect(toValidNodeSettingsTimeoutSeconds(10.5)).toBeUndefined();
	});
});
