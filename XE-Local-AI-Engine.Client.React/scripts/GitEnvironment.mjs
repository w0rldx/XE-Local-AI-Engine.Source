import { runChecked } from "./RunPackageTool.mjs";
import { devNull } from "node:os";

// The variables scripts/lib/git-env.sh lists: inherited, any one of them points git at another repository, work tree,
// index or object store than its working directory's, so a caller's private GIT_INDEX_FILE would be read and rewritten.
export const REPOSITORY_SELECTORS = [
	"GIT_DIR",
	"GIT_WORK_TREE",
	"GIT_INDEX_FILE",
	"GIT_COMMON_DIR",
	"GIT_OBJECT_DIRECTORY",
	"GIT_ALTERNATE_OBJECT_DIRECTORIES",
	"GIT_NAMESPACE",
];
// Inherited, GIT_CONFIG is the file `git config` writes and the others inject settings (GIT_CONFIG_COUNT gates its
// GIT_CONFIG_KEY_n/GIT_CONFIG_VALUE_n pairs; GIT_TEMPLATE_DIR outranks init.templateDir); git-env.sh, git_env.py and
// TestGitEnvironment drop the same ones.
export const CONFIG_OVERRIDES = ["GIT_CONFIG", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT", "GIT_TEMPLATE_DIR"];
// Command-level settings outrank every config file, so no hook, signing agent or file monitor a config names runs, and
// no global ignore or attributes file (configured or the XDG default) or init template can refuse or rewrite a fixture
// file. Every isolation helper sets the same keys (GitEnvironment.test.mjs).
export const SETTINGS = [
	["core.hooksPath", devNull],
	["commit.gpgSign", "false"],
	["tag.gpgSign", "false"],
	["core.excludesFile", devNull],
	["core.attributesFile", devNull],
	["core.fsmonitor", "false"],
	["init.templateDir", ""],
];
const DROPPED = new Set([...REPOSITORY_SELECTORS, ...CONFIG_OVERRIDES]);

// The environment every git child of the frontend tooling gets: git sees only its working directory's repository.
// Names compare uppercased, as Windows treats them.
export function gitEnvironment(environment = process.env) {
	const result = Object.fromEntries(Object.entries(environment).filter(([name]) => !DROPPED.has(name.toUpperCase())));
	result.GIT_CONFIG_COUNT = String(SETTINGS.length);
	for (const [index, [key, value]] of SETTINGS.entries()) {
		result[`GIT_CONFIG_KEY_${index}`] = key;
		result[`GIT_CONFIG_VALUE_${index}`] = value;
	}
	return result;
}

// options.env, when given, is the environment to isolate instead of this process's.
export function runGit(args, options = {}) {
	return runChecked("git", args, { ...options, env: gitEnvironment(options.env) });
}
