import { z } from "zod";

import type { XeLocalAiEngineProvidersAbstractionsContractsLocalModelOrigin } from "@/core/api/generated";
import type { ChatMessagePart, ToolCallState } from "@/features/chat/models/ChatModels";

const benchmarkPrimaryStatuses = ["Queued", "Running", "CancelRequested", "Succeeded", "Failed", "Cancelled"] as const;
export type BenchmarkPrimaryStatus = (typeof benchmarkPrimaryStatuses)[number];
const benchmarkJudgeStatuses = [
	"Disabled",
	"Pending",
	"Skipped",
	"Queued",
	"Running",
	"Succeeded",
	"Failed",
	"Cancelled",
] as const;
export type BenchmarkJudgeStatus = (typeof benchmarkJudgeStatuses)[number];
export type BenchmarkOrigin = XeLocalAiEngineProvidersAbstractionsContractsLocalModelOrigin | null;

export interface BenchmarkProjectSummary {
	id: string;
	name: string;
	contextTokens: number;
	agentDefinitionId: string;
	judgeEnabled: boolean;
	runCount: number;
	isFrozen: boolean;
	version: number;
	createdAtUtc: number;
	updatedAtUtc: number;
}

export interface BenchmarkProjectDetail extends BenchmarkProjectSummary {
	coreTask: string;
	judgeModelName: string | null;
	judgeContextTokens: number | null;
	judgePromptVersion: number;
	judgeOutputSchemaVersion: number;
}

export interface BenchmarkProjectDraft {
	name: string;
	coreTask: string;
	contextTokens: number;
	agentDefinitionId: string;
	judgeEnabled: boolean;
	judgeModelName: string | null;
	judgeContextTokens: number | null;
	judgePromptVersion: number;
	judgeOutputSchemaVersion: number;
}

export interface BenchmarkEligibleModel {
	modelName: string;
	maxContextTokens: number | null;
	effectiveContextTokens: number | null;
	origin: BenchmarkOrigin;
	modelContentFingerprint: string;
	supportsTools: boolean;
}

export interface BenchmarkJudgeResult {
	schemaVersion: number;
	score: number;
	rationale: string;
	judgeModelContentFingerprint: string;
	promptVersion: number;
}

export interface BenchmarkOutputPart {
	kind: string;
	content?: string | null;
	toolCallId?: string | null;
	toolName?: string | null;
	arguments?: string | null;
	result?: string | null;
	isError?: boolean | null;
}

export interface BenchmarkRunSummary {
	id: string;
	projectId: string;
	primaryModelName: string;
	primaryModelOrigin: BenchmarkOrigin;
	modelContentFingerprint: string;
	agentName: string;
	agentVersion: number;
	requestedContextTokens: number;
	primaryStatus: BenchmarkPrimaryStatus;
	judgeStatus: BenchmarkJudgeStatus;
	effectiveContextTokens: number | null;
	durationMs: number | null;
	totalTokens: number | null;
	tokensPerSecond: number | null;
	userScore: number | null;
	lastStreamSequence: number;
	version: number;
	createdAtUtc: number;
	updatedAtUtc: number;
}

export interface BenchmarkRunDetail extends BenchmarkRunSummary {
	outputParts: BenchmarkOutputPart[];
	judgeResult: BenchmarkJudgeResult | null;
	primaryErrorMessage: string | null;
	judgeErrorMessage: string | null;
	startedAtUtc: number | null;
	primaryCompletedAtUtc: number | null;
	judgeStartedAtUtc: number | null;
	judgeCompletedAtUtc: number | null;
}

const benchmarkRunEventKinds = [
	"OutputDelta",
	"ReasoningDelta",
	"ToolCall",
	"ToolResult",
	"PrimaryState",
	"JudgeState",
	"Metrics",
	"TerminalSnapshotAvailable",
] as const;

export const benchmarkRunEventSchema = z.object({
	runId: z.string(),
	sequence: z.number().int().nonnegative(),
	kind: z.enum(benchmarkRunEventKinds),
	payload: z.object({
		content: z.string().nullish(),
		state: z.string().nullish(),
		toolCallId: z.string().nullish(),
		toolName: z.string().nullish(),
		arguments: z.string().nullish(),
		result: z.string().nullish(),
		isError: z.boolean().nullish(),
		effectiveContextTokens: z.number().int().nullish(),
		durationMs: z.number().int().nullish(),
		totalTokens: z.number().int().nullish(),
		tokensPerSecond: z.number().nullish(),
		runVersion: z.number().int().nullish(),
	}),
});
export type BenchmarkRunEvent = z.infer<typeof benchmarkRunEventSchema>;

