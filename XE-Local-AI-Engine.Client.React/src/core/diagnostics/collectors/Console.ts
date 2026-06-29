// console.warn/error patch → breadcrumb (plan §7.2, §10).
//
// Recursion-guarded (a redaction error must not re-enter the patched console). Redaction runs on the
// interpolated args BEFORE `push`, since console messages frequently contain message bodies.

import { push } from "@/core/diagnostics/BreadcrumbBuffer";
import { redactConsoleArgs, redactString } from "@/core/diagnostics/Redact";

type ConsoleMethod = "warn" | "error";
type ConsoleFn = (...args: unknown[]) => void;

let installed = false;
let inHandler = false;

/** Patch console.warn/error to also emit redacted breadcrumbs. Returns a teardown that restores them. */
export function installConsoleCollector(): () => void {
	if (installed) {
		return () => undefined;
	}
	installed = true;

	const original: Record<ConsoleMethod, ConsoleFn> = {
		warn: console.warn.bind(console),
		error: console.error.bind(console),
	};

	const patch = (level: ConsoleMethod): ConsoleFn => {
		return (...args: unknown[]) => {
			original[level](...args);
			if (inHandler) {
				return;
			}
			inHandler = true;
			try {
				push({
					category: "console",
					level,
					message: redactString(args.map(stringifyArg).join(" ")),
					args: redactConsoleArgs(args),
				});
			} catch {
				// Never let breadcrumb capture break the app's own logging.
			} finally {
				inHandler = false;
			}
		};
	};

	console.warn = patch("warn");
	console.error = patch("error");

	return () => {
		console.warn = original.warn;
		console.error = original.error;
		installed = false;
	};
}

function stringifyArg(arg: unknown): string {
	if (typeof arg === "string") {
		return arg;
	}
	if (arg instanceof Error) {
		return arg.message;
	}
	if (arg === null || arg === undefined) {
		return String(arg);
	}
	if (typeof arg === "object") {
		return "[object]";
	}
	return String(arg);
}
