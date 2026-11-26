// @vitest-environment jsdom

import { fireEvent, screen, waitFor } from "@testing-library/react";
import i18next from "i18next";
import { HttpResponse, http } from "msw";
import { beforeAll, describe, expect, it, vi } from "vitest";

import { PlaygroundRunForm } from "@/features/modelLab/components/PlaygroundRunForm";
import de from "@/features/modelLab/locales/model-lab-de.json";
import en from "@/features/modelLab/locales/model-lab-en.json";
import { loadModelLabStrings } from "@/features/modelLab/models/ModelLabStrings";
import fixture from "@/features/modelLab/models/playgroundTrace.fixture.json";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const strings = en.pages.modelLab.playground;
const runId = "33333333-3333-4333-8333-333333333333";
const knobs = ["dataset", "learningRate", "depth", "hidden", "steps", "seed", "residual", "sharedBlock"] as const;

// Every start the page sends, answered with a saved run, or with `answer` when given.
function stubStart(answer?: () => Response): unknown[] {
	const bodies: unknown[] = [];
	server.use(
		http.post(localApiPath("model-lab/playground/runs"), async ({ request }) => {
			bodies.push(await request.json());
			return (
				answer?.() ??
				HttpResponse.json({
					id: runId,
					template: fixture.summary.template,
					config: fixture.summary.config,
					outcome: fixture.summary.outcome,
					finalLoss: fixture.summary.finalLoss,
					finalAccuracy: fixture.summary.finalAccuracy,
					traceByteCount: 10,
					createdAtUtc: "2026-11-25T00:00:00Z",
				})
			);
		}),
		http.get(localApiPath("model-lab/playground/runs"), () =>
			HttpResponse.json({ platformSupported: true, unsupportedMessage: null, maxRetainedRuns: 20, runs: [] }),
		),
	);
	return bodies;
}

function input(knob: string): HTMLInputElement {
	return screen.getByTestId(`model-lab-playground-${knob}`) as HTMLInputElement;
}

describe("PlaygroundRunForm", () => {
	beforeAll(() => loadModelLabStrings("en"));

	it("sends every knob with its default, then opens the saved run", async () => {
		const bodies = stubStart();
		const onStarted = vi.fn();
		renderWithProviders(<PlaygroundRunForm onStarted={onStarted} />);

		fireEvent.click(screen.getByRole("button", { name: strings.start }));

		await waitFor(() => expect(onStarted).toHaveBeenCalledWith(runId));
		expect(bodies).toEqual([
			{ dataset: "xor", learningRate: 0.5, residual: false, sharedBlock: false, depth: 2, hidden: 8, steps: 500, seed: 0 },
		]);
	});

	it("explains every knob in a help card, in English and German", async () => {
		renderWithProviders(<PlaygroundRunForm onStarted={vi.fn()} />);
		for (const knob of knobs) {
			expect(screen.getByTestId(`model-lab-playground-${knob}-info`)).toBeTruthy();
		}
		fireEvent.mouseEnter(screen.getByRole("button", { name: `About ${strings.knob.learningRate.label}` }));
		expect((await screen.findByRole("tooltip")).textContent).toContain(strings.knob.learningRate.help.split("\n")[0]);

		for (const knob of knobs) {
			expect(de.pages.modelLab.playground.knob[knob].help).not.toBe(strings.knob[knob].help);
			expect(de.pages.modelLab.playground.knob[knob].help).toContain("Sie");
		}
	});

	it.each([
		["learningRate", "0.0001", "10", "0.00009", "10.5"],
		["depth", "1", "4", "0", "5"],
		["hidden", "2", "16", "1", "17"],
		["steps", "1", "2000", "0", "2001"],
		["seed", "0", "2147483647", "-1", "2147483648"],
	])("sends %s at both bounds and refuses a value just outside them", async (knob, min, max, below, above) => {
		const bodies = stubStart();
		renderWithProviders(<PlaygroundRunForm onStarted={vi.fn()} />);
		const start = screen.getByRole("button", { name: strings.start });
		const range = i18next.t("pages.modelLab.playground.knob.range", { min, max });
		const refuses = async (value: string): Promise<void> => {
			fireEvent.change(input(knob), { target: { value } });
			fireEvent.click(start);
			expect(await screen.findByText(range)).toBeTruthy();
		};
		const sends = async (value: string, count: number): Promise<void> => {
			fireEvent.change(input(knob), { target: { value } });
			fireEvent.click(start);
			await waitFor(() => expect(bodies.length).toBe(count));
			await waitFor(() => expect(start.hasAttribute("data-loading")).toBe(false));
		};

		await refuses(below);
		await refuses(above);
		expect(bodies).toEqual([]);
		await sends(min, 1);
		await sends(max, 2);

		expect(bodies.map((body) => (body as Record<string, unknown>)[knob])).toEqual([Number(min), Number(max)]);
	});

	it.each([
		[
			"a 409 refusal",
			() => HttpResponse.json({ reason: "Busy", message: "A playground run is already running." }, { status: 409 }),
		],
		[
			"a 400 validation problem",
			() =>
				HttpResponse.json(
					{ status: 400, title: "One or more errors occurred!", detail: "Depth must be from 1 to 4.", errors: {} },
					{ status: 400 },
				),
		],
	])("shows the server's message for %s", async (_case, answer) => {
		stubStart(answer);
		const onStarted = vi.fn();
		renderWithProviders(<PlaygroundRunForm onStarted={onStarted} />);

		fireEvent.click(screen.getByRole("button", { name: strings.start }));

		const refused = await screen.findByTestId("model-lab-playground-start-refused");
		expect(refused.textContent).toMatch(/already running|Depth must be/);
		expect(onStarted).not.toHaveBeenCalled();
	});
});
