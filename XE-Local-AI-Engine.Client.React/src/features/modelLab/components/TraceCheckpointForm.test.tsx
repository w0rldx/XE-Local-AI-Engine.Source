// @vitest-environment jsdom

// The Record attention switch against a stubbed preflight (MSW): it follows the node's verdict on the checkpoint, and says
// why when it is off.

import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import i18next from "i18next";
import { HttpResponse, http } from "msw";
import { describe, expect, it } from "vitest";

import { TraceCheckpointForm } from "@/features/modelLab/components/TraceCheckpointForm";
import { loadModelLabStrings } from "@/features/modelLab/models/ModelLabStrings";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const gguf = "org/Tiny-MoE-GGUF:Q4_K_M";
const artifact = {
	id: "22222222-2222-4222-8222-222222222222",
	repoId: "allenai/OLMoE-1B-7B-0924-Instruct",
	revision: "7f1c97f440a4a5e1ad0d5c4b4a1d5b8f8f3e2a10",
	status: "Ready",
	totalBytes: 1,
	files: [],
	license: null,
	errorMessage: null,
	version: 1,
	createdAtUtc: 1,
	updatedAtUtc: 1,
	progress: null,
};
const confirmation =
	"The base checkpoint 'allenai/OLMoE-1B-7B-0924-Instruct' declares the license 'apache-2.0'. I confirm I am permitted to download these weights and to run experiments on them.";
const preflight = {
	repoId: artifact.repoId,
	revision: artifact.revision,
	weightBytes: 1,
	requiredVramBytes: 3_000_000_000,
	cardVramBytes: 34_000_000_000,
	license: "apache-2.0",
	isGated: false,
	checkpointHiddenSize: 2048,
	checkpointBlockCount: 16,
	steerableBlockCount: 16,
	modelType: "olmoe",
	checkpointLayerTypes: Array.from({ length: 16 }, () => "full_attention"),
	attentionSupported: true,
	attentionRefusalMessage: null,
	attentionRefusal: null,
	traceRefusalMessage: null,
	traceRefusal: null,
	defaultSteeringLayer: 10,
	ggufEmbeddingLength: 2048,
	ggufBlockCount: 16,
	metadataPresent: true,
	licenseConfirmationText: confirmation,
	refusal: null,
	refusalMessage: null,
};

async function chooseOption(label: RegExp, option: string): Promise<void> {
	const select = await screen.findByRole("combobox", { name: label });
	fireEvent.click(select);
	const listbox = await waitFor(() => {
		const element = document.getElementById(select.getAttribute("aria-controls") ?? "");
		if (element === null) {
			throw new Error("the dropdown is not open yet");
		}
		return element;
	});
	fireEvent.click(await within(listbox).findByRole("option", { name: option, hidden: true }));
}

async function recordAttentionAfterCheck(checked: Record<string, unknown>): Promise<HTMLInputElement> {
	server.use(
		http.get(localApiPath("models"), () =>
			HttpResponse.json({
				isAvailable: true,
				items: [
					{
						modelName: gguf,
						displayLabel: null,
						isSelected: false,
						kind: "Chat",
						detectedKind: "Chat",
						capabilities: [],
						isReasoningCapable: false,
						isToolCapable: true,
						isOverridden: false,
						provider: "llamacpp",
					},
				],
			}),
		),
		http.get(localApiPath("training/base-artifacts"), () => HttpResponse.json({ items: [artifact] })),
		http.get(localApiPath("training/base-artifacts/:artifactId"), () => HttpResponse.json(artifact)),
		http.post(localApiPath("model-lab/checkpoint-preflight"), () => HttpResponse.json({ ...preflight, ...checked })),
	);
	renderWithProviders(<TraceCheckpointForm busy={false} />);
	await chooseOption(/Installed GGUF/, gguf);
	await chooseOption(/Checkpoint on this node/, "allenai/OLMoE-1B-7B-0924-Instruct @ 7f1c97f440a4");
	fireEvent.click(screen.getByTestId("model-lab-check"));
	fireEvent.click(await screen.findByRole("checkbox", { name: confirmation }));
	// The switch's reason, when it has one, is part of its label.
	return (await screen.findByRole("switch", { name: /^Record attention/ })) as HTMLInputElement;
}

