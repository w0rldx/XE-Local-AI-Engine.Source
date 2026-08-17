// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The export routes answer with a file body, which the generated SDK models as void, so the component reaches for the
// authed axios instance directly. Mock that (and the save-to-disk anchor, which jsdom cannot perform) — no network.
const { getMock, saveBlobMock, toastErrorMock } = vi.hoisted(() => ({
	getMock: vi.fn(),
	saveBlobMock: vi.fn(),
	toastErrorMock: vi.fn(),
}));

vi.mock("@/core/api/axios/AxiosInstance", () => ({
	axiosInstance: { get: getMock },
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
		getMock.mockResolvedValue({
			data: new Blob(["{}"], { type: "application/json" }),
			headers: { "content-disposition": 'attachment; filename="benchmark-demo-20260817-1200.json"' },
		});
	});
	afterEach(cleanup);

	it("downloads the JSON export under the file name the server chose", async () => {
		renderWithProviders(<BenchmarkExportButtons projectId="project-1" />);

		fireEvent.click(screen.getByTestId("benchmark-export-json"));

		await waitFor(() => expect(saveBlobMock).toHaveBeenCalledTimes(1));
		expect(getMock).toHaveBeenCalledWith("/api/local/v1/benchmarks/projects/project-1/export", { responseType: "blob" });
		expect(saveBlobMock.mock.calls[0]?.[1]).toBe("benchmark-demo-20260817-1200.json");
	});

	it("asks the .csv route for the spreadsheet form", async () => {
		renderWithProviders(<BenchmarkExportButtons projectId="project-1" />);

		fireEvent.click(screen.getByTestId("benchmark-export-csv"));

		await waitFor(() => expect(saveBlobMock).toHaveBeenCalledTimes(1));
		expect(getMock).toHaveBeenCalledWith("/api/local/v1/benchmarks/projects/project-1/export.csv", { responseType: "blob" });
	});

	// A failed export must say so: the click otherwise looks like it worked and simply produced no file.
	it("reports a failed export instead of silently saving nothing", async () => {
		getMock.mockRejectedValue(new Error("boom"));
		renderWithProviders(<BenchmarkExportButtons projectId="project-1" />);

		fireEvent.click(screen.getByTestId("benchmark-export-json"));

		await waitFor(() => expect(toastErrorMock).toHaveBeenCalledTimes(1));
		expect(saveBlobMock).not.toHaveBeenCalled();
	});
});
