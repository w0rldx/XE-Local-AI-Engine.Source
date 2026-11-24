// @vitest-environment jsdom

// Model Lab's one evidence label set: every basis in both languages, the retired labels gone from both bundles, and a
// German sample from each panel family with the badge beside the value it labels.

import { act, fireEvent, screen, within } from "@testing-library/react";
import i18next from "i18next";
import { beforeAll, describe, expect, it, vi } from "vitest";

import { ArchitectureBlockTrace } from "@/features/modelLab/components/ArchitectureBlockTrace";
import { BasisBadge } from "@/features/modelLab/components/ArchitectureExplanation";
import { NextTokenStepView } from "@/features/modelLab/components/NextTokenStepView";
import { TraceStepCandidates } from "@/features/modelLab/components/TraceStepCandidates";
import de from "@/features/modelLab/locales/model-lab-de.json";
import en from "@/features/modelLab/locales/model-lab-en.json";
import type { ExplanationBasis } from "@/features/modelLab/models/ModelLabExplanation";
import { loadModelLabStrings } from "@/features/modelLab/models/ModelLabStrings";
import { renderWithProviders } from "@/test/RenderWithProviders";

beforeAll(async () => {
	await loadModelLabStrings("en");
	await loadModelLabStrings("de");
});

async function inGerman(render: () => void): Promise<void> {
	await act(() => i18next.changeLanguage("de"));
	try {
		render();
	} finally {
		await act(() => i18next.changeLanguage("en"));
	}
}

function badgeTexts(container: HTMLElement): (string | null)[] {
	return within(container)
		.getAllByTestId("model-lab-explain-basis")
		.map((badge) => badge.textContent);
}

const source = "llama.cpp src/models/qwen35.cpp @ b12014";

const labels: [ExplanationBasis, string, string][] = [
	["measuredLlamaCpp", "Measured by llama.cpp", "Von llama.cpp gemessen"],
	["measuredTrace", "Measured in the trace", "Im Trace gemessen"],
	["derived", "Derived", "Abgeleitet"],
	["structural", "Read from the file", "Aus der Datei gelesen"],
	[{ inferredFrom: source }, `Inferred from llama.cpp (${source})`, `Aus llama.cpp abgeleitet (${source})`],
	["simulated", "Simulated on this page", "Auf dieser Seite simuliert"],
	["interpreted", "Explanation, not measured", "Erklärung, nicht gemessen"],
	["unavailable", "Not available", "Nicht verfügbar"],
];

function valueAt(bundle: unknown, path: string): unknown {
	return path.split(".").reduce<unknown>((node, key) => (node as Record<string, unknown> | undefined)?.[key], bundle);
}

describe("BasisBadge", () => {
	it.each(labels)("labels %j in English and in German", async (basis, english, german) => {
		const { unmount } = renderWithProviders(<BasisBadge basis={basis} />);
		expect(screen.getByTestId("model-lab-explain-basis").textContent).toBe(english);
		unmount();

		await inGerman(() => {
			renderWithProviders(<BasisBadge basis={basis} />);
			expect(screen.getByTestId("model-lab-explain-basis").textContent).toBe(german);
		});
	});

	it.each([
		"pages.modelLab.explain.ui.basisBackground",
		"pages.modelLab.explain.ui.basisMeasurement",
		"pages.modelLab.explain.ui.basisLlamaCpp",
		"pages.modelLab.architecture.readBadge",
		"pages.modelLab.trace.measured",
		"pages.modelLab.nextToken.ui.measuredTitle",
	])("leaves the retired label %s out of both bundles", (key) => {
		expect(valueAt(en, key)).toBeUndefined();
		expect(valueAt(de, key)).toBeUndefined();
		// The parent is still there: the key itself is what is gone, not the section it sat in.
		expect(valueAt(en, key.slice(0, key.lastIndexOf(".")))).toBeTypeOf("object");
	});
});

describe("Evidence badges in German", () => {
	it("labels an Architecture block's trace summary as derived beside its values", async () => {
		await inGerman(() => {
			renderWithProviders(
				<ArchitectureBlockTrace
					untraceable={false}
					summaries={new Map([[1, { meanNorm: 2.5, maxP99: 3, topDimensions: [7] }]])}
					blockIndex={1}
					level="overview"
				/>,
			);
			expect(screen.getByTestId("model-lab-architecture-trace-summary").textContent).toContain("2,5");
			expect(screen.getByTestId("model-lab-architecture-trace-basis").textContent).toBe("Abgeleitet");
		});
	});

	it("labels llama.cpp's candidates as measured, the rest as derived and the sampler as simulated, with Chosen kept", async () => {
		await inGerman(() => {
			renderWithProviders(
				<NextTokenStepView
					entry={{
						text: "Hi",
						logprob: Math.log(0.6),
						isReasoning: false,
						alternatives: [
							{ text: "Hi", logprob: Math.log(0.6) },
							{ text: "Hey", logprob: Math.log(0.3) },
						],
					}}
					stepNumber={1}
				/>,
			);
			const measured = screen.getByTestId("model-lab-next-token-measured");
			expect(badgeTexts(measured)).toEqual(["Von llama.cpp gemessen", "Abgeleitet"]);
			expect(within(measured).getByTestId("model-lab-next-token-chosen")).toBeTruthy();
			expect(badgeTexts(screen.getByTestId("model-lab-next-token-simulated"))).toEqual(["Auf dieser Seite simuliert"]);
		});
	});

	it("labels a trace step's candidates as measured in the trace beside their raw/processed note, and the switch still works", async () => {
		const onBasisChange = vi.fn();
		await inGerman(() => {
			renderWithProviders(
				<TraceStepCandidates
					token={{
						id: 1,
						text: "Hi",
						logprob: Math.log(0.6),
						candidates: [
							{ id: 1, text: "Hi", logprob: Math.log(0.6) },
							{ id: 2, text: "Hey", logprob: Math.log(0.3) },
						],
						processedLogprob: Math.log(0.9),
						processedCandidates: [{ id: 1, text: "Hi", logprob: Math.log(0.9) }],
					}}
					step={0}
					candidateSteps={1}
					basis="raw"
					onBasisChange={onBasisChange}
				/>,
			);
			const measured = screen.getByTestId("model-lab-next-token-measured");
			expect(measured.textContent).toContain("Von PyTorch aus den rohen Logits des Modells");
			expect(badgeTexts(measured)).toEqual(["Im Trace gemessen", "Abgeleitet"]);

			fireEvent.click(screen.getByRole("radio", { name: "Verarbeitete Scores" }));
			expect(onBasisChange).toHaveBeenCalledWith("processed");
		});
	});
});
