// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The panel is one query and one mutation over the generated SDK; mocking the two SDK functions keeps the test on the
// component's own rules (when it asks, and what it says about the answer) with no network and no msw handler set.
const { estimateMock, clearCacheMock, patchMock, toastSuccessMock, toastErrorMock } = vi.hoisted(() => ({
	estimateMock: vi.fn(),
	clearCacheMock: vi.fn(),
	patchMock: vi.fn(),
	toastSuccessMock: vi.fn(),
	toastErrorMock: vi.fn(),
}));

vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { success: toastSuccessMock, error: toastErrorMock, info: vi.fn(), warn: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));

vi.mock("@/core/api/generated", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated")>()),
	getBenchmarkKldDiskEstimate: estimateMock,
	clearBenchmarkFidelityCache: clearCacheMock,
	updateBenchmarkProjectFidelity: patchMock,
}));

import { BenchmarkFidelityPanel } from "@/features/benchmarks/components/BenchmarkFidelityPanel";
import type { BenchmarkProjectFidelity } from "@/features/benchmarks/models/BenchmarkModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

const models = [
	{ modelName: "base.gguf", maxContextTokens: null, effectiveContextTokens: null, origin: null, modelContentFingerprint: "v1:b", supportsTools: false },
];

// Only the members the mapper reads; the panel re-renders from the query cache, not from this.
const detailWire = { id: "project-1", name: "P", coreTask: "t", judge: { enabled: false }, version: 8 };

const fidelity: BenchmarkProjectFidelity = {
	enabled: true,
	kldEnabled: false,
	chunks: null,
	chunksEffective: 200,
	kldBaseModelName: null,
	kldBaseFingerprint: null,
	kldExpectedDigest: null,
};

const estimate = {
	estimatedBytes: 25_300_000_000,
	freeDiskBytes: 400_000_000_000,
	cachedBytes: 0,
	chunks: 200,
	contextTokens: 512,
	vocabSize: 151_936,
	formula: "200 chunks × 512 tokens × 151936 vocab × 1.75 B/logit",
	fitsOnDisk: true,
};

describe("BenchmarkFidelityPanel", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		estimateMock.mockResolvedValue({ data: estimate });
		clearCacheMock.mockResolvedValue({ data: undefined });
	});
	afterEach(cleanup);

	// The estimate is a disk probe on the node. Asking for it on every project view would probe the disk for an operator
	// who never opens the section, and the answer moves whenever the disk does — so it is read on open, not on mount.
	it("does not ask the node for an estimate until the section is opened", async () => {
		renderWithProviders(<BenchmarkFidelityPanel projectId="project-1" fidelity={fidelity} projectVersion={7} models={[]} />);

		expect(estimateMock).not.toHaveBeenCalled();

		fireEvent.click(screen.getByTestId("benchmark-fidelity-toggle"));

		await waitFor(() => expect(estimateMock).toHaveBeenCalledOnce());
	});

	// 25 GB is not a number to discover after the write has started, so it is stated with its arithmetic rather than
	// asserted: the operator can check the formula against the size.
	it("states the cache size and the arithmetic behind it", async () => {
		renderWithProviders(<BenchmarkFidelityPanel projectId="project-1" fidelity={fidelity} projectVersion={7} models={[]} />);
		fireEvent.click(screen.getByTestId("benchmark-fidelity-toggle"));

		const block = await screen.findByTestId("benchmark-kld-estimate");
		expect(block.textContent).toContain("23.6 GB");
		expect(screen.getByTestId("benchmark-kld-estimate-formula").textContent).toBe(estimate.formula);
		expect(block.textContent).toContain("151936");
	});

	it("warns when the node says the reservation does not fit", async () => {
		estimateMock.mockResolvedValue({ data: { ...estimate, fitsOnDisk: false } });
		renderWithProviders(<BenchmarkFidelityPanel projectId="project-1" fidelity={fidelity} projectVersion={7} models={[]} />);
		fireEvent.click(screen.getByTestId("benchmark-fidelity-toggle"));

		expect(await screen.findByTestId("benchmark-kld-estimate-too-large")).toBeTruthy();
	});

	it("does not warn when it fits", async () => {
		renderWithProviders(<BenchmarkFidelityPanel projectId="project-1" fidelity={fidelity} projectVersion={7} models={[]} />);
		fireEvent.click(screen.getByTestId("benchmark-fidelity-toggle"));

		await screen.findByTestId("benchmark-kld-estimate");
		expect(screen.queryByTestId("benchmark-kld-estimate-too-large")).toBeNull();
	});

	it("clears the cached base logits for the project it was given", async () => {
		renderWithProviders(<BenchmarkFidelityPanel projectId="project-1" fidelity={fidelity} projectVersion={7} models={[]} />);
		fireEvent.click(screen.getByTestId("benchmark-fidelity-toggle"));

		fireEvent.click(await screen.findByTestId("benchmark-fidelity-clear-cache"));

		await waitFor(() =>
			expect(clearCacheMock).toHaveBeenCalledWith(expect.objectContaining({ path: { projectId: "project-1" } })),
		);
	});
});

