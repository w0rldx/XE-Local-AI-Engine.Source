// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { BenchmarkLaunchEvidencePanel } from "@/features/benchmarks/components/BenchmarkLaunchEvidencePanel";
import type { BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";
import { noBenchmarkLaunchFacts } from "@/features/benchmarks/models/BenchmarkModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

// "What we meant to launch" vs "what launched" is the one comparison a single run can make on its own, and both halves
// of it are recorded: the projection identity AND the executable digest. A drift in either is real, on either side.

afterEach(cleanup);

function detail(overrides: Partial<BenchmarkRunDetail> = {}): BenchmarkRunDetail {
	return {
		id: "run-1",
		projectId: "project-1",
		primaryModelName: "model.gguf",
		primaryModelOrigin: null,
		modelContentFingerprint: "v1:test",
		agentName: "agent",
		agentVersion: 1,
		requestedContextTokens: 4096,
		primaryStatus: "Succeeded",
		judgeStatus: "Succeeded",
		effectiveContextTokens: 4096,
		durationMs: 1,
		totalTokens: 1,
		tokensPerSecond: 1,
		userScore: null,
		lastStreamSequence: 1,
		version: 1,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		primaryLaunch: { ...noBenchmarkLaunchFacts },
		judgeLaunch: { ...noBenchmarkLaunchFacts },
		primaryLaunchReceipt: null,
		judgeLaunchReceipt: null,
		primaryEnvironmentFacts: null,
		judgeEnvironmentFacts: null,
		outputParts: [],
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

describe("BenchmarkLaunchEvidencePanel", () => {
	// The identity hashes the projection, not the binary: swapping the executable under a byte-identical projection
	// leaves the identity equal, and that is exactly the drift worth reporting.
	it("reports an executable-digest drift even when the launch identity matched", () => {
		renderWithProviders(
			<BenchmarkLaunchEvidencePanel
				run={detail({
					primaryLaunch: {
						...noBenchmarkLaunchFacts,
						intendedLaunchIdentity: "identity-1",
						effectiveLaunchIdentity: "identity-1",
						intendedExecutableSha256: "a".repeat(64),
						executableSha256: "b".repeat(64),
					},
				})}
			/>,
		);

		expect(screen.getByTestId("benchmark-intended-effective-differs")).toBeTruthy();
	});

	// A row with only one end recorded is a gap in the evidence, not a difference between two observed values.
	it("stays silent when one side of every row was never recorded", () => {
		renderWithProviders(
			<BenchmarkLaunchEvidencePanel
				run={detail({
					primaryLaunch: { ...noBenchmarkLaunchFacts, intendedLaunchIdentity: "identity-1", intendedExecutableSha256: "a" },
				})}
			/>,
		);

		expect(screen.queryByTestId("benchmark-intended-effective-differs")).toBeNull();
	});

	it("reports the judge side independently of the primary side", () => {
		renderWithProviders(
			<BenchmarkLaunchEvidencePanel
				run={detail({
					judgeLaunch: {
						...noBenchmarkLaunchFacts,
						intendedLaunchIdentity: "identity-1",
						effectiveLaunchIdentity: "identity-2",
					},
				})}
			/>,
		);

		expect(screen.queryByTestId("benchmark-intended-effective-differs")).toBeNull();
		expect(screen.getByTestId("benchmark-judge-intended-effective-differs")).toBeTruthy();
	});

	// A runtime-bundle listing runs to hundreds of rows; none of them are built until the operator asks for them.
	it("keeps the evidence tables out of the DOM until their section is opened", () => {
		renderWithProviders(
			<BenchmarkLaunchEvidencePanel run={detail({ primaryLaunchReceipt: { variant: "cuda", os: "linux" } })} />,
		);

		expect(screen.queryByTestId("benchmark-primary-receipt")).toBeNull();

		fireEvent.click(screen.getByRole("button", { name: "Launch receipt" }));

		expect(screen.getByTestId("benchmark-primary-receipt")).toBeTruthy();
		expect(screen.getByText("receipt.variant")).toBeTruthy();
	});
});
