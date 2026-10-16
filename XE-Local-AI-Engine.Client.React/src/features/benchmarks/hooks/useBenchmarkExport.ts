import { useMutation } from "@tanstack/react-query";

import { exportBenchmarkProject, exportBenchmarkProjectCsv } from "@/core/api/generated";
import { fileNameFromContentDisposition, saveBlob } from "@/core/api/utils/DownloadBlob";

// The export routes are Operator-gated and answer with a file body. The generated ops read it as JSON / text; the
// `responseType: "blob"` override keeps the bytes as a download (and skips the response validator, which only runs on
// JSON), and the server-chosen file name is read back off Content-Disposition.

export type BenchmarkExportFormat = "json" | "csv";

async function downloadBenchmarkExport(projectId: string, format: BenchmarkExportFormat): Promise<void> {
	// `export` for the full JSON record, `export.csv` for the flat one-row-per-run sheet.
	const options = { path: { projectId }, responseType: "blob" } as const;
	const response = format === "csv" ? await exportBenchmarkProjectCsv(options) : await exportBenchmarkProject(options);
	saveBlob(
		// The blob override changes the body axios returns, not the generated response type.
		response.data as unknown as Blob,
		fileNameFromContentDisposition(response.headers["content-disposition"]) ?? `benchmark-export.${format}`,
	);
}

/** Fetches one project's export and hands it to the browser as a download. */
export function useBenchmarkExport() {
	return useMutation({
		mutationFn: ({ projectId, format }: { projectId: string; format: BenchmarkExportFormat }) =>
			downloadBenchmarkExport(projectId, format),
	});
}
