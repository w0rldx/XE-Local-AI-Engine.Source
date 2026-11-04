import { describe, expect, it, vi } from "vitest";

vi.mock("@/features/modelLab/locales/model-lab-de.json", () => {
	throw new Error("Failed to fetch dynamically imported module");
});

import { forgetFailedModelLabStrings, loadModelLabStrings } from "@/features/modelLab/models/ModelLabStrings";

describe("a failed strings import", () => {
	it("settles once for the visit, and a new visit tries again", async () => {
		const first = loadModelLabStrings("de");
		await first;

		expect(loadModelLabStrings("de")).toBe(first);

		forgetFailedModelLabStrings();

		expect(loadModelLabStrings("de")).not.toBe(first);
	});
});
