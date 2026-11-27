// @vitest-environment jsdom

import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import i18next from "i18next";
import { HttpResponse, http } from "msw";
import { useState } from "react";
import { beforeAll, describe, expect, it } from "vitest";

import { listModelLabPlaygroundRunsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { PlaygroundPanel } from "@/features/modelLab/components/PlaygroundPanel";
import de from "@/features/modelLab/locales/model-lab-de.json";
import en from "@/features/modelLab/locales/model-lab-en.json";
import { sameExperimentId } from "@/features/modelLab/models/ModelLabNavigation";
import { loadModelLabStrings } from "@/features/modelLab/models/ModelLabStrings";
import fixture from "@/features/modelLab/test/playgroundTrace.fixture.json";
import { acceptConfirm, openConfirm } from "@/test/ConfirmFlow";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const strings = en.pages.modelLab.playground;
const runId = "44444444-4444-4444-8444-444444444444";
const otherId = "55555555-5555-4555-8555-555555555555";

const savedRun = (id: string) => ({
	id,
	template: fixture.summary.template,
	config: fixture.summary.config,
	outcome: fixture.summary.outcome,
	finalLoss: fixture.summary.finalLoss,
	finalAccuracy: fixture.summary.finalAccuracy,
	traceByteCount: 145_000,
	createdAtUtc: "2026-11-25T00:00:00Z",
});

// The list holds both runs; each answers with `trace` (the fixture's by default). Counts the run reads.
function stubRuns(trace: unknown = fixture.trace, platformSupported = true): { reads: string[]; lists: () => number } {
	const reads: string[] = [];
	let lists = 0;
	server.use(
		http.get(localApiPath("model-lab/playground/runs"), () => {
			lists++;
			return HttpResponse.json({
				platformSupported,
				unsupportedMessage: platformSupported ? null : "The training stepper runs on Linux x86_64 only.",
				maxRetainedRuns: 20,
				runs: [savedRun(runId), savedRun(otherId)],
			});
		}),
		http.get(localApiPath("model-lab/playground/runs/:runId"), ({ params }) => {
			reads.push(String(params["runId"]));
			return HttpResponse.json({ run: savedRun(String(params["runId"])), trace });
		}),
	);
	return { reads, lists: () => lists };
}

// The page's part: the URL's run, closed only by its own delete.
function Panel({ shown = true, initial }: { shown?: boolean; initial?: string }) {
	const [run, setRun] = useState(initial);
	return (
		<ConfirmProvider>
			<p data-testid="run-in-url">{run ?? ""}</p>
			<PlaygroundPanel
				shown={shown}
				runId={run}
				onRunChange={setRun}
				onRunDeleted={(removed) => setRun((open) => (sameExperimentId(open, removed) ? undefined : open))}
			/>
		</ConfirmProvider>
	);
}

async function openStepper(): Promise<HTMLElement> {
	return screen.findByTestId("model-lab-playground-stepper");
}

function phase(name: string): void {
	fireEvent.click(within(screen.getByTestId("model-lab-playground-phase")).getByRole("radio", { name }));
}

function chip(group: string, name: string): void {
	fireEvent.click(within(screen.getByTestId(group)).getByRole("button", { name }));
}

// The fixture's trace with its first step edited, and the run's outcome diverged there.
function diverged(edit: (step: (typeof fixture.trace.recorded)[number]) => object) {
	const [first] = fixture.trace.recorded;
	if (first === undefined) {
		throw new Error("the fixture has no recorded step");
	}
	return {
		...fixture.trace,
		recorded: [edit(structuredClone(first))],
		outcome: { kind: "diverged", atStep: 0, updatesApplied: 0 },
	};
}

describe("PlaygroundPanel", () => {
	beforeAll(() => loadModelLabStrings("en"));

	it("reads nothing before its tab has shown", async () => {
		const stub = stubRuns();
		renderWithProviders(<Panel shown={false} initial={runId} />);
		await act(async () => undefined);

		expect(stub.lists()).toBe(0);
		expect(stub.reads).toEqual([]);
		expect(screen.queryByTestId("model-lab-playground-runs")).toBeNull();
	});

	it("says why a platform has no training stepper, and still lists, replays and deletes its saved runs", async () => {
		stubRuns(fixture.trace, false);
		renderWithProviders(<Panel initial={runId} />);

		expect((await screen.findByTestId("model-lab-playground-unsupported")).textContent).toContain("Linux x86_64");
		expect(screen.queryByTestId("model-lab-playground-start")).toBeNull();
		expect(screen.getAllByTestId("model-lab-playground-run").length).toBe(2);
		await openStepper();
		const [first] = screen.getAllByTestId("model-lab-playground-run");
		expect(within(first as HTMLElement).getByRole("button", { name: /^Delete / })).toBeTruthy();
	});

	it("keeps the form's edits and the open run when a refetch of the cached list fails", async () => {
		stubRuns();
		const { queryClient } = renderWithProviders(<Panel initial={runId} />);
		await openStepper();
		const seed = screen.getByTestId("model-lab-playground-seed") as HTMLInputElement;
		fireEvent.change(seed, { target: { value: "7" } });
		fireEvent.click(screen.getByTestId("model-lab-playground-next"));
		const position = i18next.t("pages.modelLab.playground.position", { index: 2, count: 8, step: 2, steps: 8 });
		expect(screen.getByTestId("model-lab-playground-position").textContent).toBe(position);

		server.use(http.get(localApiPath("model-lab/playground/runs"), () => new HttpResponse(null, { status: 500 })));
		await act(() => queryClient.refetchQueries({ queryKey: listModelLabPlaygroundRunsQueryKey() }));

		expect(await screen.findByTestId("model-lab-playground-list-failed")).toBeTruthy();
		expect((screen.getByTestId("model-lab-playground-seed") as HTMLInputElement).value).toBe("7");
		expect(screen.getByTestId("model-lab-playground-position").textContent).toBe(position);
		expect(screen.getByTestId("run-in-url").textContent).toBe(runId);
	});

	it("names the opened run's saved learning rate and seed, so runs that differ only in those stay apart", async () => {
		const tuned = { ...savedRun(runId), config: { ...fixture.summary.config, learningRate: 0.05, seed: 3 } };
		server.use(
			http.get(localApiPath("model-lab/playground/runs"), () =>
				HttpResponse.json({
					platformSupported: true,
					unsupportedMessage: null,
					maxRetainedRuns: 20,
					runs: [tuned, savedRun(otherId)],
				}),
			),
			http.get(localApiPath("model-lab/playground/runs/:runId"), () => HttpResponse.json({ run: tuned, trace: fixture.trace })),
		);
		renderWithProviders(<Panel initial={runId} />);
		await openStepper();

		const [opened, other] = screen.getAllByTestId("model-lab-playground-run").map((row) => within(row).getAllByRole("button")[0]);
		expect(opened?.getAttribute("aria-current")).toBe("true");
		expect(opened?.textContent).toMatch(/, learning rate 0\.05, seed 3$/);
		expect(other?.textContent).toMatch(/, learning rate 0\.5, seed 0$/);
	});

	it("opens a linked run once the list holds it, and reads no run the list lacks", async () => {
		const stub = stubRuns();
		const { unmount } = renderWithProviders(<Panel initial="66666666-6666-4666-8666-666666666666" />);
		await screen.findByTestId("model-lab-playground-runs");
		expect(stub.reads).toEqual([]);
		unmount();

		renderWithProviders(<Panel initial={runId.toUpperCase()} />);
		await openStepper();
		expect(stub.reads).toEqual([runId]);
		expect(screen.getByTestId("model-lab-playground-retention").textContent).toBe(
			i18next.t("pages.modelLab.playground.retention", { count: 20 }),
		);
	});

	it("says the values are recorded playback, in English and in German", async () => {
		stubRuns();
		renderWithProviders(<Panel initial={runId} />);
		await openStepper();
		expect(screen.getByTestId("model-lab-playground-provenance").textContent).toContain(strings.provenance);

		await loadModelLabStrings("de");
		try {
			await act(() => i18next.changeLanguage("de"));
			expect(screen.getByTestId("model-lab-playground-provenance").textContent).toContain(
				de.pages.modelLab.playground.provenance,
			);
			expect(screen.getByRole("radio", { name: de.pages.modelLab.playground.phase.update.label })).toBeTruthy();
		} finally {
			await act(() => i18next.changeLanguage("en"));
		}
	});

	// A hand-edited `i18nextLng` such as `en_US` is not a well-formed tag; the replay still renders its numbers.
	it("replays a run under a malformed stored language tag", async () => {
		stubRuns();
		await i18next.changeLanguage("en_US");
		try {
			renderWithProviders(<Panel initial={runId} />);
			await openStepper();
			expect(screen.getByTestId("model-lab-playground-final").textContent).toMatch(/\d/);
			phase(strings.phase.update.label);
			expect(screen.getByTestId("model-lab-playground-update-check").textContent).toMatch(/\d/);
		} finally {
			await act(() => i18next.changeLanguage("en"));
		}
	});

	it("makes the loss row and each chip row one Tab stop, on the current or selected button", async () => {
		stubRuns();
		renderWithProviders(<Panel initial={runId} />);
		await openStepper();
		const stops = (row: string) =>
			within(screen.getByTestId(row))
				.getAllByRole("button")
				.map((button) => button.getAttribute("tabindex"));
		const losses = within(screen.getByTestId("model-lab-playground-losses")).getAllByRole("button");
		expect(stops("model-lab-playground-losses")).toEqual(["0", "-1", "-1", "-1", "-1", "-1", "-1", "-1"]);

		// The Tab stop follows focus, so Tab leaves the row from where the arrows went; a new step brings it back.
		losses[0]?.focus();
		fireEvent.keyDown(losses[0] as HTMLElement, { key: "End" });
		expect(document.activeElement).toBe(losses[7]);
		expect(stops("model-lab-playground-losses")).toEqual(["-1", "-1", "-1", "-1", "-1", "-1", "-1", "0"]);
		fireEvent.click(screen.getByTestId("model-lab-playground-next"));
		expect(stops("model-lab-playground-losses")).toEqual(["-1", "0", "-1", "-1", "-1", "-1", "-1", "-1"]);

		expect(stops("model-lab-playground-occurrences")).toEqual(["0", "-1", "-1", "-1"]);
		chip("model-lab-playground-occurrences", "head");
		expect(stops("model-lab-playground-occurrences")).toEqual(["-1", "-1", "-1", "0"]);
	});

	it("steps through the recorded steps and shows each visit of the shared block on its one parameter set", async () => {
		stubRuns();
		renderWithProviders(<Panel initial={runId} />);
		await openStepper();
		const position = () => screen.getByTestId("model-lab-playground-position").textContent;
		expect(position()).toBe(i18next.t("pages.modelLab.playground.position", { index: 1, count: 8, step: 1, steps: 8 }));

		fireEvent.click(screen.getByTestId("model-lab-playground-next"));
		expect(position()).toBe(i18next.t("pages.modelLab.playground.position", { index: 2, count: 8, step: 2, steps: 8 }));
		fireEvent.click(within(screen.getByTestId("model-lab-playground-losses")).getAllByRole("button")[7] as HTMLElement);
		expect(position()).toBe(i18next.t("pages.modelLab.playground.position", { index: 8, count: 8, step: 8, steps: 8 }));

		// Forward: the block runs twice, in execution order between the input and the output layer.
		const visit = (n: number) => i18next.t("pages.modelLab.playground.visit", { module: "block", visit: n });
		const occurrences = within(screen.getByTestId("model-lab-playground-occurrences")).getAllByRole("button");
		expect(occurrences.map((button) => button.textContent)).toEqual(["embed", visit(1), visit(2), "head"]);
		chip("model-lab-playground-occurrences", visit(2));
		// A block's recorded output is after its tanh; the head's is its Linear output, the logit.
		expect(screen.getByText("Output of block, visit 2 (after tanh)")).toBeTruthy();
		chip("model-lab-playground-occurrences", "head");
		expect(screen.getByText("Output of head")).toBeTruthy();

		// Backward: block.weight has one gradient per visit, and they add up to its one gradient.
		phase(strings.phase.backward.label);
		expect(screen.getByText("Gradient at the output of embed (after tanh)")).toBeTruthy();
		chip("model-lab-playground-grad-occurrences", "head");
		expect(screen.getByText("Gradient at the output of head")).toBeTruthy();
		chip("model-lab-playground-grad-parameters", "block.weight");
		expect(
			screen.getByText(i18next.t("pages.modelLab.playground.visitGrad", { parameter: "block.weight", module: visit(1) })),
		).toBeTruthy();
		expect(
			screen.getByText(i18next.t("pages.modelLab.playground.visitGrad", { parameter: "block.weight", module: visit(2) })),
		).toBeTruthy();
		const visitCheck = screen.getByTestId("model-lab-playground-visit-check");
		expect(within(visitCheck).getByTestId("model-lab-explain-basis").textContent).toBe(en.pages.modelLab.explain.ui.basisDerived);
		expect(visitCheck.textContent).toMatch(/within the tolerance/);
		chip("model-lab-playground-grad-parameters", "embed.weight");
		expect(screen.getByTestId("model-lab-playground-visit-check").textContent).toContain(strings.gap.notShared);

		// Update: one update of the shared weight, matching SGD.
		phase(strings.phase.update.label);
		chip("model-lab-playground-update-parameters", "block.weight");
		const updateCheck = screen.getByTestId("model-lab-playground-update-check");
		expect(updateCheck.textContent).toMatch(/within the tolerance/);
		expect(screen.getAllByTestId("model-lab-playground-map").length).toBe(2);
		// Every value states its basis.
		for (const values of screen.getAllByTestId("model-lab-playground-values")) {
			expect(within(values).getByTestId("model-lab-explain-basis").textContent).toBe(
				en.pages.modelLab.explain.ui.basisMeasuredTrace,
			);
		}
	});

	it("case (a): a non-finite loss shows forward and loss, and says no backward pass or update ran", async () => {
		stubRuns(diverged((step) => ({ ...step, loss: { ...step.loss, value: null }, backward: null, update: null })));
		renderWithProviders(<Panel initial={runId} />);
		await openStepper();

		expect(screen.getByTestId("model-lab-playground-outcome").textContent).toContain(
			i18next.t("pages.modelLab.playground.outcome.diverged", { step: 1, updates: 0, steps: 8 }),
		);
		phase(strings.phase.loss.label);
		expect(screen.getByTestId("model-lab-playground-loss").textContent).toContain(strings.notFinite);
		for (const name of [strings.phase.backward.label, strings.phase.update.label]) {
			phase(name);
			const missing = screen.getByTestId("model-lab-playground-phase-missing");
			expect(missing.textContent).toContain(strings.gap.lossNotFinite);
			expect(within(missing).getByTestId("model-lab-explain-basis").textContent).toBe(
				en.pages.modelLab.explain.ui.basisUnavailable,
			);
		}
	});

	it("case (b): a non-finite gradient shows the backward pass and says no update ran", async () => {
		stubRuns(
			diverged((step) => ({
				...step,
				backward: {
					...step.backward,
					parameterGrads: step.backward.parameterGrads.map((item) =>
						item.p === 3 ? { ...item, grad: [null, ...item.grad.slice(1)] } : item,
					),
				},
				update: null,
			})),
		);
		renderWithProviders(<Panel initial={runId} />);
		await openStepper();

		phase(strings.phase.backward.label);
		chip("model-lab-playground-grad-parameters", "block.bias");
		expect(screen.getByTestId("model-lab-playground-visit-check").textContent).toContain(strings.gap.nonFinite);
		expect(screen.getAllByText(strings.notFinite).length).toBeGreaterThan(0);
		phase(strings.phase.update.label);
		expect(screen.getByTestId("model-lab-playground-phase-missing").textContent).toContain(strings.gap.gradientNotFinite);
	});

	it("says a saved trace it cannot read is unreadable", async () => {
		stubRuns({ formatVersion: 2 });
		renderWithProviders(<Panel initial={runId} />);

		expect((await screen.findByTestId("model-lab-playground-trace-unreadable")).textContent).toBe(strings.traceUnreadable);
	});

	it("opens a run by its click and takes a deleted open run out of the URL", async () => {
		const stub = stubRuns();
		server.use(http.delete(localApiPath(`model-lab/playground/runs/${runId}`), () => new HttpResponse(null, { status: 204 })));
		renderWithProviders(<Panel />);
		const [first] = await screen.findAllByTestId("model-lab-playground-run");
		fireEvent.click(within(first as HTMLElement).getAllByRole("button")[0] as HTMLElement);
		await openStepper();
		expect(screen.getByTestId("run-in-url").textContent).toBe(runId);
		const before = stub.lists();

		const remove = within(first as HTMLElement).getByRole("button", { name: /^Delete / });
		await openConfirm(remove, strings.deleteConfirm.title);
		acceptConfirm();

		await waitFor(() => expect(screen.getByTestId("run-in-url").textContent).toBe(""));
		await waitFor(() => expect(stub.lists()).toBeGreaterThan(before));
	});
});
