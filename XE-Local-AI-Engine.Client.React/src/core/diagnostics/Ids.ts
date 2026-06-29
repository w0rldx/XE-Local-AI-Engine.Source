// Small id helpers shared by the diagnostics buffer and trace context.

/** Random UUID with a manual fallback for environments lacking `crypto.randomUUID`. */
export function generateId(): string {
	const cryptoApi = globalThis.crypto;
	if (cryptoApi?.randomUUID) {
		return cryptoApi.randomUUID();
	}
	return `${Date.now().toString(16)}-${Math.random().toString(16).slice(2, 10)}`;
}

/** Generate `length` random lowercase hex characters using a CSPRNG when available. */
export function randomHex(length: number): string {
	const byteCount = Math.ceil(length / 2);
	const bytes = new Uint8Array(byteCount);
	const cryptoApi = globalThis.crypto;
	if (cryptoApi?.getRandomValues) {
		cryptoApi.getRandomValues(bytes);
	} else {
		for (let index = 0; index < byteCount; index += 1) {
			bytes[index] = Math.floor(Math.random() * 256);
		}
	}
	let hex = "";
	for (const byte of bytes) {
		hex += byte.toString(16).padStart(2, "0");
	}
	return hex.slice(0, length);
}