// The panel reports what is configured; the project form is what sets it. Both halves have to agree, or the operator
// reads "off" beside a project that is measuring.
describe("BenchmarkFidelityPanel configured state", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		estimateMock.mockResolvedValue({ data: estimate });
		clearCacheMock.mockResolvedValue({ data: undefined });
	});
	afterEach(cleanup);

	it("says the project is not measuring, and where to turn it on", () => {
		renderWithProviders(<BenchmarkFidelityPanel projectId="project-1" fidelity={{ ...fidelity, enabled: false }} projectVersion={7} models={[]} />);
		fireEvent.click(screen.getByTestId("benchmark-fidelity-toggle"));

		expect(screen.getByTestId("benchmark-fidelity-state").textContent).toBe("off");
		expect(screen.getByTestId("benchmark-fidelity-settings").textContent).toContain("project settings");
	});

	it("distinguishes a perplexity-only project from one that also measures KL divergence", () => {
		renderWithProviders(<BenchmarkFidelityPanel projectId="project-1" fidelity={fidelity} projectVersion={7} models={[]} />);

		expect(screen.getByTestId("benchmark-fidelity-state").textContent).toBe("PPL");

		cleanup();
		renderWithProviders(
			<BenchmarkFidelityPanel
				projectId="project-1"
				fidelity={{ ...fidelity, kldEnabled: true, kldBaseModelName: "base.gguf" }}
				projectVersion={7}
				models={[]}
			/>,
		);

		expect(screen.getByTestId("benchmark-fidelity-state").textContent).toBe("PPL + KLD");
	});

	// `chunksEffective` is what runs; `chunks` is what was typed. Showing the typed null as "200" would claim a choice
	// nobody made, and showing nothing would hide what is actually being scored.
	it("reports the chunk count that actually runs, and the base it measures against", () => {
		renderWithProviders(
			<BenchmarkFidelityPanel
				projectId="project-1"
				fidelity={{ ...fidelity, kldEnabled: true, kldBaseModelName: "base.gguf", chunksEffective: 200 }}
				projectVersion={7}
				models={[]}
			/>,
		);
		fireEvent.click(screen.getByTestId("benchmark-fidelity-toggle"));

		const line = screen.getByTestId("benchmark-fidelity-settings").textContent;
		expect(line).toContain("200");
		expect(line).toContain("base.gguf");
	});

	// The estimate is what informs the DECISION to enable KLD, so it must not be gated on KLD already being on.
	it("still reads the estimate for a project that has not enabled KL divergence", async () => {
		renderWithProviders(<BenchmarkFidelityPanel projectId="project-1" fidelity={fidelity} projectVersion={7} models={[]} />);
		fireEvent.click(screen.getByTestId("benchmark-fidelity-toggle"));

		expect(await screen.findByTestId("benchmark-kld-estimate")).toBeTruthy();
	});
});

// The settings have their OWN route with its own CAS. That is what makes them editable on a frozen project, which is
// exactly when an operator wants them: after seeing runs.
describe("BenchmarkFidelityPanel settings", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		estimateMock.mockResolvedValue({ data: estimate });
		clearCacheMock.mockResolvedValue({ data: undefined });
		patchMock.mockResolvedValue({ data: { project: { ...detailWire }, enqueuedRunIds: [], enqueuedCount: 0 } });
	});
	afterEach(cleanup);

	const open = (overrides: Partial<BenchmarkProjectFidelity> = {}) => {
		renderWithProviders(
			<BenchmarkFidelityPanel projectId="project-1" fidelity={{ ...fidelity, ...overrides }} projectVersion={7} models={models} />,
		);
		fireEvent.click(screen.getByTestId("benchmark-fidelity-toggle"));
	};

	it("writes the settings through the fidelity route with the project's version", async () => {
		open();

		fireEvent.click(screen.getByTestId("benchmark-fidelity-save"));

		await waitFor(() =>
			expect(patchMock).toHaveBeenCalledWith(
				expect.objectContaining({
					path: { projectId: "project-1" },
					body: expect.objectContaining({ expectedVersion: 7, fidelityEnabled: true, measureExisting: false }),
				}),
			),
		);
	});

	// Enabling fidelity must not silently spend GPU on a project's whole history.
	it("only measures existing runs when the operator asks for it, and reports the count", async () => {
		patchMock.mockResolvedValue({ data: { project: { ...detailWire }, enqueuedRunIds: ["a", "b"], enqueuedCount: 2 } });
		open();

		fireEvent.click(screen.getByTestId("benchmark-fidelity-measure-existing"));
		fireEvent.click(screen.getByTestId("benchmark-fidelity-save"));

		await waitFor(() =>
			expect(patchMock).toHaveBeenCalledWith(expect.objectContaining({ body: expect.objectContaining({ measureExisting: true }) })),
		);
		await waitFor(() => expect(toastSuccessMock).toHaveBeenCalledWith(expect.stringContaining("2")));
	});

	// Nothing is deleted; a new expected digest is minted and old figures start reading kld-stale. Saying so is the
	// honest answer, and it is the difference between an operator re-measuring and one filing a bug.
	it("warns that changing the base model makes measured figures read stale", async () => {
		open({ kldEnabled: true, kldBaseModelName: "base.gguf" });

		expect(screen.queryByTestId("benchmark-fidelity-remeasure-note")).toBeNull();

		fireEvent.change(screen.getByTestId("benchmark-fidelity-chunks"), { target: { value: "300" } });

		expect(screen.getByTestId("benchmark-fidelity-remeasure-note").textContent).toContain("kld-stale");
	});

	it("refuses to save KL divergence with no base model", () => {
		open();

		fireEvent.click(screen.getByTestId("benchmark-fidelity-kld-enabled"));

		expect((screen.getByTestId("benchmark-fidelity-save") as HTMLButtonElement).disabled).toBe(true);
	});

	it("hides the chunk count and KLD controls until perplexity is enabled", () => {
		open({ enabled: false });

		expect(screen.queryByTestId("benchmark-fidelity-chunks")).toBeNull();
		expect(screen.queryByTestId("benchmark-fidelity-kld-enabled")).toBeNull();
	});
});
