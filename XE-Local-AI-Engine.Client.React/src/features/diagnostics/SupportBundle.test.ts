// @vitest-environment jsdom

import { strFromU8, strToU8, unzipSync, zipSync } from "fflate";
import { delay, http } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { SCHEMA_VERSION, type Snapshot } from "@/core/diagnostics/Diagnostics";
import { parseSnapshotZip } from "@/features/diagnostics/ExportSnapshot";
import {
	buildSupportBundleZip,
	exportSupportBundle,
	SUPPORT_BUNDLE_PATH,
	SUPPORT_BUNDLE_TIMEOUT_MS,
} from "@/features/diagnostics/SupportBundle";
import en from "@/locales/en.json";
import { binaryRoute, localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { setupMswServer } from "@/test/UseMswServer";

const { saveBlobMock, exportSnapshotMock, toastMock } = vi.hoisted(() => ({
	saveBlobMock: vi.fn(),
	exportSnapshotMock: vi.fn(),
	toastMock: { warning: vi.fn() },
}));

vi.mock("@/core/api/utils/DownloadBlob", () => ({ saveBlob: saveBlobMock }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));
vi.mock("@/features/diagnostics/ExportSnapshot", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/features/diagnostics/ExportSnapshot")>()),
	exportSnapshot: exportSnapshotMock,
}));

const server = setupMswServer();

const snapshot: Snapshot = {
	id: "snap-1",
	createdAt: 1_700_000_000_000,
	schemaVersion: SCHEMA_VERSION,
	kind: "manual",
	breadcrumbs: [],
	network: [],
	env: { route: "/chat", appVersion: "1.0.0", userAgent: "test", viewport: { width: 800, height: 600 }, locale: "en" },
};

const serverZip = zipSync({
	"manifest.json": strToU8('{"schemaVersion":1}'),
	"node-info.json": strToU8('{"version":"1.0.0"}'),
	"logs/xe-node.log": strToU8("[trace:abc] started"),
});

async function blobBytes(blob: Blob): Promise<Uint8Array> {
	return new Uint8Array(await blob.arrayBuffer());
}

describe("support bundle", () => {
	beforeEach(() => {
		saveBlobMock.mockReset();
		exportSnapshotMock.mockReset();
		toastMock.warning.mockReset();
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	it("keeps every server entry and adds snapshot.json", async () => {
		server.use(binaryRoute(SUPPORT_BUNDLE_PATH, serverZip));

		const entries = unzipSync(await buildSupportBundleZip(snapshot));

		expect(Object.keys(entries).sort()).toEqual(["logs/xe-node.log", "manifest.json", "node-info.json", "snapshot.json"]);
		expect(strFromU8(entries["logs/xe-node.log"] as Uint8Array)).toBe("[trace:abc] started");
	});

	it("produces a zip the snapshot importer still reads", async () => {
		server.use(binaryRoute(SUPPORT_BUNDLE_PATH, serverZip));

		expect(parseSnapshotZip(await buildSupportBundleZip(snapshot))).toEqual(snapshot);
	});

	it("saves the merged zip under the snapshot id", async () => {
		server.use(binaryRoute(SUPPORT_BUNDLE_PATH, serverZip));

		await exportSupportBundle(snapshot);

		expect(saveBlobMock).toHaveBeenCalledTimes(1);
		const [blob, fileName] = saveBlobMock.mock.calls[0] as [Blob, string];
		expect(fileName).toBe("xe-support-snap-1.zip");
		expect(Object.keys(unzipSync(await blobBytes(blob)))).toContain("node-info.json");
		expect(exportSnapshotMock).not.toHaveBeenCalled();
		expect(toastMock.warning).not.toHaveBeenCalled();
	});

	it("falls back to the browser-only zip with a toast when the server answers 500", async () => {
		server.use(problemDetailsRoute("get", SUPPORT_BUNDLE_PATH, 500, { detail: "boom" }));

		await exportSupportBundle(snapshot);

		expect(exportSnapshotMock).toHaveBeenCalledWith(snapshot);
		expect(saveBlobMock).not.toHaveBeenCalled();
		expect(toastMock.warning).toHaveBeenCalledWith(en.diagnostics.serverBundleUnavailable);
	});

	it("falls back when the body is not a zip", async () => {
		server.use(binaryRoute(SUPPORT_BUNDLE_PATH, strToU8("<html>not a zip</html>"), "text/html"));

		await exportSupportBundle(snapshot);

		expect(exportSnapshotMock).toHaveBeenCalledWith(snapshot);
		expect(saveBlobMock).not.toHaveBeenCalled();
		expect(toastMock.warning).toHaveBeenCalledWith(en.diagnostics.serverBundleUnavailable);
	});

	it("falls back once the deadline passes on a node that never answers", async () => {
		vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
		server.use(
			http.get(localApiPath(SUPPORT_BUNDLE_PATH), async () => {
				await delay("infinite");
			}),
		);

		const exported = exportSupportBundle(snapshot);
		await vi.advanceTimersByTimeAsync(SUPPORT_BUNDLE_TIMEOUT_MS - 1);
		expect(exportSnapshotMock).not.toHaveBeenCalled();

		await vi.advanceTimersByTimeAsync(1);
		await exported;

		expect(exportSnapshotMock).toHaveBeenCalledWith(snapshot);
		expect(saveBlobMock).not.toHaveBeenCalled();
		expect(toastMock.warning).toHaveBeenCalledWith(en.diagnostics.serverBundleUnavailable);
	});
});
