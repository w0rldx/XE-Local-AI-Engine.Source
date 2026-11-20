// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { DownloadProgressPanel } from "@/core/model-downloads/components/DownloadProgressPanel/DownloadProgressPanel";
import type { GgufDownloadStatus } from "@/core/model-downloads/queries/useGgufDownload";
import { renderWithProviders } from "@/test/RenderWithProviders";

afterEach(cleanup);

const MODEL = "unsloth/x:Q4_K_M";

function renderFailed(errorCode: string | null, sanitizedMessage: string | null) {
	const status: GgufDownloadStatus = {
		operationId: "op-1",
		operationKind: "Download",
		modelName: MODEL,
		phase: "Failed",
		pct: undefined,
		completedBytes: null,
		totalBytes: null,
		startedAtUtc: null,
		updatedAtUtc: null,
		errorCode,
		sanitizedMessage,
	};
	renderWithProviders(
		<DownloadProgressPanel
			inFlight={[MODEL]}
			downloadStatuses={new Map([[MODEL, status]])}
			onCancel={() => undefined}
			cancellingModelName={null}
		/>,
	);
	return screen.getByRole("alert");
}

describe("DownloadProgressPanel failure text", () => {
	it("shows the translated text of a known code instead of the server's sentence", () => {
		const alert = renderFailed("InsufficientStorage", "Need 5 GB, 1 GB free.");

		expect(alert.textContent).toContain("There is not enough free storage for this download.");
		expect(alert.textContent).not.toContain("Need 5 GB");
	});

	it("keeps the server's text on its own line after a DownloadFailed", () => {
		const alert = renderFailed("DownloadFailed", "Download failed.");

		expect(alert.textContent).toContain("The model could not be downloaded.\nDownload failed.");
	});

	it("falls back to the generic sentence for an unknown code", () => {
		const alert = renderFailed("SomethingNew", "Raw server text.");

		expect(alert.textContent).toContain("The model could not be downloaded.");
		expect(alert.textContent).not.toContain("Raw server text.");
	});
});
