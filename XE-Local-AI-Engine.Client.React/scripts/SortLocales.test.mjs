import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import { sortLocales, sortLocaleText } from "./SortLocales.mjs";

const unsorted =
	'{\n\t"b": {\n\t\t"z": "1",\n\t\t"a": ["x", {"d": 1, "c": 2}]\n\t},\n\t"10": "ten",\n\t"2": "two",\n\t"a": "A"\n}\n';

test("sorts keys byte-wise at every depth and keeps values and array order", () => {
	assert.equal(
		sortLocaleText(unsorted),
		'{\n\t"10": "ten",\n\t"2": "two",\n\t"a": "A",\n\t"b": {\n\t\t"a": [\n\t\t\t"x",\n\t\t\t{\n\t\t\t\t"c": 2,\n\t\t\t\t"d": 1\n\t\t\t}\n\t\t],\n\t\t"z": "1"\n\t}\n}\n',
	);
});

test("--check names the first unsorted key and writes nothing; the sort then makes it pass", (context) => {
	const directory = mkdtempSync(join(tmpdir(), "xe-locales-"));
	context.after(() => rmSync(directory, { recursive: true, force: true }));
	const path = join(directory, "en.json");
	writeFileSync(path, unsorted);

	const problems = sortLocales({ directory, check: true });

	assert.equal(problems.length, 1);
	assert.match(problems[0], /en\.json: <root>: "10" belongs before "2"/);
	assert.equal(readFileSync(path, "utf8"), unsorted);

	assert.deepEqual(sortLocales({ directory }), []);
	assert.deepEqual(sortLocales({ directory, check: true }), []);
});
