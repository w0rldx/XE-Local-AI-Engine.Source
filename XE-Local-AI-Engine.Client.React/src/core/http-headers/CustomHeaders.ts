// One editable custom-header row. Secret values are write-only: the backend never returns them, so `value` is blank on
// load for a secret row. `hasStoredSecret` is true only when the STORED row is secret and has a value: it drives the
// "stored" hint, the keep-blank rule and the no-resurrection guard. A plain stored row turned secret has nothing to keep.
export interface CustomHeaderDraft {
	name: string;
	value: string;
	isSecret: boolean;
	hasStoredSecret: boolean;
}

// The first problem found in the rows. `index` names the offending row; it is absent for a list-level problem (the cap).
export interface CustomHeaderError {
	readonly message: string;
	readonly index?: number;
}

// The wire shape both the Azure and the external-connection requests accept for a header row.
export interface CustomHeaderRequest {
	name: string;
	isSecret: boolean;
	value?: string;
}

export const emptyCustomHeader: CustomHeaderDraft = { name: "", value: "", isSecret: false, hasStoredSecret: false };

// Reserved header names (lower-cased for case-insensitive compare) that must never be operator-set: they would override
// credentials or transport framing, or are content headers the .NET request-header collection refuses. Mirrors the
// backend reserved set (CustomHeaderRules).
const RESERVED_HEADER_NAMES = new Set<string>([
	"api-key",
	"authorization",
	"host",
	"content-type",
	"content-length",
	"content-encoding",
	"cookie",
	"proxy-authorization",
	"transfer-encoding",
	"connection",
	"expect",
	"allow",
	"content-disposition",
	"content-language",
	"content-location",
	"content-md5",
	"content-range",
	"expires",
	"last-modified",
]);

// RFC 7230 header-name token charset.
const HEADER_NAME_TOKEN = /^[A-Za-z0-9!#$%&'*+\-.^_`|~]+$/;

const MAX_HEADERS = 32;
const MAX_HEADER_NAME_LENGTH = 128;
const MAX_HEADER_VALUE_LENGTH = 4096;

// RFC 7230 field-value guard: reject CR/LF/NUL and any control char except HTAB (0x09), plus DEL (0x7F). Implemented
// char-by-char to avoid a control-char regex literal (biome lint).
function hasControlCharacter(value: string): boolean {
	for (let index = 0; index < value.length; index += 1) {
		const code = value.charCodeAt(index);
		if (code === 0x09) {
			continue;
		}
		if (code <= 0x1f || code === 0x7f) {
			return true;
		}
	}
	return false;
}

// The transport refuses header values outside ASCII.
function hasNonAsciiCharacter(value: string): boolean {
	for (let index = 0; index < value.length; index += 1) {
		if (value.charCodeAt(index) >= 0x80) {
			return true;
		}
	}
	return false;
}

function validateRow(header: CustomHeaderDraft, seenNames: Set<string>): string | undefined {
	const name = header.name.trim();
	const hasValue = header.value.trim().length > 0;

	if (name.length === 0) {
		return hasValue ? "Enter a header name for every row that has a value." : undefined;
	}
	if (name.length > MAX_HEADER_NAME_LENGTH) {
		return `Header name "${name}" is too long (max ${MAX_HEADER_NAME_LENGTH} characters).`;
	}
	if (!HEADER_NAME_TOKEN.test(name)) {
		return `Header name "${name}" contains characters that are not allowed in an HTTP header name.`;
	}
	const nameKey = name.toLowerCase();
	if (RESERVED_HEADER_NAMES.has(nameKey)) {
		return `Header name "${name}" is reserved and cannot be set.`;
	}
	if (seenNames.has(nameKey)) {
		return `Header name "${name}" is duplicated.`;
	}
	seenNames.add(nameKey);

	if (header.value.length > MAX_HEADER_VALUE_LENGTH) {
		return `Value for header "${name}" is too long (max ${MAX_HEADER_VALUE_LENGTH} characters).`;
	}
	if (hasControlCharacter(header.value)) {
		return `Value for header "${name}" contains a line break or control character.`;
	}
	if (hasNonAsciiCharacter(header.value)) {
		return `Value for header "${name}" contains a non-ASCII character; header values must be plain ASCII.`;
	}
	// Secret rows keep the stored value only while both secret and blank. Turning "Secret" off on a stored-secret row
	// with a blank value must not silently reuse the stored value.
	if (!header.isSecret && header.hasStoredSecret && !hasValue) {
		return `Enter a new value for header "${name}" before saving — the stored secret is not reused once "Secret" is turned off.`;
	}
	// A secret row with no stored value must carry a fresh value to resolve to anything.
	if (header.isSecret && !header.hasStoredSecret && !hasValue) {
		return `Enter a value for the secret header "${name}".`;
	}
	return undefined;
}

// Validates the custom-header rows, returning the first problem (mirrors the backend guards so the operator sees an
// inline error before save). Blank-name rows are dropped, so only a blank name that carries a value is an error.
export function validateHeaders(headers: readonly CustomHeaderDraft[]): CustomHeaderError | undefined {
	if (headers.length > MAX_HEADERS) {
		return { message: `Remove some headers — at most ${MAX_HEADERS} custom headers are allowed.` };
	}
	const seenNames = new Set<string>();
	for (const [index, header] of headers.entries()) {
		const message = validateRow(header, seenNames);
		if (message !== undefined) {
			return { message, index };
		}
	}
	return undefined;
}

// True for a secret row left blank over a stored value: the request omits its value and the backend keeps the stored one.
export function keepsStoredSecret(header: CustomHeaderDraft): boolean {
	return header.isSecret && header.hasStoredSecret && header.value.trim().length === 0;
}

// Maps rows to the request. Blank-name rows are dropped; a blank secret value is omitted, which the backend reads as
// "keep the stored value" (and refuses when nothing is stored, or when the endpoint moved to another origin).
export function toHeaderRequests(headers: readonly CustomHeaderDraft[]): CustomHeaderRequest[] {
	return headers.flatMap((header) => {
		const name = header.name.trim();
		if (name.length === 0) {
			return [];
		}
		const omitValue = header.isSecret && header.value.trim().length === 0;
		return [{ name, isSecret: header.isSecret, ...(omitValue ? {} : { value: header.value }) }];
	});
}
