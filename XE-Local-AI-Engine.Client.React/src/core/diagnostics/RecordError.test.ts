import { describe, expect, it } from "vitest";
import { z } from "zod";

import { describeError } from "@/core/diagnostics/RecordError";

describe("describeError", () => {
	it("records a fixed message and no stack for a response-shape mismatch", () => {
		const parsed = z.object({ secretField: z.string() }).safeParse({ secretField: 12345 });
		if (parsed.success) {
			throw new Error("expected the parse to fail");
		}

		const described = describeError(parsed.error);

		expect(described).toEqual({ message: "ZodError: the server returned a response in an unexpected shape" });
	});

	it("keeps the message and stack of any other Error", () => {
		const error = new Error("boom");

		expect(describeError(error)).toEqual({ message: "boom", stack: error.stack });
	});
});
