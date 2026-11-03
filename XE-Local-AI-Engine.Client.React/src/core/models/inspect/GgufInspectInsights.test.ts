import { describe, expect, it } from "vitest";

import { blockKind, hasExperts, tensorTypeRows } from "@/core/models/inspect/GgufInspectInsights";

function tensor(name: string, ggmlType: string | null = "Q4_K", ggmlTypeId = 12, byteSize: number | null = 100) {
	return { name, shape: [256], ggmlType, ggmlTypeId, offset: 0, byteSize };
}

function block(index: number, ...suffixes: string[]) {
	return suffixes.map((suffix) => tensor(`blk.${index}.${suffix}.weight`));
}

describe("blockKind", () => {
	it("tells the blocks of a 27B-shaped list apart", () => {
		expect(blockKind(block(0, "ssm_a", "ssm_alpha", "attn_qkv", "attn_gate"))).toBe("linearAttention");
		expect(blockKind(block(3, "attn_q", "attn_k", "attn_v", "attn_q_norm", "attn_output"))).toBe("fullAttention");
		expect(blockKind(block(64, "attn_q", "attn_k", "nextn.eh_proj", "nextn.enorm"))).toBe("mtp");
	});

	it("keeps the attention layout of a hybrid list apart from its experts", () => {
		const linear = block(0, "ssm_a", "attn_qkv", "ffn_up_exps");
		const full = block(1, "attn_q", "ffn_gate_inp", "ffn_down_exps");
		const dense = block(2, "attn_q", "ffn_up");

		expect([blockKind(linear), hasExperts(linear)]).toEqual(["linearAttention", true]);
		expect([blockKind(full), hasExperts(full)]).toEqual(["fullAttention", true]);
		expect([blockKind(dense), hasExperts(dense)]).toEqual(["fullAttention", false]);
	});

	it("reads a block with experts and no attention tensors as other plus expert", () => {
		const experts = block(3, "ffn_gate_exps", "ffn_up_exps");

		expect([blockKind(experts), hasExperts(experts)]).toEqual(["other", true]);
	});

	it("reads fused attention as full attention, and a lone norm as neither", () => {
		expect(blockKind(block(2, "attn_qkv", "attn_q_norm", "ffn_up"))).toBe("fullAttention");
		expect(blockKind(block(2, "attn_q_norm", "ffn_up"))).toBe("other");
		expect(blockKind([])).toBe("other");
	});
});

describe("tensorTypeRows", () => {
	it("sums per type, largest first, with each share of the known bytes", () => {
		const rows = tensorTypeRows([tensor("a", "F32", 0, 100), tensor("b", "Q4_K", 12, 200), tensor("c", "Q4_K", 12, 100)]);

		expect(rows.map((row) => [row.ggmlType, row.tensorCount, row.bytes, row.percent])).toEqual([
			["Q4_K", 2, 300, 75],
			["F32", 1, 100, 25],
		]);
	});

	it("keeps an unknown type under its id and counts sizes it cannot sum", () => {
		const rows = tensorTypeRows([
			tensor("known", "F32", 0, 100),
			tensor("ragged", "F32", 0, null),
			tensor("future", null, 99, null),
		]);

		expect(rows).toEqual([
			{ ggmlType: "F32", ggmlTypeId: 0, tensorCount: 2, bytes: 100, unknownSizeCount: 1, percent: 100 },
			{ ggmlType: null, ggmlTypeId: 99, tensorCount: 1, bytes: 0, unknownSizeCount: 1, percent: 0 },
		]);
	});

	it("has no rows for an empty list and no division by zero when no size is known", () => {
		expect(tensorTypeRows([])).toEqual([]);
		expect(tensorTypeRows([tensor("future", null, 99, null)]).map((row) => row.percent)).toEqual([0]);
	});
});
