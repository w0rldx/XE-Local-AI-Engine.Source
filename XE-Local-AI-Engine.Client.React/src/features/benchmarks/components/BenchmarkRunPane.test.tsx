// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import i18n from "i18next";
import { I18nextProvider } from "react-i18next";
import { afterEach, describe, expect, it, vi } from "vitest";

import { BenchmarkRunPane } from "@/features/benchmarks/components/BenchmarkRunPane";
import type { BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";
import "@/i18n";

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
afterEach(cleanup);

function run(overrides: Partial<BenchmarkRunDetail> = {}): BenchmarkRunDetail {
	return {
		id: "run-1",
		projectId: "p",
		primaryModelName: "model.gguf",
		primaryModelOrigin: null,
		modelContentFingerprint: "v1:test",
		agentName: "agent",
		agentVersion: 1,
		requestedContextTokens: 4096,
		primaryStatus: "Succeeded",
		judgeStatus: "Skipped",
		effectiveContextTokens: 4096,
		durationMs: 1250,
		totalTokens: 30,
		tokensPerSecond: 24,
		userScore: null,
		lastStreamSequence: 2,
		version: 3,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		outputParts: [{ kind: "output", content: "Answer" }],
		judgeResult: null,
		primaryErrorMessage: null,
		judgeErrorMessage: null,
		startedAtUtc: 1,
		primaryCompletedAtUtc: 2,
		judgeStartedAtUtc: null,
		judgeCompletedAtUtc: null,
		...overrides,
	};
}

function renderPane(value: BenchmarkRunDetail, onScore = vi.fn(), onCancel = vi.fn()) {
	return render(
		<I18nextProvider i18n={i18n}>
			<MantineProvider>
				<BenchmarkRunPane
					run={value}
					parts={value.outputParts}
					isConnected={false}
					isReconnecting={false}
					onScore={onScore}
					onCancel={onCancel}
					onDelete={vi.fn()}
				/>
			</MantineProvider>
		</I18nextProvider>,
	);
}

describe("BenchmarkRunPane", () => {
	it("renders persisted output, nullable provenance, metrics, and a distinct skipped judge explanation", () => {
		renderPane(run());
		expect(screen.getByText("Answer")).toBeTruthy();
		expect(screen.getByText("Legacy / Unknown")).toBeTruthy();
		expect(screen.getByText(/1.3s/)).toBeTruthy();
		expect(screen.getByText(/requested but skipped/i)).toBeTruthy();
	});

	it("offers five score values only after primary success", () => {
		const onScore = vi.fn();
		const { rerender } = renderPane(run({ primaryStatus: "Running" }), onScore);
		expect((screen.getByTestId("benchmark-score-5") as HTMLButtonElement).disabled).toBe(true);
		rerender(
			<I18nextProvider i18n={i18n}>
				<MantineProvider>
					<BenchmarkRunPane
						run={run()}
						parts={[]}
						isConnected={true}
						isReconnecting={false}
						onScore={onScore}
						onCancel={vi.fn()}
						onDelete={vi.fn()}
					/>
				</MantineProvider>
			</I18nextProvider>,
		);
		fireEvent.click(screen.getByTestId("benchmark-score-5"));
		expect(onScore).toHaveBeenCalledWith(5);
	});

	it("does not downgrade a successful primary when the judge fails", () => {
		renderPane(run({ judgeStatus: "Failed", judgeErrorMessage: "Judge output was invalid." }));
		expect(screen.getByText("Succeeded")).toBeTruthy();
		expect(screen.getByText("Judge output was invalid.")).toBeTruthy();
	});
});
