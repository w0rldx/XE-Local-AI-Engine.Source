// Decides which composer toolbar layout fits the composer's OWN width. The toolbar is one row that never wraps, so a
// narrow pane (a desktop window with the navigation sidebar and the conversation list both open) needs the same
// folded layout a phone gets, even though the viewport alone says "desktop". The widths are the rendered sizes of
// the toolbar's controls; each is an upper bound, so the estimate errs towards folding early, never towards overlap.

// Every toggle and icon button in the row is a 36px square, and so is the floor of the agent and workflow pickers
// (folded below the toolbar's container-query width, and never shrunk below it above that).
const iconWidthPx = 36;
// Mantine `gap={4}` between the row's controls, plus the root row's `gap="xs"` before Send.
const controlGapPx = 4;
const sendGapPx = 10;
// Send with its label, and the icon-only Send of the folded layout.
const labelledSendWidthPx = 90;
// The model trigger's floor (ModelSelectorCard.module.css `.compact-trigger` min-width). A phone's one-line trigger
// may go narrower still, which only leaves the estimate with slack.
const modelTriggerMinWidthPx = 120;
// The context readout with its compact and context-state buttons: a bare "1.1k/—" readout, and one with a progress bar.
const contextWidthPx = 100;
const contextWithProgressWidthPx = 150;
// "Answer now" beside Stop while a reasoning turn streams, a labelled button.
const answerNowWidthPx = 110;
// The Textarea's side padding around the bottomSection toolbar.
const composerChromePx = 28;

export interface ComposerToolbarControls {
	readonly localTools: boolean;
	/** The web review toggle is on screen (local tools on AND the node allows web access); inline in every layout. */
	readonly webReview: boolean;
	readonly knowledgeBase: boolean;
	readonly agentPicker: boolean;
	readonly workflowPicker: boolean;
	readonly attachments: boolean;
	readonly samplingOptions: boolean;
	readonly voice: boolean;
	readonly answerNow: boolean;
	/** `undefined` = no context readout; `true` = the readout draws a progress bar. */
	readonly contextProgress: boolean | undefined;
}

export interface ComposerToolbarFit {
	/** Fold the reasoning, local-tools, knowledge-base and sampling toggles into the overflow menu. */
	readonly compact: boolean;
	/** Room for the context readout and its two buttons on the row. */
	readonly showContext: boolean;
	/** Send keeps its label. */
	readonly labelledSend: boolean;
}

// Cheapest loss first: the context readout goes (as it does on a narrow phone), then Send's label, and only then do
// the toggles fold into the overflow menu, which is the one step that costs a click. Folding frees enough room to
// bring the readout back, as the compact phone layout already does; without it the last layout is the floor.
const floorLayout: ComposerToolbarFit = { compact: true, showContext: false, labelledSend: false };
const layouts: readonly ComposerToolbarFit[] = [
	{ compact: false, showContext: true, labelledSend: true },
	{ compact: false, showContext: false, labelledSend: true },
	{ compact: false, showContext: false, labelledSend: false },
	{ compact: true, showContext: true, labelledSend: false },
	floorLayout,
];

function rowWidth(controls: ComposerToolbarControls, layout: ComposerToolbarFit): number {
	const { compact } = layout;
	const icons = [
		compact, // the overflow menu trigger
		!compact, // reasoning
		!compact && controls.localTools,
		controls.webReview,
		!compact && controls.knowledgeBase,
		controls.agentPicker,
		controls.workflowPicker,
		controls.attachments,
		!compact && controls.samplingOptions,
		controls.voice,
	].filter(Boolean).length;
	const context =
		layout.showContext && controls.contextProgress !== undefined
			? (controls.contextProgress ? contextWithProgressWidthPx : contextWidthPx) + controlGapPx
			: 0;
	const answerNow = controls.answerNow ? answerNowWidthPx + controlGapPx : 0;
	const send = layout.labelledSend ? labelledSendWidthPx : iconWidthPx;
	return (
		composerChromePx + modelTriggerMinWidthPx + icons * (iconWidthPx + controlGapPx) + context + answerNow + sendGapPx + send
	);
}

/**
 * The first layout, in the order above, whose row fits a composer `composerWidth` px wide. The last one is the floor;
 * on a phone with every toggle on, the row's own horizontal scroll takes what is left rather than letting controls
 * overlap.
 */
export function fitComposerToolbar(composerWidth: number, controls: ComposerToolbarControls): ComposerToolbarFit {
	return layouts.find((layout) => rowWidth(controls, layout) <= composerWidth) ?? floorLayout;
}
