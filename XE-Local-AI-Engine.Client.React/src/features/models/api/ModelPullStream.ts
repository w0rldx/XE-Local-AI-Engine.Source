import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

// HAND-WIRED STREAM (off the generated hey-api SDK, per plan §7.2 / §8): the generated client is REST
// request/response only, so the streaming pull is consumed directly here — mirroring the chat-stream / runtime
// log-stream survivors. The endpoint is Operator-gated and validates the model name server-side; the events it
// emits are sanitized to ONLY `{status, completedBytes, totalBytes}` (plan invariant §3.2) — never paths, tokens,
// or raw Ollama payloads.
//
// Transport: POST `models/pull/stream` (= /api/local/v1/models/pull/stream, LocalApiRoutes.LocalModels.PullStream)
// returning `application/x-ndjson` — one sanitized JSON object per newline-delimited line, scoped to the requesting
// client (no broadcast). This matches the Lane B contract verified 2026-06-03 against PullStreamLocalModelEndpoint;
// the route + body shape are centralized in this one function so the hook (useModelPull) stays transport-agnostic.

const pullStreamPath = "models/pull/stream";

// Sanitized progress event — the wire shape every consumer sees. Fields are defensively optional because the
// payload is untrusted streamed JSON; the hook coalesces them when deriving a percent.
export interface ModelPullProgressEvent {
	status: string;
	completedBytes?: number;
	totalBytes?: number;
	// Present only on the terminal failure line (status === "error"): a short, sanitized reason from the backend.
	error?: string;
}

// Narrows one parsed NDJSON line to a ModelPullProgressEvent, dropping anything that is not an object with a string
// status. Unknown / extra fields are ignored so a backend that adds a field never breaks the client.
function toProgressEvent(parsed: unknown): ModelPullProgressEvent | undefined {
	if (typeof parsed !== "object" || parsed === null) {
		return undefined;
	}
	const candidate = parsed as Record<string, unknown>;
	const rawStatus = candidate["status"];
	if (typeof rawStatus !== "string") {
		return undefined;
	}
	const rawCompleted = candidate["completedBytes"];
	const rawTotal = candidate["totalBytes"];
	const rawError = candidate["error"];
	return {
		status: rawStatus,
		completedBytes: typeof rawCompleted === "number" ? rawCompleted : undefined,
		totalBytes: typeof rawTotal === "number" ? rawTotal : undefined,
		error: typeof rawError === "string" ? rawError : undefined,
	};
}

// Streams pull progress for one model as an async iterable of sanitized events. The caller passes an AbortSignal
// (the hook aborts on unmount / a superseding pull) which cancels the in-flight fetch. Throws on a non-OK response
// or a network failure so the hook can surface an error toast.
export async function* streamModelPull(modelName: string, signal: AbortSignal): AsyncIterable<ModelPullProgressEvent> {
	// Attach the SAME bearer the shared local-API axios request interceptor uses (addAuthRequestInterceptor:
	// useNodeAuthStore.getState().accessToken), and — like that interceptor — only when a token exists, so this
	// hand-wired fetch authenticates against the Operator gate identically to every generated local API call.
	const headers: Record<string, string> = {
		"Content-Type": "application/json",
		Accept: "application/x-ndjson",
	};
	const accessToken = useNodeAuthStore.getState().accessToken;
	if (accessToken) {
		headers["Authorization"] = `Bearer ${accessToken}`;
	}

	const response = await fetch(buildLocalApiUrl(pullStreamPath), {
		method: "POST",
		headers,
		body: JSON.stringify({ modelName }),
		signal,
	});

	if (!response.ok || response.body === null) {
		throw new Error(`Pull stream request failed (${response.status}).`);
	}

	const reader = response.body.getReader();
	const decoder = new TextDecoder();
	let buffer = "";

	try {
		while (true) {
			// biome-ignore lint/performance/noAwaitInLoops: reading a stream is inherently sequential — each chunk must be awaited before the next.
			const { value, done } = await reader.read();
			if (done) {
				break;
			}
			buffer += decoder.decode(value, { stream: true });

			// NDJSON: emit each complete line, keep the trailing partial line in the buffer.
			let newlineIndex = buffer.indexOf("\n");
			while (newlineIndex !== -1) {
				const line = buffer.slice(0, newlineIndex).trim();
				buffer = buffer.slice(newlineIndex + 1);
				if (line.length > 0) {
					const event = toProgressEvent(safeParse(line));
					if (event !== undefined) {
						yield event;
					}
				}
				newlineIndex = buffer.indexOf("\n");
			}
		}

		// Flush any final line that was not newline-terminated.
		const tail = buffer.trim();
		if (tail.length > 0) {
			const event = toProgressEvent(safeParse(tail));
			if (event !== undefined) {
				yield event;
			}
		}
	} finally {
		reader.releaseLock();
	}
}

// Parses one NDJSON line, returning undefined on malformed JSON so a single bad line never aborts the stream.
function safeParse(line: string): unknown {
	try {
		return JSON.parse(line);
	} catch {
		return undefined;
	}
}
