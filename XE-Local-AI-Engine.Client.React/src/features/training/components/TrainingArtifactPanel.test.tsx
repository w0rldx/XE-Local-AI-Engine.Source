// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { TrainingArtifactPanel } from "@/features/training/components/TrainingArtifactPanel";
import type { TrainingArtifactView } from "@/features/training/models/TrainingModels";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, defaultValue?: string) => defaultValue ?? _key }),
}));

const mocks = vi.hoisted(() => ({
	artifacts: [] as TrainingArtifactView[],
	beginRevalidation: vi.fn(),
	decide: vi.fn(),
	discard: vi.fn(),
	override: vi.fn(),
	noop: vi.fn(),
}));

vi.mock("@/features/training/queries/useTrainingArtifacts", () => ({
	useTrainingArtifacts: () => ({ data: mocks.artifacts }),
	useStartTrainingExport: () => ({ isPending: false, mutate: mocks.noop }),
	useRunTrainingArtifactSmoke: () => ({ isPending: false, mutate: mocks.noop }),
	usePromoteTrainingArtifact: () => ({ isPending: false, mutate: mocks.noop }),
	useDeleteTrainingArtifact: () => ({ isPending: false, mutate: mocks.noop }),
	useDecideTrainingArtifactQuality: () => ({ isPending: false, mutate: mocks.decide }),
	useBeginTrainingArtifactQualityRevalidation: () => ({ isPending: false, mutate: mocks.beginRevalidation }),
	useDiscardTrainingArtifactQuality: () => ({ isPending: false, mutate: mocks.discard }),
	useOverrideTrainingArtifactQuality: () => ({ isPending: false, mutate: mocks.override }),
}));

vi.mock("@/features/training/components/ComparisonCreateDialog", () => ({
	ComparisonCreateDialog: ({ opened, artifactId, freshEvaluations, onComparisonCreated }: { opened: boolean; artifactId?: string; freshEvaluations?: boolean; onComparisonCreated?: (id: string) => void }) =>
		opened ? (
			<button data-artifact-id={artifactId} data-fresh-evaluations={String(freshEvaluations)} data-testid="mock-comparison-complete" onClick={() => onComparisonCreated?.("comparison-1")} type="button">
				Complete comparison
			</button>
		) : null,
}));

function artifact(overrides: Partial<TrainingArtifactView> = {}): TrainingArtifactView {
	return {
		id: "artifact-1",
		runId: "run-1",
		kind: "MergedGguf",
		fileName: "merged-Q4_K_M.gguf",
		sha256: "0123456789abcdef",
		sizeBytes: 4096,
		smokeState: "Passed",
		smokeReason: null,
		committedModelName: null,
		qualityComparisonId: null,
		qualityOutcome: "Pending",
		discardedAtUtc: null,
		discardReason: null,
		discardCleanupPending: false,
		version: 3,
		...overrides,
	};
}

function renderPanel(row: TrainingArtifactView) {
	mocks.artifacts = [row];
	return render(
		<MantineProvider>
			<TrainingArtifactPanel exportPhase={null} onExportStarted={vi.fn()} runId="run-1" />
		</MantineProvider>,
	);
}

