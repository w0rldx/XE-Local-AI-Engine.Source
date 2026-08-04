// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ImageModelManager } from "@/features/images/components/ImageModelManager";
import type { ImageModelCatalogEntryView, ImageModelDownloadView, ImageModelView } from "@/features/images/models/ImageModels";

const startMutate = vi.fn();
const cancelMutate = vi.fn();
const deleteMutate = vi.fn();
const refreshInstalledModels = vi.fn();
let downloads: ImageModelDownloadView[] = [];
let catalogEntries: ImageModelCatalogEntryView[] = [];

vi.mock("@/features/images/queries/useImageQueries", () => ({
	useStartImageModelDownload: () => ({ mutate: startMutate, isPending: false }),
	useImageModelDownloads: () => ({ data: downloads }),
	useCancelImageModelDownload: () => ({ mutate: cancelMutate, isPending: false }),
	useDeleteImageModel: () => ({ mutate: deleteMutate, isPending: false }),
	useImageModelCatalog: () => ({ data: catalogEntries, isPending: false, error: null }),
	useBrowseImageRepositories: () => ({ data: [], isFetching: false, error: null }),
	useInspectImageRepository: () => ({ data: undefined, isPending: false, error: null }),
	useRefreshInstalledImageModels: () => refreshInstalledModels,
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
		defaultSteps: 20,
		defaultCfgScale: 7,
		defaultSampler: "euler_a",
		...overrides,
	};
}

describe("ImageModelManager", () => {
	beforeEach(() => {
		downloads = [];
		catalogEntries = [];
		confirmResult = true;
		startMutate.mockReset();
		cancelMutate.mockReset();
		deleteMutate.mockReset();
		refreshInstalledModels.mockReset();
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

	// The manual form now lives behind the "Advanced" tab (the catalog is the default), and Tabs is mounted with
	// keepMounted={false}, so the inputs do not exist until that tab is opened.
	function openManualTab() {
		if (screen.queryByTestId("image-model-download-repo") === null) {
			fireEvent.click(screen.getByTestId("image-model-tab-manual"));
		}
	}

	function fillAndSubmit(modelName = "bogus-model") {
		openManualTab();
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

		// The installed-model and catalog queries only poll WHILE something is in flight, and untracking a finished
		// download switches that polling off. Since the 2s status poll routinely observes Completed before the 5s model
		// poll has fired even once, leaving the refresh to those polls loses the race often enough that a model can
		// finish installing and stay invisible until an unrelated refetch happens along.
		it("refreshes the installed models when a download completes", async () => {
			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);

			downloads = [download({ phase: "Running", completedBytes: 50, totalBytes: 100 })];
			fillAndSubmit();
			resolveLastStart();
			await waitFor(() => expect(screen.getByTestId("image-model-download-progress")).toBeTruthy());

			// The coordinator now reports the terminal success the poll picks up.
			downloads = [download({ phase: "Completed", completedBytes: 100, totalBytes: 100 })];
			fillAndSubmit();
			resolveLastStart();

			await waitFor(() => expect(refreshInstalledModels).toHaveBeenCalled());
			expect(toastError).not.toHaveBeenCalled();
		});

		it("does not refresh the installed models when a download fails", async () => {
			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);

			downloads = [download({ phase: "Running" })];
			fillAndSubmit();
			resolveLastStart();
			await waitFor(() => expect(screen.getByTestId("image-model-download-progress")).toBeTruthy());

			downloads = [download({ phase: "Failed", sanitizedError: "nope" })];
			fillAndSubmit();
			resolveLastStart();

			await waitFor(() => expect(toastError).toHaveBeenCalledWith("nope"));
			expect(refreshInstalledModels).not.toHaveBeenCalled();
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

	// A model like Qwen-Image is not one file: it is a diffusion transformer, a VAE and a 7B LLM text encoder, and the
	// encoder is published in a DIFFERENT repository from the other two. The simple single-file form cannot express any
	// of that, which is why installing such a model was impossible before this form existed.
	describe("advanced multi-part file set", () => {
		function enableAdvanced() {
			openManualTab();
			fireEvent.click(screen.getByTestId("image-model-download-advanced-toggle"));
		}

		it("swaps the single weight-file input for a per-role file list", () => {
			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);
			openManualTab();
			expect(screen.getByTestId("image-model-download-file")).toBeTruthy();

			enableAdvanced();

			expect(screen.queryByTestId("image-model-download-file")).toBeNull();
			expect(screen.getByTestId("image-model-download-part-0")).toBeTruthy();
			expect(screen.getByTestId("image-model-download-part-1")).toBeTruthy();
		});

		it("sends every declared part with its own repository and size", () => {
			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);
			enableAdvanced();

			setValue(screen.getByTestId("image-model-download-repo") as HTMLInputElement, "QuantStack/Qwen-Image-GGUF");
			setValue(screen.getByTestId("image-model-download-name") as HTMLInputElement, "qwen-image");
			setValue(screen.getByTestId("image-model-download-part-file-0") as HTMLInputElement, "Qwen_Image-Q4_K_M.gguf");
			setValue(screen.getByTestId("image-model-download-part-size-0") as HTMLInputElement, "13065746976");
			setValue(screen.getByTestId("image-model-download-part-file-1") as HTMLInputElement, "VAE/Qwen_Image-VAE.safetensors");

			fireEvent.click(screen.getByTestId("image-model-download-add-part"));
			setValue(screen.getByTestId("image-model-download-part-file-2") as HTMLInputElement, "Qwen2.5-VL-7B-Instruct.Q4_K_M.gguf");
			setValue(screen.getByTestId("image-model-download-part-repo-2") as HTMLInputElement, "mradermacher/Qwen2.5-VL-7B-Instruct-GGUF");

			fireEvent.click(screen.getByTestId("image-model-download-submit"));

			const body = startMutate.mock.calls.at(-1)?.[0] as {
				parts: { role: string; fileName: string; repoId?: string; sizeBytes?: number }[];
			};
			expect(body.parts).toHaveLength(3);
			const [diffusion, vae, encoder] = body.parts;
			expect(diffusion).toMatchObject({ role: "Diffusion", fileName: "Qwen_Image-Q4_K_M.gguf", sizeBytes: 13_065_746_976 });
			// An un-overridden part must send no repo at all, so the backend applies the set's. A blank size likewise
			// stays undefined rather than becoming 0 — a zero would read as "declared" and silently disable the
			// free-space pre-flight it was meant to feed.
			expect(vae?.repoId).toBeUndefined();
			expect(vae?.sizeBytes).toBeUndefined();
			expect(encoder).toMatchObject({ fileName: "Qwen2.5-VL-7B-Instruct.Q4_K_M.gguf", repoId: "mradermacher/Qwen2.5-VL-7B-Instruct-GGUF" });
		});

		it("refuses to submit a file set with no diffusion file", () => {
			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);
			enableAdvanced();

			setValue(screen.getByTestId("image-model-download-repo") as HTMLInputElement, "QuantStack/Qwen-Image-GGUF");
			setValue(screen.getByTestId("image-model-download-name") as HTMLInputElement, "qwen-image");
			// Only the VAE row is filled — the diffusion row stays empty, so no diffusion part is declared.
			setValue(screen.getByTestId("image-model-download-part-file-1") as HTMLInputElement, "VAE/Qwen_Image-VAE.safetensors");

			expect((screen.getByTestId("image-model-download-submit") as HTMLButtonElement).disabled).toBe(true);
			expect(screen.getByTestId("image-model-download-parts-warning")).toBeTruthy();
		});
	});

	// The whole point of Stage 4: a user should not have to type a repo id, a weight file name, a model name and a
	// family to install a model. A catalog row already carries all four plus the sizes.
	describe("curated catalog", () => {
		it("installs the whole file-set from one click, with nothing typed", () => {
			catalogEntries = [catalogEntry()];
			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);

			fireEvent.click(screen.getByTestId("image-model-catalog-install-qwen-image"));

			expect(startMutate).toHaveBeenCalledTimes(1);
			const body = startMutate.mock.calls[0]?.[0] as {
				modelName: string;
				repoId: string;
				family: string;
				parts: { role: string; fileName: string; repoId?: string; sizeBytes?: number }[];
			};
			expect(body.modelName).toBe("qwen-image");
			expect(body.family).toBe("QwenImage");
			expect(body.parts).toHaveLength(2);
			// The cross-repo override survives: the Qwen2.5-VL text encoder lives in a DIFFERENT repository from the
			// diffusion weights, and dropping that field makes the model impossible to install rather than merely awkward.
			expect(body.parts[1]).toMatchObject({
				role: "Llm",
				repoId: "mradermacher/Qwen2.5-VL-7B-Instruct-GGUF",
				sizeBytes: 4_683_072_512,
			});
			// An in-repo part sends no repoId at all, so the backend applies the set's.
			expect(body.parts[0]?.repoId).toBeUndefined();
		});

		it("keeps a row busy while its own download is still transferring", () => {
			// The 202 lands in milliseconds; the transfer takes minutes. A row that went back to "Install" in between
			// invites a second click on a download already running.
			//
			// Nothing is running at mount here on purpose: a download the coordinator ALREADY reports as running is
			// adopted on load (see the reload-recovery test below), which disables the button before it can be clicked.
			catalogEntries = [catalogEntry({ id: "sdxl-1.0" })];
			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);

			fireEvent.click(screen.getByTestId("image-model-catalog-install-sdxl-1.0"));
			downloads = [download({ modelName: "sdxl-1.0", phase: "Running", completedBytes: 10, totalBytes: 100 })];
			resolveLastStart();

			expect((screen.getByTestId("image-model-catalog-install-sdxl-1.0") as HTMLButtonElement).disabled).toBe(true);
		});

		// A model download is detached and outlives the page, but the tracking set is component state that starts empty
		// on every reload. Gating the status poll on it alone made a reload mid-transfer permanently blind: nothing was
		// tracked, so nothing was fetched, so nothing could become tracked — the progress row and its Cancel button
		// simply disappeared for the rest of the transfer, and the row offered Install again.
		it("adopts a download the coordinator already reports as running", async () => {
			catalogEntries = [catalogEntry({ id: "sdxl-1.0" })];
			downloads = [download({ modelName: "sdxl-1.0", phase: "Running", completedBytes: 10, totalBytes: 100 })];

			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);

			await waitFor(() => expect(screen.getByTestId("image-model-download-progress")).toBeTruthy());
			expect((screen.getByTestId("image-model-catalog-install-sdxl-1.0") as HTMLButtonElement).disabled).toBe(true);
		});

		it("shows an installed entry as installed instead of offering a second download", () => {
			catalogEntries = [catalogEntry({ id: "sd-1.5", isInstalled: true })];
			renderWithProviders(<ImageModelManager models={[model()]} isLoading={false} />);

			expect(screen.getByTestId("image-model-catalog-installed-sd-1.5")).toBeTruthy();
			expect(screen.queryByTestId("image-model-catalog-install-sd-1.5")).toBeNull();
		});

		it("renders the fit verdict the backend computed, including Unknown", () => {
			// Unknown is a real answer on a box whose VRAM could not be probed. It must render as its own state, never
			// silently as a comfortable fit.
			catalogEntries = [catalogEntry({ fitVerdict: "Unknown" })];
			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);

			expect(screen.getByTestId("image-model-catalog-fit-qwen-image").textContent).toContain("Fit unknown");
		});

		it("warns when the download does not fit the free disk", () => {
			catalogEntries = [catalogEntry({ fitsOnDisk: false })];
			renderWithProviders(<ImageModelManager models={[]} isLoading={false} />);

			expect(screen.getByTestId("image-model-catalog-disk-qwen-image")).toBeTruthy();
		});
	});
});

