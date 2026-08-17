import { useMutation } from "@tanstack/react-query";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { fileNameFromContentDisposition, saveBlob } from "@/core/api/utils/DownloadBlob";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";

// The export routes are Operator-gated and answer with a file body, which the generated hey-api op models as void (the
// OpenAPI response is a download, not a JSON payload the SPA consumes). So the bytes are fetched as a blob through the
// shared axios instance — whose request interceptor attaches the bearer token — exactly as useImageObjectUrl does for
// the operator-gated image route, and the server-chosen file name is read back off Content-Disposition.

export type BenchmarkExportFormat = "json" | "csv";

/** `export` for the full JSON record, `export.csv` for the flat one-row-per-run sheet. */
const exportPath = (projectId: string, format: BenchmarkExportFormat): string =>
	buildLocalApiUrl(`benchmarks/projects/${projectId}/export${format === "csv" ? ".csv" : ""}`);

async function downloadBenchmarkExport(projectId: string, format: BenchmarkExportFormat): Promise<void> {
	const response = await axiosInstance.get<Blob>(exportPath(projectId, format), { responseType: "blob" });
	saveBlob(
		response.data,
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
