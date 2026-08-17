import { describe, expect, it } from "vitest";

import { fileNameFromContentDisposition } from "@/core/api/utils/DownloadBlob";

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