describe("TrainingArtifactPanel quality gate", () => {
	beforeEach(() => {
		mocks.artifacts = [];
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
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
		vi.clearAllMocks();
	});

	it("offers validation after smoke and binds the completed comparison back to the artifact", () => {
		renderPanel(artifact());

		expect(screen.getByText("Pending")).toBeTruthy();
		expect((screen.getByRole("button", { name: "Register as model" }) as HTMLButtonElement).disabled).toBe(true);
		fireEvent.click(screen.getByRole("button", { name: "Validate quality" }));

		const comparison = screen.getByTestId("mock-comparison-complete");
		expect(comparison.getAttribute("data-artifact-id")).toBe("artifact-1");
		fireEvent.click(comparison);

		expect(mocks.decide).toHaveBeenCalledWith(
			{
				path: { artifactId: "artifact-1" },
				body: { comparisonId: "comparison-1", expectedVersion: 3 },
			},
			expect.any(Object),
		);
	});

	it("keeps promotion explicit and enables it only after quality passes", () => {
		renderPanel(artifact({ qualityComparisonId: "comparison-1", qualityOutcome: "Passed" }));

		expect(screen.getAllByText("Passed")).toHaveLength(2);
		expect((screen.getByRole("button", { name: "Register as model" }) as HTMLButtonElement).disabled).toBe(false);
	});

	it("starts a pending revalidation before opening a fresh-evaluation comparison", async () => {
		renderPanel(artifact({ qualityComparisonId: "comparison-old", qualityOutcome: "Passed" }));

		fireEvent.click(screen.getByRole("button", { name: "Revalidate quality" }));

		expect((screen.getByRole("button", { name: "Register as model" }) as HTMLButtonElement).disabled).toBe(true);
		expect(mocks.beginRevalidation).toHaveBeenCalledWith(
			{ path: { artifactId: "artifact-1" }, body: { expectedVersion: 3 } },
			expect.any(Object),
		);
		const callbacks = mocks.beginRevalidation.mock.calls[0]?.[1] as { onSuccess: (response: { version: number }) => void };
		await act(() => callbacks.onSuccess({ version: 4 }));

		const comparison = await screen.findByTestId("mock-comparison-complete");
		expect(comparison.getAttribute("data-fresh-evaluations")).toBe("true");
		fireEvent.click(comparison);
		expect(mocks.decide).toHaveBeenCalledWith(
			{
				path: { artifactId: "artifact-1" },
				body: { comparisonId: "comparison-1", expectedVersion: 4 },
			},
			expect.any(Object),
		);
	});

	it("requires a non-empty reason only when overriding a complete failed comparison", async () => {
		const view = renderPanel(artifact({ qualityComparisonId: "comparison-1", qualityOutcome: "Failed" }));

		fireEvent.click(screen.getByRole("button", { name: "Override failure" }));
		const submit = (await screen.findByRole("button", { name: "Record override" })) as HTMLButtonElement;
		expect(submit.disabled).toBe(true);
		fireEvent.change(await screen.findByLabelText("Override reason"), { target: { value: "Known benchmark exception" } });
		expect(submit.disabled).toBe(false);
		fireEvent.click(submit);
		expect(mocks.override).toHaveBeenCalledWith(
			{
				path: { artifactId: "artifact-1" },
				body: { expectedVersion: 3, reason: "Known benchmark exception" },
			},
			expect.any(Object),
		);

		view.unmount();
		renderPanel(artifact({ qualityComparisonId: null, qualityOutcome: "Failed" }));
		expect(screen.queryByRole("button", { name: "Override failure" })).toBeNull();
	});

	it("requires an audited reason to discard a completed staged artifact", async () => {
		const view = renderPanel(artifact({ qualityComparisonId: "comparison-1", qualityOutcome: "Passed" }));

		fireEvent.click(screen.getByRole("button", { name: "Discard staged file" }));
		const submit = (await screen.findByRole("button", { name: "Confirm discard" })) as HTMLButtonElement;
		expect(submit.disabled).toBe(true);
		fireEvent.change(await screen.findByLabelText("Discard reason"), { target: { value: "Superseded by a newer export" } });
		expect(submit.disabled).toBe(false);
		fireEvent.click(submit);
		expect(mocks.discard).toHaveBeenCalledWith(
			{
				path: { artifactId: "artifact-1" },
				body: { expectedVersion: 3, reason: "Superseded by a newer export" },
			},
			expect.any(Object),
		);

		view.unmount();
		renderPanel(
			artifact({
				qualityComparisonId: "comparison-1",
				qualityOutcome: "Passed",
				discardedAtUtc: 1_787_137_200_000,
				discardReason: "Superseded by a newer export",
			}),
		);
		expect(screen.getByText("Discarded")).toBeTruthy();
		expect(screen.getByText("Superseded by a newer export")).toBeTruthy();
		expect(screen.queryByRole("button", { name: "Discard staged file" })).toBeNull();
	});

	it("surfaces and retries incomplete discard cleanup with the audited reason", () => {
		renderPanel(
			artifact({
				qualityComparisonId: "comparison-1",
				qualityOutcome: "Passed",
				discardedAtUtc: 1_787_137_200_000,
				discardReason: "Superseded by a newer export",
				discardCleanupPending: true,
			}),
		);

		expect(screen.getByText("Cleanup pending")).toBeTruthy();
		fireEvent.click(screen.getByRole("button", { name: "Retry cleanup" }));
		expect(mocks.discard).toHaveBeenCalledWith(
			{
				path: { artifactId: "artifact-1" },
				body: { expectedVersion: 3, reason: "Superseded by a newer export" },
			},
			expect.any(Object),
		);
	});
});
