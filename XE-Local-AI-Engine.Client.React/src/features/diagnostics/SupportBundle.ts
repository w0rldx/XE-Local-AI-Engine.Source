// The one-zip support export: the node's scrubbed server bundle with the browser snapshot added as `snapshot.json`.
//
// The bundle route answers a file body, which the generated hey-api op models as void, so the bytes come through the
// shared axios instance as useBenchmarkExport does; its interceptor attaches the bearer token. They are read as an
// ArrayBuffer rather than a Blob because fflate unzips bytes. Whatever goes wrong with the server half (offline node,
// 401, a body that is not a zip), the operator still gets the browser-only zip.

import { unzipSync, zipSync } from "fflate";
import { t } from "i18next";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { saveBlob } from "@/core/api/utils/DownloadBlob";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import type { Snapshot } from "@/core/diagnostics/Diagnostics";
import { toast } from "@/core/ui/notifications/Toast";
import { exportSnapshot, snapshotArchiveEntries } from "@/features/diagnostics/ExportSnapshot";

export const SUPPORT_BUNDLE_PATH = "diagnostics/support-bundle";
// The shared axios instance has no timeout. A node that accepts the request and never answers would otherwise keep the
// Export button loading forever and the browser-only zip would never be saved. The bundle is ~1.5 MB of local reads.
export const SUPPORT_BUNDLE_TIMEOUT_MS = 20_000;

/** Fetches the server bundle and returns it merged with the snapshot; throws when the server half is unusable. */
export async function buildSupportBundleZip(snapshot: Snapshot): Promise<Uint8Array> {
	// An abort on the page's own timer rather than axios' `timeout`, so the deadline follows the (fakeable) global clock.
	const deadline = new AbortController();
	const timer = setTimeout(() => deadline.abort(), SUPPORT_BUNDLE_TIMEOUT_MS);
	let response: { data: ArrayBuffer };
	try {
		response = await axiosInstance.get<ArrayBuffer>(buildLocalApiUrl(SUPPORT_BUNDLE_PATH), {
			responseType: "arraybuffer",
			signal: deadline.signal,
		});
	} finally {
		clearTimeout(timer);
	}
	const serverEntries = unzipSync(new Uint8Array(response.data));
	if (Object.keys(serverEntries).length === 0) {
		throw new Error("The server support bundle is empty.");
	}
	return zipSync({ ...serverEntries, ...snapshotArchiveEntries(snapshot) });
}

/** Downloads the merged support zip, or the browser-only snapshot zip plus a toast when the server half fails. */
export async function exportSupportBundle(snapshot: Snapshot): Promise<void> {
	let merged: Uint8Array;
	try {
		merged = await buildSupportBundleZip(snapshot);
	} catch {
		exportSnapshot(snapshot);
		toast.warning(t("diagnostics.serverBundleUnavailable"));
		return;
	}
	saveBlob(new Blob([new Uint8Array(merged)], { type: "application/zip" }), `xe-support-${snapshot.id}.zip`);
}
