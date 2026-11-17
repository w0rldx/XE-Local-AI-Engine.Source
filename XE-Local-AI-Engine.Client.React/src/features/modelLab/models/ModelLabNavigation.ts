import { z } from "zod";

// The page's tabs, in display order. `?tab=` on the route takes one of these, so a tab is linkable and survives a reload.
export const modelLabTabs = ["compare", "nextToken", "inspect", "trace", "architecture"] as const;
export type ModelLabTab = (typeof modelLabTabs)[number];

// An unknown or mistyped `?tab=` falls back to the default tab instead of failing the route.
export const modelLabSearchSchema = z.object({
	tab: z.enum(modelLabTabs).optional().catch(undefined),
});

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
