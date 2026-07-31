// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ImageModelManager } from "@/features/images/components/ImageModelManager";
import type { ImageModelDownloadView } from "@/features/images/models/ImageModels";

const startMutate = vi.fn();
let downloads: ImageModelDownloadView[] = [];

vi.mock("@/features/images/queries/useImageQueries", () => ({
	useStartImageModelDownload: () => ({ mutate: startMutate, isPending: false }),
	useImageModelDownloads: () => ({ data: downloads }),
}));

const toastError = vi.fn();
vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { success: vi.fn(), error: (message: string) => toastError(message) },
}));

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

function download(overrides: Partial<ImageModelDownloadView> = {}): ImageModelDownloadView {
	return {
		modelName: "bogus-model",
		phase: "Running",
		completedBytes: null,
		totalBytes: null,
		sanitizedError: null,
		...overrides,
	};
}

// F-031: a download that fails must stop the spinner and tell the operator why. Before the fix the UI had no way to
// learn about a failure at all — it polled the installed-models list and waited forever for a model that never arrived.
describe("ImageModelManager failed download reporting", () => {
	beforeEach(() => {
		downloads = [];
		startMutate.mockReset();
		toastError.mockReset();
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				onchange: null,
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
				dispatchEvent: vi.fn(),
			})),
		});
		Object.defineProperty(window, "ResizeObserver", {
			writable: true,
			value: class ResizeObserverMock {
				observe = vi.fn();

				unobserve = vi.fn();

				disconnect = vi.fn();
			},
		});
	});

	afterEach(() => {
		cleanup();
	});

	// Drives the component into the "download pending" state by invoking the mutation's onSuccess, exactly as the
	// real mutation does once the backend returns 202.
	function startPendingDownload() {
		const lastCall = startMutate.mock.calls.at(-1);
		if (lastCall === undefined) {
			throw new Error("The download mutation was never invoked.");
		}
		const handlers = lastCall[1] as { onSuccess: () => void };
		handlers.onSuccess();
	}

	it("surfaces the sanitized reason and clears the pending state when the download fails", async () => {
		renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);

		fillAndSubmit();
		startPendingDownload();

		await waitFor(() => expect(screen.getByTestId("image-model-download-progress")).toBeTruthy());

		// The coordinator now reports the terminal failure the poll picks up.
		downloads = [download({ phase: "Failed", sanitizedError: "The requested file was not found in the repository." })];
		fillAndSubmit();
		startPendingDownload();

		await waitFor(() => {
			expect(screen.getByTestId("image-model-download-error").textContent).toContain(
				"The requested file was not found in the repository.",
			);
		});
		expect(toastError).toHaveBeenCalledWith("The requested file was not found in the repository.");
		expect(screen.queryByTestId("image-model-download-progress")).toBeNull();
	});

	it("keeps showing progress while the download is still running", async () => {
		renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);

		downloads = [download({ phase: "Running", completedBytes: 50, totalBytes: 100 })];
		fillAndSubmit();
		startPendingDownload();

		await waitFor(() => expect(screen.getByTestId("image-model-download-progress")).toBeTruthy());
		expect(screen.queryByTestId("image-model-download-error")).toBeNull();
		expect(toastError).not.toHaveBeenCalled();
	});

	function fillAndSubmit() {
		const repo = screen.getByTestId("image-model-download-repo") as HTMLInputElement;
		const file = screen.getByTestId("image-model-download-file") as HTMLInputElement;
		const name = screen.getByTestId("image-model-download-name") as HTMLInputElement;
		setValue(repo, "Comfy-Org/stable-diffusion-v1-5-archive");
		setValue(file, "this-file-does-not-exist.safetensors");
		setValue(name, "bogus-model");
		(screen.getByTestId("image-model-download-submit") as HTMLButtonElement).click();
	}
});

// React tracks the input's value on the DOM node, so a native setter + input event is needed for a controlled input.
function setValue(input: HTMLInputElement, value: string) {
	const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value")?.set;
	setter?.call(input, value);
	input.dispatchEvent(new Event("input", { bubbles: true }));
}
