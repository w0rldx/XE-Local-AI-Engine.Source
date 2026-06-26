// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Deterministic i18n: t returns the supplied default string (or a {{var}}-interpolated defaultValue from the options
// object form) so labels and the hidden-count note are readable in assertions.
vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, fallbackOrOptions?: string | { defaultValue?: string; [param: string]: unknown }) => {
			if (typeof fallbackOrOptions === "string") {
				return fallbackOrOptions;
			}
			if (fallbackOrOptions && typeof fallbackOrOptions === "object") {
				const template = fallbackOrOptions.defaultValue ?? key;
				return template.replace(/\{\{(\w+)\}\}/g, (_match, name: string) => String(fallbackOrOptions[name] ?? ""));
			}
			return key;
		},
	}),
}));

import { RecommendationTable } from "@/features/model-fit/components/RecommendationTable";
import type { ModelFitRecommendation } from "@/features/model-fit/models/ModelFitModels";

function makeRecommendation(overrides: Partial<ModelFitRecommendation>): ModelFitRecommendation {
	return {
		rank: 1,
		modelName: "model-a",
		providerModelName: null,
		score: 8,
		fitLevel: "GPU",
		runMode: "gpu",
		quantization: "Q4_0",
		estimatedTokensPerSecond: 42,
		requiredRamMb: 6144,
		requiredVramMb: 4096,
		contextTokens: 8192,
		isInstalled: false,
		pullModelName: null,
		releaseDate: null,
		isTrustedPublisher: true,
		...overrides,
	};
}