export const benchmarkReplayResetSchema = z.object({
	runId: z.string(),
	latestSequence: z.number().int().nonnegative(),
	runVersion: z.number().int().nonnegative(),
});
export type BenchmarkReplayReset = z.infer<typeof benchmarkReplayResetSchema>;

function mergeTextPart(parts: BenchmarkOutputPart[], kind: "output" | "reasoning", content: string): BenchmarkOutputPart[] {
	if (!content) {
		return parts;
	}
	const next = [...parts];
	const last = next.at(-1);
	if (last?.kind === kind) {
		next[next.length - 1] = { ...last, content: `${last.content ?? ""}${content}` };
	} else {
		next.push({ kind, content });
	}
	return next;
}

export function applyBenchmarkEvent(parts: BenchmarkOutputPart[], event: BenchmarkRunEvent): BenchmarkOutputPart[] {
	if (event.kind === "OutputDelta") {
		return mergeTextPart(parts, "output", event.payload.content ?? "");
	}
	if (event.kind === "ReasoningDelta") {
		return mergeTextPart(parts, "reasoning", event.payload.content ?? "");
	}
	if (event.kind === "ToolCall") {
		return [
			...parts,
			{
				kind: "tool_call",
				toolCallId: event.payload.toolCallId,
				toolName: event.payload.toolName,
				arguments: event.payload.arguments,
			},
		];
	}
	if (event.kind === "ToolResult") {
		return [
			...parts,
			{
				kind: "tool_result",
				toolCallId: event.payload.toolCallId,
				toolName: event.payload.toolName,
				result: event.payload.result,
				isError: event.payload.isError,
			},
		];
	}
	return parts;
}

function toolState(isError: boolean | null | undefined): ToolCallState {
	return isError ? "failed" : "received";
}

export function toChatMessageParts(parts: readonly BenchmarkOutputPart[]): ChatMessagePart[] {
	const rendered: ChatMessagePart[] = [];
	const tools = new Map<string, number>();
	let sequence = 0;
	for (const part of parts) {
		sequence += 1;
		if ((part.kind === "output" || part.kind === "text") && part.content) {
			rendered.push({ kind: "text", id: `benchmark-text-${sequence}`, sequence, text: part.content });
			continue;
		}
		if (part.kind === "reasoning" && part.content) {
			rendered.push({ kind: "reasoning", id: `benchmark-reasoning-${sequence}`, sequence, text: part.content });
			continue;
		}
		if (part.kind === "tool_call") {
			const id = part.toolCallId || `benchmark-tool-${sequence}`;
			tools.set(id, rendered.length);
			rendered.push({
				kind: "tool",
				id,
				sequence,
				name: part.toolName || "tool",
				state: "requesting",
				args: part.arguments ?? undefined,
			});
			continue;
		}
		if (part.kind === "tool_result") {
			const id = part.toolCallId || `benchmark-tool-${sequence}`;
			const index = tools.get(id);
			if (index !== undefined) {
				const existing = rendered[index];
				if (existing?.kind === "tool") {
					rendered[index] = {
						...existing,
						state: toolState(part.isError),
						result: part.result ?? undefined,
					};
				}
			} else {
				rendered.push({
					kind: "tool",
					id,
					sequence,
					name: part.toolName || "tool",
					state: toolState(part.isError),
					result: part.result ?? undefined,
				});
			}
		}
	}
	return rendered.sort((left, right) => left.sequence - right.sequence);
}

export const isPrimaryActive = (status: BenchmarkPrimaryStatus): boolean =>
	status === "Queued" || status === "Running" || status === "CancelRequested";
export const isJudgeActive = (status: BenchmarkJudgeStatus): boolean => status === "Queued" || status === "Running";
const isPrimaryTerminal = (status: BenchmarkPrimaryStatus): boolean => !isPrimaryActive(status);
export const isRunTerminal = (run: BenchmarkRunSummary): boolean =>
	isPrimaryTerminal(run.primaryStatus) && !isJudgeActive(run.judgeStatus) && run.judgeStatus !== "Pending";
