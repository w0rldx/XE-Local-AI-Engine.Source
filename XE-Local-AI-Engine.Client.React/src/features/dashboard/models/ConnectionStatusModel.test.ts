import { describe, expect, it } from "vitest";

import { connectionActionHint, connectionStatusColor, connectionStatusLabel, formatOptionalDate } from "@/features/dashboard/models/ConnectionStatusModel";

describe("connection status model", () => {
	it("maps connected and error states to high-signal labels and colors", () => {
		expect(connectionStatusLabel("connected")).toBe("Connected");
		expect(connectionStatusColor("connected")).toBe("green");
		expect(connectionStatusLabel("error")).toBe("Error");
		expect(connectionStatusColor("error")).toBe("red");
	});

	it("surfaces disable guidance while reconnecting", () => {
		expect(connectionActionHint("reconnecting", true)).toContain("Disable auto-connect");
	});

	it("formats absent dates without throwing", () => {
		expect(formatOptionalDate()).toBe("Not available");
		expect(formatOptionalDate("not-a-date")).toBe("not-a-date");
	});
});
