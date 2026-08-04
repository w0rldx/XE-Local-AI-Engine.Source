// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ImageModelManager } from "@/features/images/components/ImageModelManager";
import type { ImageModelDownloadView, ImageModelView } from "@/features/images/models/ImageModels";

const startMutate = vi.fn();
const cancelMutate = vi.fn();
const deleteMutate = vi.fn();
let downloads: ImageModelDownloadView[] = [];

vi.mock("@/features/images/queries/useImageQueries", () => ({
	useStartImageModelDownload: () => ({ mutate: startMutate, isPending: false }),
	useImageModelDownloads: () => ({ data: downloads }),
	useCancelImageModelDownload: () => ({ mutate: cancelMutate, isPending: false }),
	useDeleteImageModel: () => ({ mutate: deleteMutate, isPending: false }),
}));

let confirmResult = true;
const confirmSpy = vi.fn();
vi.mock("@/core/ui/hooks/useConfirm", () => ({
	useConfirm: () => ({
		confirm: (options: unknown) => {
			confirmSpy(options);
			return Promise.resolve(confirmResult);
		},
	}),
}));

// No i18next instance is initialised under vitest, so the real useTranslation returns the default string with the
// {{placeholders}} intact. This interpolating stub lets the progress assertions check the actual values (the part
// index, the percentage) rather than the template.
vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, defaultValue?: string, options?: Record<string, unknown>) => {
			let text = defaultValue ?? key;
			if (options) {
				for (const [name, value] of Object.entries(options)) {
					text = text.replace(`{{${name}}}`, String(value));
				}
			}
			return text;
		},
	}),
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
		partIndex: null,
		partCount: null,
		...overrides,
	};
}

function model(overrides: Partial<ImageModelView> = {}): ImageModelView {
	return {
		modelName: "sd-1.5",
		repoId: "second-state/stable-diffusion-v1-5-GGUF",
		family: "Sd15",
		kind: "Txt2Img",
		sizeBytes: 1_900_000_000,
		downloadedAtUtc: 0,
		...overrides,
	};
}

