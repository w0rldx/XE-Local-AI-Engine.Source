// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";

import { fileNameFromContentDisposition, saveBlob } from "@/core/api/utils/DownloadBlob";

describe("saveBlob", () => {
	afterEach(() => {
		vi.restoreAllMocks();
		vi.useRealTimers();
	});

	// The click only QUEUES the download. Revoking the object URL in the same task can pull it out from under a
	// browser that has not taken the bytes yet — the download then silently does nothing, with no error anywhere.
	it("keeps the object URL alive past the click and revokes it on the next tick", () => {
		vi.useFakeTimers();
		const createObjectURL = vi.spyOn(URL, "createObjectURL").mockReturnValue("blob:fake");
		const revokeObjectURL = vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => undefined);
		const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => undefined);

		saveBlob(new Blob(["bytes"]), "report.csv");

		expect(createObjectURL).toHaveBeenCalledTimes(1);
		expect(click).toHaveBeenCalledTimes(1);
		expect(revokeObjectURL).not.toHaveBeenCalled();

		vi.runAllTimers();

		expect(revokeObjectURL).toHaveBeenCalledWith("blob:fake");
	});
});

// The parsed value lands on `<a download>`, so it is a trust boundary: whatever the header says, the browser must only
// ever be handed one path-free segment.

describe("fileNameFromContentDisposition", () => {
	it("reads the quoted and unquoted filename parameter", () => {
		expect(fileNameFromContentDisposition('attachment; filename="benchmark-demo-20260817-1200.json"')).toBe(
			"benchmark-demo-20260817-1200.json",
		);
		expect(fileNameFromContentDisposition("attachment; filename=report.csv")).toBe("report.csv");
	});

	it("strips any path a header tries to smuggle into the download name", () => {
		expect(fileNameFromContentDisposition('attachment; filename="../../etc/passwd"')).toBe("passwd");
		expect(fileNameFromContentDisposition('attachment; filename="C:\\Windows\\system.ini"')).toBe("system.ini");
		expect(fileNameFromContentDisposition('attachment; filename=".."')).toBeNull();
	});

	it("has no name for a missing, empty or non-string header", () => {
		expect(fileNameFromContentDisposition("attachment")).toBeNull();
		expect(fileNameFromContentDisposition(undefined)).toBeNull();
		expect(fileNameFromContentDisposition(["attachment"])).toBeNull();
	});
});
