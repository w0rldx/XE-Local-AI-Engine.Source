// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { GgufDownloadDialog } from "@/features/models/components/GgufDownloadDialog";
import type { GgufRepository } from "@/features/models/models/GgufModels";
import { jsonRoute } from "@/test/msw/Handlers";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// The dialog's own query (GET model-fit/gguf/inspect) runs against MSW through the real generated SDK and axios
// interceptors, so the wire shape → hasProjector path is exercised rather than stubbed. The dialog does NOT issue the
// start request — it hands the choice up through onConfirm, and useGgufAcquisitionFlow turns that into the mutation —
// so the projector value is asserted on that callback here, and on the actual request body in ModelManagement.test.tsx.
const server = setupMswServer();

const repository = (repoId: string): GgufRepository => ({
	repoId,
	isGated: false,
	downloads: 10,
	likes: 1,
	lastModifiedAtUtc: null,
	license: "apache-2.0",
	hasUsableGguf: true,
	isTrustedPublisher: true,
});

const quantFile = {
	fileName: "model-Q4_K_M.gguf",
	quant: "Q4_K_M",
	isDynamic: false,
	isDraft: false,
	sizeBytes: 4_000_000_000,
	qualityTier: "Balanced",
	fitVerdict: "Fits",
	isRecommended: true,
};

/** Stubs the inspect endpoint for one repo id. MSW matches the latest registered handler first. */
function inspectReturns(body: Record<string, unknown>): void {
	server.use(jsonRoute("get", "model-fit/gguf/inspect", body));
}

function renderDialog(repoId: string, onConfirm = vi.fn()) {
	const result = renderWithProviders(
		<GgufDownloadDialog
			repository={repository(repoId)}
			onClose={vi.fn()}
			onConfirm={onConfirm}
			onConfirmDefault={vi.fn()}
			isDownloading={false}
		/>,
	);
	return { ...result, onConfirm };
}

describe("GgufDownloadDialog vision-projector choice", () => {
	afterEach(cleanup);

	it("offers no projector choice for a repo that ships none, and sends nothing for it", async () => {
		inspectReturns({ repoId: "owner/text-only", hasProjector: false, projectorSizeBytes: null, files: [quantFile] });
		const { onConfirm } = renderDialog("owner/text-only");

		// Wait for the quant row, so the absence assertion below is about a loaded dialog rather than a pending one.
		expect(await screen.findByTestId("gguf-download-row-Q4_K_M")).toBeTruthy();
		expect(screen.queryByTestId("gguf-download-include-projector")).toBeNull();

		fireEvent.click(screen.getByTestId("gguf-download-confirm"));

		// undefined, not false: the field is omitted entirely so the server default applies.
		await waitFor(() => expect(onConfirm).toHaveBeenCalledTimes(1));
		expect(onConfirm.mock.calls[0]?.[2]).toBeUndefined();
	});

	it("offers the projector checkbox checked by default, labelled with its size, and includes it on confirm", async () => {
		inspectReturns({ repoId: "owner/vision", hasProjector: true, projectorSizeBytes: 1_073_741_824, files: [quantFile] });
		const { onConfirm } = renderDialog("owner/vision");

		// The label is the shipped `en` bundle string with the size interpolated by the real i18next instance.
		const checkbox = await screen.findByRole("checkbox", { name: /Include vision projector \(1\.0 GB\)/ });
		expect((checkbox as HTMLInputElement).checked).toBe(true);
		expect(screen.getByText(/only a text-only model can be used as a benchmark judge/)).toBeTruthy();

		fireEvent.click(screen.getByTestId("gguf-download-confirm"));

		await waitFor(() => expect(onConfirm).toHaveBeenCalledTimes(1));
		expect(onConfirm.mock.calls[0]?.[2]).toBe(true);
	});

	it("sends includeProjector false once the operator clears the checkbox", async () => {
		inspectReturns({ repoId: "owner/vision", hasProjector: true, projectorSizeBytes: 1_073_741_824, files: [quantFile] });
		const { onConfirm } = renderDialog("owner/vision");

		fireEvent.click(await screen.findByTestId("gguf-download-include-projector"));
		await waitFor(() => expect((screen.getByTestId("gguf-download-include-projector") as HTMLInputElement).checked).toBe(false));

		fireEvent.click(screen.getByTestId("gguf-download-confirm"));

		await waitFor(() => expect(onConfirm).toHaveBeenCalledTimes(1));
		expect(onConfirm.mock.calls[0]?.[2]).toBe(false);
	});

	it("resets the cleared checkbox when a different repository is inspected", async () => {
		inspectReturns({ repoId: "owner/vision", hasProjector: true, projectorSizeBytes: 1_073_741_824, files: [quantFile] });
		// The repo swap is driven from inside the provider tree, the way the page swaps its `downloadRepo` state — an RTL
		// `rerender` would re-render the dialog without the QueryClientProvider that renderWithProviders wrapped it in.
		function SwitchableDialog() {
			const [repoId, setRepoId] = useState("owner/vision");
			return (
				<>
					<button type="button" data-testid="switch-repo" onClick={() => setRepoId("owner/vision-2")}>
						switch
					</button>
					<GgufDownloadDialog
						repository={repository(repoId)}
						onClose={vi.fn()}
						onConfirm={vi.fn()}
						onConfirmDefault={vi.fn()}
						isDownloading={false}
					/>
				</>
			);
		}
		renderWithProviders(<SwitchableDialog />);

		fireEvent.click(await screen.findByTestId("gguf-download-include-projector"));
		await waitFor(() => expect((screen.getByTestId("gguf-download-include-projector") as HTMLInputElement).checked).toBe(false));

		// A second vision repo: the pick was tagged with the first repo id, so it no longer applies and the default returns.
		inspectReturns({ repoId: "owner/vision-2", hasProjector: true, projectorSizeBytes: 2_147_483_648, files: [quantFile] });
		fireEvent.click(screen.getByTestId("switch-repo"));

		await waitFor(() => expect((screen.getByTestId("gguf-download-include-projector") as HTMLInputElement).checked).toBe(true));
		// …and the new repo's own projector size is what the label now shows.
		expect(await screen.findByRole("checkbox", { name: /Include vision projector \(2\.0 GB\)/ })).toBeTruthy();
	});
});