describe("ImageModelManager", () => {
	beforeEach(() => {
		downloads = [];
		confirmResult = true;
		startMutate.mockReset();
		cancelMutate.mockReset();
		deleteMutate.mockReset();
		confirmSpy.mockReset();
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

	// Drives the component into the "download tracked" state by invoking the mutation's onSuccess, exactly as the real
	// mutation does once the backend returns 202.
	//
	// The `act` wrapper is load-bearing, not decoration: onSuccess resets the draft, and React's input value tracker
	// suppresses `onChange` when a field is re-filled with the value the DOM node already holds. Without flushing the
	// reset first, re-typing the same repo/file into the form silently no-ops and the next submit stays disabled.
	function resolveLastStart() {
		const lastCall = startMutate.mock.calls.at(-1);
		if (lastCall === undefined) {
			throw new Error("The download mutation was never invoked.");
		}
		const handlers = lastCall[1] as { onSuccess: () => void };
		act(() => {
			handlers.onSuccess();
		});
	}

	function fillAndSubmit(modelName = "bogus-model") {
		setValue(screen.getByTestId("image-model-download-repo") as HTMLInputElement, "Comfy-Org/stable-diffusion-v1-5-archive");
		setValue(screen.getByTestId("image-model-download-file") as HTMLInputElement, "this-file-does-not-exist.safetensors");
		setValue(screen.getByTestId("image-model-download-name") as HTMLInputElement, modelName);
		fireEvent.click(screen.getByTestId("image-model-download-submit"));
	}

	// F-031: a download that fails must stop the spinner and tell the operator why. Before the fix the UI had no way to
	// learn about a failure at all — it polled the installed-models list and waited forever for a model that never arrived.
	describe("failed download reporting", () => {
		it("surfaces the sanitized reason and clears the pending state when the download fails", async () => {
			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);

			fillAndSubmit();
			resolveLastStart();

			await waitFor(() => expect(screen.getByTestId("image-model-download-progress")).toBeTruthy());

			// The coordinator now reports the terminal failure the poll picks up.
			downloads = [download({ phase: "Failed", sanitizedError: "The requested file was not found in the repository." })];
			fillAndSubmit();
			resolveLastStart();

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
			resolveLastStart();

			await waitFor(() => expect(screen.getByTestId("image-model-download-progress")).toBeTruthy());
			expect(screen.queryByTestId("image-model-download-error")).toBeNull();
			expect(toastError).not.toHaveBeenCalled();
		});
	});

	// The manager used to hold ONE pending model name, so starting any download disabled the Download button for every
	// other model. The catalog will offer several at once, which makes that a hard blocker rather than a rough edge.
	describe("concurrent downloads", () => {
		it("tracks every started download and shows a row for each", async () => {
			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);

			fillAndSubmit("first-model");
			resolveLastStart();
			fillAndSubmit("second-model");
			resolveLastStart();

			downloads = [
				download({ modelName: "first-model", phase: "Running", completedBytes: 10, totalBytes: 100 }),
				download({ modelName: "second-model", phase: "Running", completedBytes: 30, totalBytes: 100 }),
			];
			fillAndSubmit("third-model");

			await waitFor(() => {
				expect(screen.getByTestId("image-model-download-row-first-model")).toBeTruthy();
				expect(screen.getByTestId("image-model-download-row-second-model")).toBeTruthy();
			});
		});

		it("leaves the Download button usable for a different model while one download runs", async () => {
			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);

			fillAndSubmit("first-model");
			resolveLastStart();
			downloads = [download({ modelName: "first-model", phase: "Running", completedBytes: 10, totalBytes: 100 })];

			fillAndSubmit("second-model");

			await waitFor(() => {
				const submit = screen.getByTestId("image-model-download-submit") as HTMLButtonElement;
				expect(submit.hasAttribute("disabled")).toBe(false);
			});
			// Two distinct starts were dispatched — the second was never blocked by the first.
			expect(startMutate.mock.calls.map((call) => (call[0] as { modelName: string }).modelName)).toEqual([
				"first-model",
				"second-model",
			]);
		});
	});

	describe("progress detail", () => {
		it("shows an aggregate percentage and the part being fetched", async () => {
			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);

			fillAndSubmit("qwen-image");
			resolveLastStart();
			downloads = [
				download({ modelName: "qwen-image", phase: "Running", completedBytes: 500, totalBytes: 1000, partIndex: 2, partCount: 3 }),
			];
			fillAndSubmit("qwen-image");

			await waitFor(() => {
				const detail = screen.getByTestId("image-model-download-detail-qwen-image").textContent ?? "";
				expect(detail).toContain("50%");
				expect(detail).toContain("Part 2 of 3");
			});
		});

		it("reports advancing bytes without inventing a percentage when the set total is unknown", async () => {
			// A set total is only computable when EVERY part declared a size. A bar derived from a partial total would
			// pass 100% and read as broken, so the row must fall back to the bytes actually transferred.
			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);

			fillAndSubmit("unsized-model");
			resolveLastStart();
			downloads = [download({ modelName: "unsized-model", phase: "Running", completedBytes: 2_097_152, totalBytes: null })];
			fillAndSubmit("unsized-model");

			await waitFor(() => {
				const detail = screen.getByTestId("image-model-download-detail-unsized-model").textContent ?? "";
				expect(detail).toContain("2 MB transferred");
				expect(detail).not.toContain("%");
			});
		});
	});

	describe("cancel", () => {
		it("cancels the clicked row's download by model name", async () => {
			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);

			fillAndSubmit("first-model");
			resolveLastStart();
			fillAndSubmit("second-model");
			resolveLastStart();
			downloads = [
				download({ modelName: "first-model", phase: "Running" }),
				download({ modelName: "second-model", phase: "Running" }),
			];
			fillAndSubmit("second-model");

			await waitFor(() => expect(screen.getByTestId("image-model-download-cancel-second-model")).toBeTruthy());
			fireEvent.click(screen.getByTestId("image-model-download-cancel-second-model"));

			expect(cancelMutate).toHaveBeenCalledTimes(1);
			expect(cancelMutate.mock.calls[0]?.[0]).toBe("second-model");
		});
	});

	describe("delete", () => {
		it("asks for confirmation before deleting an installed model", async () => {
			renderWithProviders(<ImageModelManager models={[model()]} isLoading={false} />);

			fireEvent.click(screen.getByTestId("image-model-delete-sd-1.5"));

			await waitFor(() => expect(deleteMutate).toHaveBeenCalledTimes(1));
			expect(confirmSpy).toHaveBeenCalledTimes(1);
			expect(deleteMutate.mock.calls[0]?.[0]).toBe("sd-1.5");
		});

		it("does not delete when the confirmation is declined", async () => {
			confirmResult = false;
			renderWithProviders(<ImageModelManager models={[model()]} isLoading={false} />);

			fireEvent.click(screen.getByTestId("image-model-delete-sd-1.5"));

			await waitFor(() => expect(confirmSpy).toHaveBeenCalledTimes(1));
			expect(deleteMutate).not.toHaveBeenCalled();
		});
	});
});

// React tracks the input's value on the DOM node, so a native setter + input event is needed for a controlled input.
function setValue(input: HTMLInputElement, value: string) {
	const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value")?.set;
	setter?.call(input, value);
	input.dispatchEvent(new Event("input", { bubbles: true }));
}
