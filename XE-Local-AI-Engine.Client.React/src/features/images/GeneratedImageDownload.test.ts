import { describe, expect, it } from "vitest";

import { buildGeneratedImageFileName } from "@/features/images/GeneratedImageDownload";

// The model name reaches this function as operator-supplied free text and lands in an anchor's `download` attribute.
// A separator or traversal segment surviving into the file name would let the model name steer where the browser
// writes the file, so the sanitizer is the security boundary here, not a cosmetic touch.
describe("buildGeneratedImageFileName", () => {
	it("builds a name from the model and seed", () => {
		expect(buildGeneratedImageFileName("sd-1.5", 182_736)).toBe("xe-image-sd-1.5-seed-182736.png");
	});

	it("lowercases the model segment", () => {
		expect(buildGeneratedImageFileName("SDXL-Base", 1)).toBe("xe-image-sdxl-base-seed-1.png");
	});

	it("collapses path separators so the name cannot escape the download directory", () => {
		const name = buildGeneratedImageFileName("../../etc/passwd", 7);

		expect(name).toBe("xe-image-etc-passwd-seed-7.png");
		expect(name).not.toContain("/");
		expect(name).not.toContain("..");
	});

	it("collapses backslashes and colons so a Windows path cannot survive", () => {
		const name = buildGeneratedImageFileName("C:\\models\\flux", 9);

		expect(name).not.toContain("\\");
		expect(name).not.toContain(":");
		expect(name).toBe("xe-image-c-models-flux-seed-9.png");
	});

	it("drops the model segment entirely when nothing safe remains", () => {
		expect(buildGeneratedImageFileName("///", 42)).toBe("xe-image-seed-42.png");
	});

	it("truncates a very long model name", () => {
		const name = buildGeneratedImageFileName("a".repeat(200), 1);

		expect(name).toBe(`xe-image-${"a".repeat(40)}-seed-1.png`);
	});

	// -1 is the "runtime picked the seed and did not report it" sentinel. Naming the file `seed--1` would advertise a
	// seed that reproduces nothing.
	it("names a random-seed image 'random' rather than seed--1", () => {
		expect(buildGeneratedImageFileName("sd-1.5", -1)).toBe("xe-image-sd-1.5-random.png");
	});

	it("still records seed 0, which is a real seed", () => {
		expect(buildGeneratedImageFileName("sd-1.5", 0)).toBe("xe-image-sd-1.5-seed-0.png");
	});
});
