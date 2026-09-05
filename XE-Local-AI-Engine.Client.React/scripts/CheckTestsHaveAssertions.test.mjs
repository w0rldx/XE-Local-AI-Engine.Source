import { checkTestsHaveAssertions, findTestsWithoutAssertions } from "./CheckTestsHaveAssertions.mjs";
import assert from "node:assert/strict";
import test from "node:test";

const names = (source) => findTestsWithoutAssertions(source).map((finding) => finding.name);

test("passes a test that asserts and flags one that only acts", () => {
	const source = `describe("suite", () => {
	it("asserts", () => {
		render(<Panel />);
		expect(screen.getByRole("button")).toBeTruthy();
	});

	it("only clicks", () => {
		render(<Panel />);
		fireEvent.click(screen.getByRole("button"));
	});
});
`;
	assert.deepEqual(findTestsWithoutAssertions(source), [{ line: 7, name: "only clicks" }]);
});

test("accepts an assertion that lives in a helper named for asserting", () => {
	const source = `it("orders the parts", () => {
	expectDocumentOrder([first, second]);
});

it("checks the payload", () => {
	assert.deepEqual(body, expected);
});

it("neither", () => {
	renderDocumentOrder([first, second]);
});
`;
	assert.deepEqual(names(source), ["neither"]);
});

test("reads the arguments of it.each and test.each past the table, in both call and template form", () => {
	const source = `it.each(["a", "b"])("accepts %s", (name) => {
	expect(pattern.test(name)).toBe(true);
});

test.each([
	{ a: 1 },
])("adds %s", ({ a }) => {
	compute(a);
});

it.each\`
	a    | b
	\${1} | \${2}
\`("tabulates", ({ a }) => {
	expect(a).toBe(1);
});
`;
	assert.deepEqual(names(source), ["adds %s"]);
});

test("does not mistake a member call named test for a test declaration", () => {
	assert.deepEqual(findTestsWithoutAssertions('const ok = SKILL_NAME_PATTERN.test(name);\n'), []);
});

test("walks nested describes and ignores skipped and todo blocks", () => {
	const source = `describe("outer", () => {
	describe("inner", () => {
		it("nested and bare", () => {
			mount();
		});
		it.skip("skipped", () => {
			mount();
		});
		it.todo("todo");
	});
});
`;
	assert.deepEqual(names(source), ["nested and bare"]);
});

test("reads neither test names nor commented-out code as assertions", () => {
	const source = `it("expects nothing despite the name", () => {
	// expect(value).toBe(1);
	const label = "expect(value).toBe(1)";
	use(label);
});
`;
	assert.deepEqual(names(source), ["expects nothing despite the name"]);
});

test("keeps brackets inside regex literals from skewing the argument scan", () => {
	const source = `it("finds the count", () => {
	const found = screen.getByText(/\\(\\d+\\)/);
	expect(found).toBeTruthy();
});

it("finds nothing", () => {
	screen.getByText(/\\(\\d+\\)/);
});
`;
	assert.deepEqual(names(source), ["finds nothing"]);
});

test("every real Vitest file in src passes the guard", () => {
	assert.ok(checkTestsHaveAssertions() > 300);
});