function renderTable(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("RecommendationTable", () => {
	beforeEach(() => {
		// Mantine's MantineProvider reads window.matchMedia / ResizeObserver on mount; jsdom omits both.
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

	it("shows a Download button only for a not-installed row with a model name and calls onDownload with that row", () => {
		const onDownload = vi.fn();
		const available = makeRecommendation({
			rank: 1,
			modelName: "available",
			isInstalled: false,
			pullModelName: "available:latest",
		});
		const installed = makeRecommendation({
			rank: 2,
			modelName: "installed",
			isInstalled: true,
			pullModelName: "installed:latest",
		});
		const noTag = makeRecommendation({ rank: 3, modelName: "no-tag", isInstalled: false, pullModelName: null });

		renderTable(<RecommendationTable recommendations={[available, installed, noTag]} onDownload={onDownload} />);

		// Only the available row (rank 1) exposes a Download button.
		const downloadButton = screen.getByTestId("model-fit-download-button-1");
		expect(downloadButton).toBeTruthy();
		// Installed row and the null-tag row carry no Download button.
		expect(screen.queryByTestId("model-fit-download-button-2")).toBeNull();
		expect(screen.queryByTestId("model-fit-download-button-3")).toBeNull();

		fireEvent.click(downloadButton);
		expect(onDownload).toHaveBeenCalledTimes(1);
		expect(onDownload).toHaveBeenCalledWith(available);
	});

	it("flags an untrusted publisher with a warning badge and shows none for a trusted publisher", () => {
		const untrusted = makeRecommendation({ rank: 1, modelName: "sketchy/model", isTrustedPublisher: false });
		const trusted = makeRecommendation({ rank: 2, modelName: "unsloth/model", isTrustedPublisher: true });

		renderTable(<RecommendationTable recommendations={[untrusted, trusted]} />);

		// The untrusted row (rank 1) carries the warning badge; the trusted row (rank 2) does not.
		expect(screen.getByTestId("model-fit-untrusted-badge-1")).toBeTruthy();
		expect(screen.queryByTestId("model-fit-untrusted-badge-2")).toBeNull();
	});

	it("disables the in-flight row's Download button when downloadingModelName matches its tag", () => {
		const onDownload = vi.fn();
		const available = makeRecommendation({
			rank: 1,
			modelName: "available",
			isInstalled: false,
			pullModelName: "available:latest",
		});

		renderTable(
			<RecommendationTable recommendations={[available]} onDownload={onDownload} downloadingModelName="available:latest" />,
		);

		const downloadButton = screen.getByTestId("model-fit-download-button-1") as HTMLButtonElement;
		expect(downloadButton.disabled).toBe(true);
	});

	it("renders no action column when onDownload is not provided (pure-presentation mode)", () => {
		const available = makeRecommendation({ rank: 1, isInstalled: false, pullModelName: "available:latest" });

		renderTable(<RecommendationTable recommendations={[available]} />);

		expect(screen.queryByTestId("model-fit-download-button-1")).toBeNull();
	});

	it("renders a CPU-mode badge for a row whose run mode is CPU", () => {
		const cpuRow = makeRecommendation({ rank: 1, runMode: "cpu", isInstalled: true, pullModelName: null });
		const gpuRow = makeRecommendation({ rank: 2, runMode: "gpu", isInstalled: true, pullModelName: null });

		renderTable(<RecommendationTable recommendations={[cpuRow, gpuRow]} />);

		expect(screen.getByTestId("model-fit-cpu-mode-badge-1")).toBeTruthy();
		expect(screen.queryByTestId("model-fit-cpu-mode-badge-2")).toBeNull();
	});

	it("renders a GPU fit badge for a GPU row and drops the redundant fit badge for a CPU row", () => {
		// A GPU run shows its "GPU" fit badge; a CPU run shows only the CPU-mode badge (the duplicate "CPU" fit badge is dropped).
		const gpuRow = makeRecommendation({ rank: 1, fitLevel: "GPU", runMode: "gpu", isInstalled: true, pullModelName: null });
		const cpuRow = makeRecommendation({ rank: 2, fitLevel: "CPU", runMode: "cpu", isInstalled: true, pullModelName: null });

		renderTable(<RecommendationTable recommendations={[gpuRow, cpuRow]} />);

		// The GPU row surfaces a single "GPU" badge text.
		expect(screen.getByText("GPU")).toBeTruthy();
		// The CPU row never renders a "CPU" fit badge — only the "CPU mode" badge stands in.
		expect(screen.queryByText("CPU")).toBeNull();
		expect(screen.getByTestId("model-fit-cpu-mode-badge-2")).toBeTruthy();
	});

	it("surfaces both the required RAM and required VRAM fit estimates", () => {
		const row = makeRecommendation({ rank: 1, requiredRamMb: 6144, requiredVramMb: 4096, isInstalled: true, pullModelName: null });

		renderTable(<RecommendationTable recommendations={[row]} />);

		// 6144 MB → 6.0 GB, 4096 MB → 4.0 GB (formatMemoryMb).
		expect(screen.getByText("6.0 GB")).toBeTruthy();
		expect(screen.getByText("4.0 GB")).toBeTruthy();
	});

	it("shows every row including catalog-only ones (no hiding) and marks the non-downloadable ones", () => {
		const onDownload = vi.fn();
		const available = makeRecommendation({
			rank: 1,
			modelName: "available",
			isInstalled: false,
			pullModelName: "available:latest",
		});
		const installed = makeRecommendation({ rank: 2, modelName: "installed", isInstalled: true, pullModelName: null });
		// Neither downloadable nor installed — a catalog-only name. It renders with a badge + no action.
		const catalogOnly = makeRecommendation({ rank: 3, modelName: "catalog-only", isInstalled: false, pullModelName: null });

		renderTable(<RecommendationTable recommendations={[available, installed, catalogOnly]} onDownload={onDownload} />);

		expect(screen.getByTestId("model-fit-recommendation-row-1")).toBeTruthy();
		expect(screen.getByTestId("model-fit-recommendation-row-2")).toBeTruthy();
		expect(screen.getByTestId("model-fit-recommendation-row-3")).toBeTruthy();
		// The catalog-only row is labelled and carries no Download button.
		expect(screen.getByText("Catalog only")).toBeTruthy();
		expect(screen.queryByTestId("model-fit-download-button-3")).toBeNull();
	});

	it("paginates a large result set to the default page size and renders the pagination footer", () => {
		const many = Array.from({ length: 30 }, (_, index) =>
			makeRecommendation({ rank: index + 1, modelName: `model-${index + 1}`, pullModelName: `model-${index + 1}:latest` }),
		);

		renderTable(<RecommendationTable recommendations={many} />);

		// Default page size is 25 → only the first 25 rows render; row 26 lives on page 2.
		expect(screen.getAllByTestId(/^model-fit-recommendation-row-/).length).toBe(25);
		expect(screen.queryByTestId("model-fit-recommendation-row-26")).toBeNull();
		expect(screen.getByTestId("model-fit-recommendations-pagination")).toBeTruthy();
	});

	it("renders the release date for a row that has one", () => {
		const dated = makeRecommendation({ rank: 1, isInstalled: true, pullModelName: "dated:latest", releaseDate: "2026-01-15" });

		renderTable(<RecommendationTable recommendations={[dated]} />);

		expect(screen.getByText("2026-01-15")).toBeTruthy();
	});
});
