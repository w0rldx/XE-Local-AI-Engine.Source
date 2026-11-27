import { CONFIG_OVERRIDES, gitEnvironment, REPOSITORY_SELECTORS, runGit, SETTINGS } from "./GitEnvironment.mjs";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { devNull, tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

const repositoryFile = (path) => readFileSync(new URL(`../../${path}`, import.meta.url), "utf8");
const listed = (text, pattern) => {
	const match = pattern.exec(text);
	assert.ok(match, `${pattern} no longer matches`);
	return match[1].match(/GIT_[A-Z_]+/g).sort();
};

test("the dropped lists are the ones the scripts and the .NET test fixtures drop", () => {
	assert.deepEqual(
		listed(repositoryFile("scripts/lib/git-env.sh"), /XE_GIT_REPO_ENV_VARS=\(([^)]*)\)/),
		[...REPOSITORY_SELECTORS].sort(),
	);
	assert.deepEqual(
		listed(repositoryFile("scripts/lib/git-env.sh"), /XE_GIT_CONFIG_ENV_VARS=\(([^)]*)\)/),
		[...CONFIG_OVERRIDES].sort(),
	);
	assert.deepEqual(
		listed(
			repositoryFile("XE-Local-AI-Engine.Client.Testing/Git/TestGitEnvironment.cs"),
			/ConfigOverrides \{ get; \} =\s*\[([^\]]*)\]/,
		),
		[...CONFIG_OVERRIDES].sort(),
	);
});

test("every git isolation helper sets the same command-level settings", () => {
	for (const path of ["XE-Local-AI-Engine.Client.Testing/Git/TestGitEnvironment.cs", "scripts/lib/git_env.py"]) {
		const text = repositoryFile(path);
		assert.deepEqual(
			SETTINGS.map(([key]) => key).filter((key) => !text.includes(`"${key}"`)),
			[],
			`${path} lacks these settings`,
		);
		assert.ok(text.includes('"GIT_TEMPLATE_DIR"'), `${path} keeps GIT_TEMPLATE_DIR, which outranks init.templateDir`);
	}
});

test("a global ignore or attributes file and an init template never refuse a fixture file", () => {
	const root = mkdtempSync(join(tmpdir(), "git-environment-"));
	try {
		const operator = join(root, "operator");
		const fixture = join(root, "fixture");
		mkdirSync(join(operator, "template", "info"), { recursive: true });
		mkdirSync(join(operator, "xdg", "git"), { recursive: true });
		mkdirSync(fixture);
		writeFileSync(join(operator, "ignore"), "*.txt\n");
		writeFileSync(join(operator, "xdg", "git", "ignore"), "*.txt\n");
		writeFileSync(join(operator, "attributes"), "*.txt filter=missing\n");
		writeFileSync(join(operator, "template", "info", "exclude"), "*.txt\n");
		const config = join(operator, "gitconfig");
		const forward = (path) => path.replaceAll("\\", "/");
		writeFileSync(
			config,
			`[core]\n\texcludesFile = ${forward(join(operator, "ignore"))}\n\tattributesFile = ${forward(join(operator, "attributes"))}\n` +
				'[filter "missing"]\n\trequired = true\n',
		);
		const env = {
			...process.env,
			GIT_CONFIG_GLOBAL: config,
			GIT_TEMPLATE_DIR: join(operator, "template"),
			XDG_CONFIG_HOME: join(operator, "xdg"),
		};
		writeFileSync(join(fixture, "fixture.txt"), "fixture\n");
		runGit(["init", "-q"], { cwd: fixture, env });
		runGit(["add", "fixture.txt"], { cwd: fixture, env });
		runGit(["add", "-A"], { cwd: fixture, env });

		assert.equal(runGit(["ls-files"], { cwd: fixture, env }).stdout, "fixture.txt\n");
	} finally {
		rmSync(root, { recursive: true, force: true });
	}
});

test("git children lose every selector and injected setting and run no hook or signing agent", () => {
	const inherited = Object.fromEntries([...REPOSITORY_SELECTORS, ...CONFIG_OVERRIDES].map((name) => [name, "/caller"]));
	const environment = gitEnvironment({
		...inherited,
		git_index_file: "/caller/index",
		GIT_CONFIG_KEY_0: "core.hooksPath",
		GIT_CONFIG_VALUE_0: "/caller/hooks",
		GIT_CONFIG_KEY_7: "core.worktree",
		GIT_CEILING_DIRECTORIES: "/bound",
		PATH: "/bin",
	});
	assert.deepEqual(environment, {
		GIT_CEILING_DIRECTORIES: "/bound",
		PATH: "/bin",
		GIT_CONFIG_COUNT: "7",
		GIT_CONFIG_KEY_0: "core.hooksPath",
		GIT_CONFIG_VALUE_0: devNull,
		GIT_CONFIG_KEY_1: "commit.gpgSign",
		GIT_CONFIG_VALUE_1: "false",
		GIT_CONFIG_KEY_2: "tag.gpgSign",
		GIT_CONFIG_VALUE_2: "false",
		GIT_CONFIG_KEY_3: "core.excludesFile",
		GIT_CONFIG_VALUE_3: devNull,
		GIT_CONFIG_KEY_4: "core.attributesFile",
		GIT_CONFIG_VALUE_4: devNull,
		GIT_CONFIG_KEY_5: "core.fsmonitor",
		GIT_CONFIG_VALUE_5: "false",
		GIT_CONFIG_KEY_6: "init.templateDir",
		GIT_CONFIG_VALUE_6: "",
		// Past GIT_CONFIG_COUNT, git never reads it.
		GIT_CONFIG_KEY_7: "core.worktree",
	});
});
