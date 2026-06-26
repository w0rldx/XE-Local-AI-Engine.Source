// Filters onnxruntime-web's benign Warning-level console noise inside the TTS worker.
//
// ORT logs performance notes like "[W:onnxruntime:, session_state.cc:1280 VerifyEachNodeIsAssignedToAnEp] Some nodes
// were not assigned to the preferred execution providers…" every time it builds a session. They are harmless (ORT puts
// a few shape ops on CPU instead of WebGPU) but spam the console with a wasm stack on each synthesis. The clean fix —
// raising the session's `logSeverityLevel` — is unreachable: kokoro-js's `from_pretrained` does not forward
// `session_options`, and transformers defaults the level to Warning. So we drop ONLY ORT *warning* lines
// ("W:onnxruntime"); ORT *errors* ("E:onnxruntime") and all other output pass through untouched.

type ConsoleMethod = "log" | "warn" | "error";

interface ConsoleLike {
	log(...args: unknown[]): void;
	warn(...args: unknown[]): void;
	error(...args: unknown[]): void;
}

/** True when the console arguments are an onnxruntime-web Warning-level line that is safe to suppress. */
export function isBenignOrtWarning(args: readonly unknown[]): boolean {
	return typeof args[0] === "string" && args[0].includes("W:onnxruntime");
}

/**
 * Wraps a console-like target so {@link isBenignOrtWarning} messages are dropped. Returns a restore function that
 * reinstates the original methods (used by tests; the worker installs it for its lifetime).
 */
export function installOrtWarningFilter(target: ConsoleLike): () => void {
	const methods: ConsoleMethod[] = ["log", "warn", "error"];
	// Keep the original reference (for an exact restore) AND a bound copy (so invocation keeps the right `this`).
	const saved = methods.map((method) => {
		const original = target[method];
		return { method, original, bound: original.bind(target) };
	});

	for (const { method, bound } of saved) {
		target[method] = (...args: unknown[]): void => {
			if (isBenignOrtWarning(args)) {
				return;
			}
			bound(...args);
		};
	}

	return () => {
		for (const { method, original } of saved) {
			target[method] = original;
		}
	};
}