function catalogEntry(overrides: Partial<ImageModelCatalogEntryView> = {}): ImageModelCatalogEntryView {
	return {
		id: "qwen-image",
		displayName: "Qwen-Image",
		publisher: "QuantStack",
		repoId: "QuantStack/Qwen-Image-GGUF",
		family: "QwenImage",
		license: "apache-2.0",
		recommended: false,
		notes: null,
		parts: [
			{ role: "Diffusion", fileName: "Qwen_Image-Q4_K_M.gguf", repoId: null, sizeBytes: 13_065_746_976 },
			{
				role: "Llm",
				fileName: "Qwen2.5-VL-7B-Instruct.Q4_K_M.gguf",
				repoId: "mradermacher/Qwen2.5-VL-7B-Instruct-GGUF",
				sizeBytes: 4_683_072_512,
			},
		],
		totalSizeBytes: 17_748_819_488,
		isInstalled: false,
		fitVerdict: "Fits",
		residentBytes: 13_065_746_976,
		fitBudgetBytes: 34_359_738_368,
		fitsOnDisk: true,
		...overrides,
	};
}

// React tracks the input's value on the DOM node, so a native setter + input event is needed for a controlled input.
function setValue(input: HTMLInputElement, value: string) {
	const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value")?.set;
	setter?.call(input, value);
	input.dispatchEvent(new Event("input", { bubbles: true }));
}
