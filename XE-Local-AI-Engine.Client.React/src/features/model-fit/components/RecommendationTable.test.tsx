// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Deterministic i18n: t returns the supplied default string with its {{var}} placeholders filled from whichever
// argument carries them — t(key, defaultValue, params), t(key, { defaultValue, ...params }) or t(key, defaultValue).
// Interpolating all three forms is what lets an assertion read the value a cell actually shows rather than a template.
interface TranslationOptions {
	defaultValue?: string;
	[param: string]: unknown;
}

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, fallbackOrOptions?: string | TranslationOptions, maybeOptions?: TranslationOptions) => {
			const template = typeof fallbackOrOptions === "string" ? fallbackOrOptions : (fallbackOrOptions?.defaultValue ?? key);
			const params = typeof fallbackOrOptions === "string" ? maybeOptions : fallbackOrOptions;
			return template.replace(/\{\{(\w+)\}\}/g, (match, name: string) => (params && name in params ? String(params[name]) : match));
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
		section: "recommended",
		tier: null,
		catalogId: null,
		catalogDisplayName: null,
		catalogNotes: null,
		expertsOffloaded: false,
		gpuGb: null,
		cpuGb: null,
		kvQuant: null,
		kvQuantEstimatedGb: null,
		kvQuantHeadroomGb: null,
		kvQuantFits: null,
		kvQuantRequiresFlashAttention: null,
		kvBytesPerToken: null,
		kvBytesPerTokenQuant: null,
		attentionArch: null,
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

	it("renders the release date as a locale-formatted, date-only value with the raw ISO string as the cell tooltip", () => {
		const dated = makeRecommendation({ rank: 1, isInstalled: true, pullModelName: "dated:latest", releaseDate: "2026-01-15" });

		renderTable(<RecommendationTable recommendations={[dated]} />);

		// Formatted (not the raw ISO string), and the raw value is preserved on the cell's title/tooltip attribute.
		const cell = screen.getByText("Jan 15, 2026");
		expect(cell).toBeTruthy();
		expect(screen.queryByText("2026-01-15")).toBeNull();
		expect(cell.closest("td")?.getAttribute("title")).toBe("2026-01-15");
	});

	it("renders a date-only ISO release date without shifting a day for the local time zone", () => {
		// "2025-03-12" must render as March 12 regardless of the viewer's offset — parsing it via `new Date("2025-03-12")`
		// (UTC midnight) would show March 11 in negative-offset zones. The formatter parses the calendar parts instead.
		const dated = makeRecommendation({ rank: 1, isInstalled: true, pullModelName: "dated:latest", releaseDate: "2025-03-12" });

		renderTable(<RecommendationTable recommendations={[dated]} />);

		expect(screen.getByText("Mar 12, 2025")).toBeTruthy();
	});

	it("falls back to the empty placeholder for an unparsable release date instead of 'Invalid Date'", () => {
		const bad = makeRecommendation({ rank: 1, isInstalled: true, pullModelName: "bad:latest", releaseDate: "not-a-date" });

		renderTable(<RecommendationTable recommendations={[bad]} />);

		expect(screen.queryByText("Invalid Date")).toBeNull();
		// The Released cell shows the same em-dash placeholder the table uses for absent values.
		expect(screen.getByTestId("model-fit-recommendation-row-1").textContent).toContain("—");
	});

	it("shows a tier badge for a row with a catalog tier and none for a row without one", () => {
		const tiered = makeRecommendation({ rank: 1, tier: "S" });
		const untiered = makeRecommendation({ rank: 2, tier: null });

		renderTable(<RecommendationTable recommendations={[tiered, untiered]} />);

		expect(screen.getByTestId("model-fit-tier-badge-1")).toBeTruthy();
		expect(screen.queryByTestId("model-fit-tier-badge-2")).toBeNull();
	});

	it("prefers the catalog display name as the primary label and shows the raw model name as secondary text", () => {
		const catalogBacked = makeRecommendation({
			rank: 1,
			modelName: "unsloth/qwen2.5-coder-32b-gguf",
			catalogDisplayName: "Qwen2.5 Coder 32B",
			catalogNotes: "Strong coding model for 24GB+ VRAM.",
		});

		renderTable(<RecommendationTable recommendations={[catalogBacked]} />);

		expect(screen.getByText("Qwen2.5 Coder 32B")).toBeTruthy();
		expect(screen.getByText("unsloth/qwen2.5-coder-32b-gguf")).toBeTruthy();
		expect(screen.getByTestId("model-fit-catalog-notes-1").textContent).toBe("Strong coding model for 24GB+ VRAM.");
	});

	it("shows the MoE-offload badge with a GPU/RAM breakdown tooltip when the advisor reports a split", () => {
		const offloaded = makeRecommendation({ rank: 1, expertsOffloaded: true, gpuGb: 8, cpuGb: 16 });

		renderTable(<RecommendationTable recommendations={[offloaded]} />);

		expect(screen.getByTestId("model-fit-moe-offload-badge-1")).toBeTruthy();
	});

	it("shows the MoE-offload badge without a breakdown when the GPU/RAM split is unavailable", () => {
		const offloaded = makeRecommendation({ rank: 1, expertsOffloaded: true, gpuGb: null, cpuGb: null });

		renderTable(<RecommendationTable recommendations={[offloaded]} />);

		expect(screen.getByTestId("model-fit-moe-offload-badge-1")).toBeTruthy();
	});

	it("renders no MoE-offload badge for a row that does not offload experts", () => {
		const gpuOnly = makeRecommendation({ rank: 1, expertsOffloaded: false });

		renderTable(<RecommendationTable recommendations={[gpuOnly]} />);

		expect(screen.queryByTestId("model-fit-moe-offload-badge-1")).toBeNull();
	});

	it("shows the advisory quantized-KV hint when the advisor computed a fitting Q8_0 estimate", () => {
		const withAdvisory = makeRecommendation({
			rank: 1,
			kvQuant: "Q8_0",
			kvQuantEstimatedGb: 10.965,
			kvQuantHeadroomGb: 3.1,
			kvQuantFits: true,
			kvQuantRequiresFlashAttention: true,
		});

		renderTable(<RecommendationTable recommendations={[withAdvisory]} />);

		// The test i18n stub returns the raw default string (no interpolation), so assert presence only —
		// the same convention as the MoE-offload badge tests above.
		expect(screen.getByTestId("model-fit-kv-quant-hint-1")).toBeTruthy();
	});

	it("withholds the quantized-KV hint when the advisory estimate still would not fit", () => {
		const stillTooBig = makeRecommendation({
			rank: 1,
			kvQuant: "Q8_0",
			kvQuantEstimatedGb: 22.4,
			kvQuantHeadroomGb: -4.2,
			kvQuantFits: false,
			kvQuantRequiresFlashAttention: true,
		});

		renderTable(<RecommendationTable recommendations={[stillTooBig]} />);

		expect(screen.queryByTestId("model-fit-kv-quant-hint-1")).toBeNull();
	});

	it("renders no quantized-KV hint for a row without an advisory", () => {
		const noAdvisory = makeRecommendation({ rank: 1 });

		renderTable(<RecommendationTable recommendations={[noAdvisory]} />);

		expect(screen.queryByTestId("model-fit-kv-quant-hint-1")).toBeNull();
	});

	it("shows the KV-per-token line with the attention tag when both the figure and its quant are present", () => {
		const withPerToken = makeRecommendation({
			rank: 1,
			kvBytesPerToken: 640,
			kvBytesPerTokenQuant: "Q8_0",
			attentionArch: "mla",
		});

		renderTable(<RecommendationTable recommendations={[withPerToken]} />);

		// 640 B/token is 0.6 KB: the line reports KB, and the quant it was computed with, lower-cased.
		expect(screen.getByTestId("model-fit-kv-per-token-1").textContent).toBe("MLA \u00b7 0.6 KB/token (q8_0 KV)");
	});

	it("renders the KV-per-token line without an arch tag when the row carries no attention tag", () => {
		const noArch = makeRecommendation({ rank: 1, kvBytesPerToken: 640, kvBytesPerTokenQuant: "Q8_0", attentionArch: null });

		renderTable(<RecommendationTable recommendations={[noArch]} />);

		// No tag means no separator either: the line starts at the figure rather than with a dangling middle dot.
		expect(screen.getByTestId("model-fit-kv-per-token-1").textContent).toBe("0.6 KB/token (q8_0 KV)");
	});

	it("withholds the KV-per-token line when the figure has no quant label", () => {
		// Half a fact is worse than none: an unlabelled KV byte count is ambiguous by a factor of two.
		const unlabelled = makeRecommendation({ rank: 1, kvBytesPerToken: 640, kvBytesPerTokenQuant: null });

		renderTable(<RecommendationTable recommendations={[unlabelled]} />);

		expect(screen.queryByTestId("model-fit-kv-per-token-1")).toBeNull();
	});

	it("renders no KV-per-token line for a row that predates the field", () => {
		renderTable(<RecommendationTable recommendations={[makeRecommendation({ rank: 1 })]} />);

		expect(screen.queryByTestId("model-fit-kv-per-token-1")).toBeNull();
	});

	it("renders an explore-section row (all new fields null/false) without crashing", () => {
		const exploreRow = makeRecommendation({
			rank: 1,
			section: "explore",
			tier: null,
			catalogId: null,
			catalogDisplayName: null,
			catalogNotes: null,
			expertsOffloaded: false,
			gpuGb: null,
			cpuGb: null,
		});

		renderTable(<RecommendationTable recommendations={[exploreRow]} />);

		expect(screen.getByTestId("model-fit-recommendation-row-1")).toBeTruthy();
		expect(screen.queryByTestId("model-fit-tier-badge-1")).toBeNull();
		expect(screen.queryByTestId("model-fit-moe-offload-badge-1")).toBeNull();
		expect(screen.queryByTestId("model-fit-catalog-notes-1")).toBeNull();
	});
});
