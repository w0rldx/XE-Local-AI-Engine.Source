// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Mock the two generated export ops (and the save-to-disk anchor, which jsdom cannot perform) — no network.
const { exportJsonMock, exportCsvMock, saveBlobMock, toastErrorMock } = vi.hoisted(() => ({
	exportJsonMock: vi.fn(),
	exportCsvMock: vi.fn(),
	saveBlobMock: vi.fn(),
	toastErrorMock: vi.fn(),
}));

vi.mock("@/core/api/generated", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated")>()),
	exportBenchmarkProject: exportJsonMock,
	exportBenchmarkProjectCsv: exportCsvMock,
}));

vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { error: toastErrorMock, success: vi.fn(), info: vi.fn(), warn: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));

import { BenchmarkExportButtons } from "@/features/benchmarks/components/BenchmarkExportButtons";
import { renderWithProviders } from "@/test/RenderWithProviders";

vi.mock("@/core/api/utils/DownloadBlob", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/utils/DownloadBlob")>()),
	saveBlob: saveBlobMock,
}));

describe("BenchmarkExportButtons", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		const exported = {
			data: new Blob(["{}"], { type: "application/json" }),
			headers: { "content-disposition": 'attachment; filename="benchmark-demo-20260817-1200.json"' },
		};
		exportJsonMock.mockResolvedValue(exported);
		exportCsvMock.mockResolvedValue(exported);
	});
	afterEach(cleanup);

	it("downloads the JSON export under the file name the server chose", async () => {
		renderWithProviders(<BenchmarkExportButtons projectId="project-1" />);

		fireEvent.click(screen.getByTestId("benchmark-export-json"));

		await waitFor(() => expect(saveBlobMock).toHaveBeenCalledTimes(1));
		expect(exportJsonMock).toHaveBeenCalledWith({ path: { projectId: "project-1" }, responseType: "blob" });
		expect(exportCsvMock).not.toHaveBeenCalled();
		expect(saveBlobMock.mock.calls[0]?.[0]).toBeInstanceOf(Blob);
		expect(saveBlobMock.mock.calls[0]?.[1]).toBe("benchmark-demo-20260817-1200.json");
	});

	it("asks the .csv route for the spreadsheet form", async () => {
		renderWithProviders(<BenchmarkExportButtons projectId="project-1" />);

		fireEvent.click(screen.getByTestId("benchmark-export-csv"));

		await waitFor(() => expect(saveBlobMock).toHaveBeenCalledTimes(1));
		expect(exportCsvMock).toHaveBeenCalledWith({ path: { projectId: "project-1" }, responseType: "blob" });
		expect(exportJsonMock).not.toHaveBeenCalled();
	});

	// A failed export must say so: the click otherwise looks like it worked and simply produced no file.
	it("reports a failed export instead of silently saving nothing", async () => {
		exportJsonMock.mockRejectedValue(new Error("boom"));
		renderWithProviders(<BenchmarkExportButtons projectId="project-1" />);

		fireEvent.click(screen.getByTestId("benchmark-export-json"));

		await waitFor(() => expect(toastErrorMock).toHaveBeenCalledTimes(1));
		expect(saveBlobMock).not.toHaveBeenCalled();
	});
});

// A downloaded file is usually read by something other than a person, so the shape it will have is named before the
// download rather than found inside it.
describe("BenchmarkExportButtons schema", () => {
	it("names the export schema on both buttons", () => {
		renderWithProviders(<BenchmarkExportButtons projectId="project-1" />);

		expect(screen.getByTestId("benchmark-export-json").textContent).toContain("v5");
		expect(screen.getByTestId("benchmark-export-csv").textContent).toContain("v5");
	});
});
