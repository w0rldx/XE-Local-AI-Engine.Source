import { describe, expect, it } from "vitest";

import { fieldError, issueKey } from "@/core/ui/forms/ZodFieldErrors";

describe("issueKey", () => {
	it("joins a nested path with dots so a per-row input can look up its own error", () => {
		expect(issueKey(["env", 2, "key"])).toBe("env.2.key");
	});

	it("returns the field name unchanged for a top-level issue", () => {
		expect(issueKey(["name"])).toBe("name");
	});

	it("returns an empty key for a form-level issue that names no field", () => {
		expect(issueKey([])).toBe("");
	});
});

describe("fieldError", () => {
	it("returns the message recorded for the field", () => {
		expect(fieldError({ "command.executable": "Required" }, "command.executable")).toBe("Required");
	});

	it("returns undefined for a field with no error, so it can flow straight into Mantine's error prop", () => {
		expect(fieldError({ name: "Required" }, "url")).toBeUndefined();
	});
});
