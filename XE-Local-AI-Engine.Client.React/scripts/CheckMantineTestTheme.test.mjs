import { checkMantineTestTheme, findMantineProviders, findUnthemedTestProviders } from "./CheckMantineTestTheme.mjs";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";

function scratchTree(files) {
	const root = mkdtempSync(join(tmpdir(), "mantine-theme-guard-"));
	for (const [name, contents] of Object.entries(files)) {
		const file = join(root, name);
		mkdirSync(resolve(file, ".."), { recursive: true });
		writeFileSync(file, contents, "utf8");
	}
	return root;
}

test("a themed provider passes and an unthemed one is reported with its line and tag", () => {
	const themed = `<MantineProvider env="test" theme={testMantineTheme}>{ui}</MantineProvider>`;
	assert.deepEqual(findUnthemedTestProviders(themed), []);

	const unthemed = `const a = 1;\nrender(<MantineProvider env="test">{ui}</MantineProvider>);`;
	assert.deepEqual(findUnthemedTestProviders(unthemed), [
		{ line: 2, tag: `<MantineProvider env="test">`, isTestEnv: true, hasTheme: false },
	]);
});

test("the whole opening tag is read, so a multi-line provider and a `>` inside an attribute do not fool it", () => {
	const source = `<MantineProvider\n\tenv="test"\n\ttheme={testMantineTheme}\n>`;
	assert.deepEqual(findUnthemedTestProviders(source), []);

	// The arrow and the string both contain a `>`; ending the tag there would hide the missing theme.
	const tricky = `<MantineProvider env="test" onError={(e) => log(e)} aria-label="a > b">`;
	assert.equal(findUnthemedTestProviders(tricky).length, 1);
});

test('a provider without env="test" is not this rule\'s business', () => {
	// The two product providers pass no env at all; they must never be dragged into a test-only convention.
	const product = `<MantineProvider theme={appTheme} defaultColorScheme="auto">{children}</MantineProvider>`;
	assert.deepEqual(findMantineProviders(product), [
		{ line: 1, tag: product.slice(0, product.indexOf(">") + 1), isTestEnv: false, hasTheme: false },
	]);
	assert.deepEqual(findUnthemedTestProviders(product), []);
});

test("a differently named component starting with the same text is not a MantineProvider", () => {
	assert.deepEqual(findMantineProviders(`<MantineProviderStub env="test">`), []);
});

test("a scan that reaches no file or no provider reports zero, so the caller can refuse to pass for free", () => {
	const empty = checkMantineTestTheme({ sourceRoot: scratchTree({ "notes.md": "no tsx here" }) });
	assert.equal(empty.files, 0);
	assert.equal(empty.providers, 0);

	const noProviders = checkMantineTestTheme({ sourceRoot: scratchTree({ "Thing.test.tsx": "render(<Thing />);" }) });
	assert.equal(noProviders.files, 1);
	assert.equal(noProviders.providers, 0);
});

test("only test files and the shared src/test wrappers are scanned", () => {
	const root = scratchTree({
		"features/Thing.test.tsx": `render(<MantineProvider env="test">{ui}</MantineProvider>);`,
		"test/RenderWithProviders.tsx": `<MantineProvider env="test">{children}</MantineProvider>`,
		"features/Thing.tsx": `<MantineProvider env="test">{ui}</MantineProvider>`,
	});
	const { files, providers, offenders } = checkMantineTestTheme({ sourceRoot: root, root });
	assert.equal(files, 2);
	assert.equal(providers, 2);
	assert.deepEqual(
		offenders.map((entry) => entry.split(":")[0].replaceAll("\\", "/")),
		["features/Thing.test.tsx", "test/RenderWithProviders.tsx"],
	);
});

test("the real src tree passes, and the scan reaches the hundred-plus live call sites", () => {
	const { files, providers, offenders } = checkMantineTestTheme();
	assert.deepEqual(offenders, []);
	assert.ok(files > 200, `expected the scan to reach the test tree, it saw ${files} files`);
	assert.ok(providers > 100, `expected the hand-rolled providers to be scanned, it saw ${providers}`);
});
