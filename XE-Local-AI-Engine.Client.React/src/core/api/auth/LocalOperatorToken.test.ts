import { afterEach, describe, expect, it } from "vitest";

import { getLocalOperatorToken } from "@/core/api/auth/LocalOperatorToken";

const localOperatorTokenGlobalKey = "__XE_LOCAL_OPERATOR_TOKEN__";
const localGlobal = globalThis as unknown as Partial<Record<typeof localOperatorTokenGlobalKey, string>>;

describe("getLocalOperatorToken", () => {
	afterEach(() => {
		delete localGlobal[localOperatorTokenGlobalKey];
	});

	it("returns the injected local operator token", () => {
		localGlobal[localOperatorTokenGlobalKey] = " token-value ";

		expect(getLocalOperatorToken()).toBe("token-value");
	});

	it("ignores the unreplaced token sentinel", () => {
		localGlobal[localOperatorTokenGlobalKey] = "%XE_LOCAL_OPERATOR_TOKEN%";

		expect(getLocalOperatorToken()).toBeUndefined();
	});
});
