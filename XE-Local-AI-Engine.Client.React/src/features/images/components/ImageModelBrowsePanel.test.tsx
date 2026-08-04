// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ImageModelBrowsePanel } from "@/features/images/components/ImageModelBrowsePanel";
import type { ImageRepositoryFileView, ImageRepositoryView } from "@/features/images/models/ImageModels";

let repositories: ImageRepositoryView[] = [];
let files: ImageRepositoryFileView[] = [];

vi.mock("@/features/images/queries/useImageQueries", () => ({
	useBrowseImageRepositories: () => ({ data: repositories, isFetching: false, error: null }),
	useInspectImageRepository: () => ({
		data: { repoId: "second-state/FLUX.1-schnell-GGUF", isGated: false, license: "apache-2.0", files },
		isPending: false,
		error: null,
	}),
}));

// No i18next instance runs under vitest, so the real useTranslation returns the default string with {{placeholders}}
// intact. This interpolating stub lets assertions read the actual values.
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

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

function repository(overrides: Partial<ImageRepositoryView> = {}): ImageRepositoryView {
	return {
		repoId: "second-state/FLUX.1-schnell-GGUF",
		isGated: false,
		downloads: 1000,
		likes: 20,
		lastModifiedAtUtc: 1_750_000_000_000,
		license: "apache-2.0",
		hasUsableWeights: true,
		isTrustedPublisher: false,
		...overrides,
	};
}

const fluxFiles: ImageRepositoryFileView[] = [
	{ fileName: "flux1-schnell-Q4_0.gguf", format: "Gguf", sizeBytes: 6_688_845_536, suggestedRole: "Diffusion" },
	{ fileName: "ae.safetensors", format: "Safetensors", sizeBytes: 335_304_388, suggestedRole: "Vae" },
	{ fileName: "clip_l.safetensors", format: "Safetensors", sizeBytes: 246_144_152, suggestedRole: "ClipL" },
];

describe("ImageModelBrowsePanel", () => {
	beforeEach(() => {
		repositories = [];
		files = [];
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

	function search(term = "flux") {
		const input = screen.getByTestId("image-model-browse-input") as HTMLInputElement;
		const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value")?.set;
		setter?.call(input, term);
		input.dispatchEvent(new Event("input", { bubbles: true }));
		fireEvent.click(screen.getByTestId("image-model-browse-search"));
	}

	it("badges a gated repository and an unverified publisher without hiding either", () => {
		// Both are warnings, not filters. A gated repo that silently vanished would look like a search bug; one that
		// installed with no warning would 401 on a first run, which is worse.
		repositories = [repository({ isGated: true }), repository({ repoId: "unsloth/other", isTrustedPublisher: true })];
		renderWithProviders(<ImageModelBrowsePanel installedModelNames={[]} isInstalling={false} onInstall={vi.fn()} />);
		search();

		expect(screen.getByTestId("image-model-browse-gated-second-state/FLUX.1-schnell-GGUF")).toBeTruthy();
		expect(screen.getByTestId("image-model-browse-untrusted-second-state/FLUX.1-schnell-GGUF")).toBeTruthy();
		expect(screen.queryByTestId("image-model-browse-untrusted-unsloth/other")).toBeNull();
		expect(screen.getByTestId("image-model-browse-row-unsloth/other")).toBeTruthy();
	});

	it("installs the ticked files with their suggested roles and Hub-reported sizes", () => {
		// This is the workflow the hand-typed form replaced: four file names and four role dropdowns become four ticks,
		// and the sizes ride along so the free-disk pre-flight and the aggregate percentage both work.
		repositories = [repository()];
		files = fluxFiles;
		const onInstall = vi.fn();
		renderWithProviders(<ImageModelBrowsePanel installedModelNames={[]} isInstalling={false} onInstall={onInstall} />);
		search();
		fireEvent.click(screen.getByTestId("image-model-browse-open-second-state/FLUX.1-schnell-GGUF"));

		fireEvent.click(screen.getByTestId("image-model-browse-file-check-flux1-schnell-Q4_0.gguf"));
		fireEvent.click(screen.getByTestId("image-model-browse-file-check-ae.safetensors"));
		fireEvent.click(screen.getByTestId("image-model-browse-install"));

		expect(onInstall).toHaveBeenCalledTimes(1);
		const request = onInstall.mock.calls[0]?.[0];
		// The install name defaults to the repo's last segment, so nothing has to be typed for the common case.
		expect(request.modelName).toBe("flux.1-schnell-gguf");
		expect(request.repoId).toBe("second-state/FLUX.1-schnell-GGUF");
		// The family is inferred from the repo/file names rather than defaulting to SD 1.5, because the family drives
		// the generation form's step/CFG defaults and FLUX at SD1.5's settings just produces a bad image.
		expect(request.family).toBe("Flux");
		expect(request.parts).toEqual([
			{ role: "Diffusion", fileName: "flux1-schnell-Q4_0.gguf", sizeBytes: 6_688_845_536 },
			{ role: "Vae", fileName: "ae.safetensors", sizeBytes: 335_304_388 },
		]);
	});

	it("refuses to install a selection with no diffusion file", () => {
		repositories = [repository()];
		files = fluxFiles;
		renderWithProviders(<ImageModelBrowsePanel installedModelNames={[]} isInstalling={false} onInstall={vi.fn()} />);
		search();
		fireEvent.click(screen.getByTestId("image-model-browse-open-second-state/FLUX.1-schnell-GGUF"));

		fireEvent.click(screen.getByTestId("image-model-browse-file-check-ae.safetensors"));

		expect((screen.getByTestId("image-model-browse-install") as HTMLButtonElement).disabled).toBe(true);
		expect(screen.getByTestId("image-model-browse-diffusion-required")).toBeTruthy();
	});

	it("refuses to install two files claiming the same role", () => {
		// The runtime emits one launch flag per role, so a second diffusion file would be downloaded and never
		// referenced — several gigabytes spent on a model that cannot start. Caught before the transfer, not after.
		repositories = [repository()];
		files = [
			...fluxFiles,
			{ fileName: "flux1-schnell-Q8_0.gguf", format: "Gguf", sizeBytes: 12_634_000_000, suggestedRole: "Diffusion" },
		];
		renderWithProviders(<ImageModelBrowsePanel installedModelNames={[]} isInstalling={false} onInstall={vi.fn()} />);
		search();
		fireEvent.click(screen.getByTestId("image-model-browse-open-second-state/FLUX.1-schnell-GGUF"));

		fireEvent.click(screen.getByTestId("image-model-browse-file-check-flux1-schnell-Q4_0.gguf"));
		fireEvent.click(screen.getByTestId("image-model-browse-file-check-flux1-schnell-Q8_0.gguf"));

		expect((screen.getByTestId("image-model-browse-install") as HTMLButtonElement).disabled).toBe(true);
		expect(screen.getByTestId("image-model-browse-duplicate-role")).toBeTruthy();
	});

	it("blocks an install whose name collides with an already-installed model", () => {
		repositories = [repository()];
		files = fluxFiles;
		renderWithProviders(
			<ImageModelBrowsePanel installedModelNames={["flux.1-schnell-gguf"]} isInstalling={false} onInstall={vi.fn()} />,
		);
		search();
		fireEvent.click(screen.getByTestId("image-model-browse-open-second-state/FLUX.1-schnell-GGUF"));
		fireEvent.click(screen.getByTestId("image-model-browse-file-check-flux1-schnell-Q4_0.gguf"));

		expect((screen.getByTestId("image-model-browse-install") as HTMLButtonElement).disabled).toBe(true);
	});
});
