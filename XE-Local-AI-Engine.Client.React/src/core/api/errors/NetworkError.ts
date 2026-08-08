/**
 * Thrown by the shared axios ProblemDetails interceptor when a request never reached the node at all (axios
 * `ERR_NETWORK`: the node is down, the port moved, or the machine went to sleep). There is no response, so there is no
 * server-authored message and no status code to reason about.
 *
 * A distinct type rather than a plain Error carrying a literal string: the message an operator sees has to be
 * localized, and it has to be localized at RENDER time (i18next may not have settled on the user's language when a
 * boot-time request fails). {@link apiErrorMessage} resolves it, so every caller that goes through the canonical helper
 * gets the same actionable sentence in the current language. The `message` is deliberately left empty — anything that
 * renders `error.message` directly must fall through to its own localized fallback rather than print English here.
 */
export class NetworkError extends Error {
	constructor() {
		super();
		this.name = "NetworkError";
	}
}
