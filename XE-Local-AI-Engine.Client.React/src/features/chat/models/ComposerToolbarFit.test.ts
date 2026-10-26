import { describe, expect, it } from "vitest";

import { type ComposerToolbarControls, fitComposerToolbar } from "@/features/chat/models/ComposerToolbarFit";

const plainChat: ComposerToolbarControls = {
	localTools: false,
	webReview: false,
	knowledgeBase: true,
	agentPicker: true,
	workflowPicker: true,
	attachments: true,
	samplingOptions: false,
	voice: false,
	answerNow: false,
	contextProgress: false,
};

const everyToggle: ComposerToolbarControls = {
	...plainChat,
	localTools: true,
	webReview: true,
	samplingOptions: true,
	voice: true,
	contextProgress: true,
};

describe("fitComposerToolbar", () => {
	it("keeps the full row on a roomy composer", () => {
		expect(fitComposerToolbar(1200, everyToggle)).toEqual({
			labelledPickers: true,
			compact: false,
			showContext: true,
			labelledSend: true,
		});
	});

	// A 1440px window with the sidebar and the conversation list open leaves the composer ~786px: the pickers keep naming
	// their choice at the cost of the context readout, and fold (bringing the readout back) only when that is not enough.
	it("keeps the pickers labelled over the context readout, then folds them", () => {
		expect(fitComposerToolbar(900, plainChat)).toEqual({
			labelledPickers: true,
			compact: false,
			showContext: true,
			labelledSend: true,
		});
		expect(fitComposerToolbar(786, plainChat)).toEqual({
			labelledPickers: true,
			compact: false,
			showContext: false,
			labelledSend: true,
		});
		expect(fitComposerToolbar(700, plainChat)).toEqual({
			labelledPickers: false,
			compact: false,
			showContext: true,
			labelledSend: true,
		});
	});

	// A 1280px window with the navigation sidebar and the conversation list both open leaves the composer ~626px.
	it("gives up the context readout, then Send's label, before folding a toggle", () => {
		const toolsOn = { ...plainChat, localTools: true };
		expect(fitComposerToolbar(626, toolsOn)).toEqual({
			labelledPickers: false,
			compact: false,
			showContext: true,
			labelledSend: true,
		});
		expect(fitComposerToolbar(560, toolsOn)).toEqual({
			labelledPickers: false,
			compact: false,
			showContext: false,
			labelledSend: true,
		});
		expect(fitComposerToolbar(470, toolsOn)).toEqual({
			labelledPickers: false,
			compact: false,
			showContext: false,
			labelledSend: false,
		});
	});

	// A 1024px window with both side panes open leaves the composer ~370px.
	it("folds the toggles of a narrow composer last, and keeps the readout only if the folded row has room", () => {
		expect(fitComposerToolbar(370, plainChat)).toEqual({
			labelledPickers: false,
			compact: true,
			showContext: false,
			labelledSend: false,
		});
		// Folding frees more than the readout costs only when several toggles fold at once.
		const manyToggles = { ...everyToggle, contextProgress: false };
		expect(fitComposerToolbar(540, manyToggles)).toEqual({
			labelledPickers: false,
			compact: true,
			showContext: true,
			labelledSend: false,
		});
		expect(fitComposerToolbar(470, manyToggles)).toEqual({
			labelledPickers: false,
			compact: true,
			showContext: false,
			labelledSend: false,
		});
	});

	it("budgets the web review switch only when it is on screen", () => {
		const toolsOn = { ...plainChat, localTools: true };
		expect(fitComposerToolbar(626, { ...toolsOn, webReview: false }).showContext).toBe(true);
		expect(fitComposerToolbar(626, { ...toolsOn, webReview: true }).showContext).toBe(false);
	});
});
