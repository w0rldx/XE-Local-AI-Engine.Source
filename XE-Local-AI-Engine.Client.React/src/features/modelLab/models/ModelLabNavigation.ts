// A jump from one Model Lab tab to another, with what the target tab should open.
export type ModelLabJump =
	| { tab: "architecture"; modelName: string; traceId: string; blockIndex: number }
	| { tab: "trace"; experimentId: string }
	| { tab: "nextToken"; modelName: string; prompt: string; disableThinking: boolean };

// The page numbers every jump, so the same jump twice applies twice. A panel applies each nonce once, during render,
// and a later edit in the panel wins over it.
export type ModelLabIntent<Tab extends ModelLabJump["tab"] = ModelLabJump["tab"]> = Extract<ModelLabJump, { tab: Tab }> & {
	nonce: number;
};