describe("TraceCheckpointForm's Record attention", () => {
	it("is offered when the node can record the checkpoint's attention", async () => {
		const record = await recordAttentionAfterCheck({});

		expect(record.disabled).toBe(false);
		expect(screen.queryByText(/Attention capture is implemented for/)).toBeNull();
	});

	it("is off and gives the node's reason in the page's language when the node cannot record this checkpoint's attention", async () => {
		const record = await recordAttentionAfterCheck({
			attentionSupported: false,
			attentionRefusal: "NotFullAttention",
			attentionRefusalMessage: "The server's English text.",
		});

		expect(record.disabled).toBe(true);
		expect(record.checked).toBe(false);
		expect(
			screen.getByText(
				"This OLMoE checkpoint's config describes attention other than full attention at every block, and attention is recorded only for full attention. Start the trace without it.",
			),
		).toBeTruthy();

		try {
			await loadModelLabStrings("de");
			await act(() => i18next.changeLanguage("de"));
			expect(
				screen.getByText(
					"Die Konfiguration dieses OLMoE-Checkpoints beschreibt in mindestens einem Block eine andere Attention als Full Attention, und Attention wird nur für Full Attention aufgezeichnet. Starten Sie den Trace ohne sie.",
				),
			).toBeTruthy();
		} finally {
			await act(() => i18next.changeLanguage("en"));
		}
	});

	it("names the missing full-attention block for its code", async () => {
		await recordAttentionAfterCheck({
			checkpointLayerTypes: Array.from({ length: 16 }, () => "linear_attention"),
			attentionSupported: false,
			attentionRefusal: "NoFullAttentionBlock",
			attentionRefusalMessage: "The server's English text.",
		});

		expect(screen.getByText("This checkpoint has no full-attention block, so there is no attention to record.")).toBeTruthy();
	});

	it("shows the server's text when it gives no code", async () => {
		const reason = "A reason without a code.";
		await recordAttentionAfterCheck({ attentionSupported: false, attentionRefusalMessage: reason });

		expect(screen.getByText(reason)).toBeTruthy();
	});

	it("falls back to the missing full-attention block when the node gives no reason", async () => {
		const record = await recordAttentionAfterCheck({
			checkpointLayerTypes: Array.from({ length: 16 }, () => "linear_attention"),
		});

		expect(record.disabled).toBe(true);
		expect(screen.getByText("This checkpoint has no full-attention block, so there is no attention to record.")).toBeTruthy();
	});
});

describe("TraceCheckpointForm's trace refusal", () => {
	it("shows the node's reason in the page's language and keeps the trace from starting", async () => {
		await recordAttentionAfterCheck({ traceRefusal: "RouterLogitsRequested", traceRefusalMessage: "The server's English text." });
		fireEvent.change(screen.getByRole("textbox", { name: "Prompt" }), { target: { value: "Hello" } });

		expect(screen.getByTestId("model-lab-trace-refused").textContent).toBe(
			"This checkpoint's config sets output_router_logits, which a trace does not support. It can still be steered.",
		);
		expect((screen.getByTestId("model-lab-start") as HTMLButtonElement).disabled).toBe(true);

		try {
			await loadModelLabStrings("de");
			await act(() => i18next.changeLanguage("de"));
			expect(screen.getByTestId("model-lab-trace-refused").textContent).toBe(
				"Die Konfiguration dieses Checkpoints setzt output_router_logits, was ein Trace nicht unterstützt. Steering ist weiterhin möglich.",
			);
		} finally {
			await act(() => i18next.changeLanguage("en"));
		}
	});

	it("shows the server's text when it gives no code", async () => {
		const reason = "A reason without a code.";
		await recordAttentionAfterCheck({ traceRefusalMessage: reason });

		expect(screen.getByTestId("model-lab-trace-refused").textContent).toBe(reason);
	});

	it("lets a checkpoint without one start a trace", async () => {
		await recordAttentionAfterCheck({});
		fireEvent.change(screen.getByRole("textbox", { name: "Prompt" }), { target: { value: "Hello" } });

		expect(screen.queryByTestId("model-lab-trace-refused")).toBeNull();
		expect((screen.getByTestId("model-lab-start") as HTMLButtonElement).disabled).toBe(false);
	});
});
