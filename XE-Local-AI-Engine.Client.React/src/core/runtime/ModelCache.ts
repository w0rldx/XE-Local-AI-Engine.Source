// Download-on-demand + integrity-verified client cache for voice model weights (plan §3, §7.2, §10, decisions 2/11).
//
// Files are fetched from a manifest-supplied URL, their SHA-256 verified against the manifest BEFORE caching (a
// tampered URL can never poison the offline-forever cache), then stored keyed by `[modelId]::[version]::[file]`. A
// version bump evicts the stale blob and re-downloads; while offline the runtime falls back to a cached blob and
// warns. Progress + error events drive the download UI (Lane C). On any failure no partial blob is ever retained.

import type { VoiceModelFile } from "./VoiceManifest";

// --- tiny typed event emitter (no DOM CustomEvent ceremony) ------------------------------------------------------

type EmitterListener<TEvent> = (event: TEvent) => void;

class Emitter<TEvent> {
	private readonly listeners = new Set<EmitterListener<TEvent>>();

	/** Subscribes a listener and returns an unsubscribe function. */
	on(listener: EmitterListener<TEvent>): () => void {
		this.listeners.add(listener);
		return () => {
			this.listeners.delete(listener);
		};
	}

	emit(event: TEvent): void {
		for (const listener of [...this.listeners]) {
			listener(event);
		}
	}
}

export interface ModelDownloadProgress {
	readonly modelId: string;
	readonly version: string;
	readonly file: string;
	readonly loaded: number;
	readonly total: number;
}

export interface ModelDownloadError {
	readonly modelId: string;
	readonly version: string;
	readonly file: string;
	readonly error: Error;
}

/** Thrown when a downloaded blob's SHA-256 does not match the manifest — the blob is rejected and never cached. */
export class ModelHashMismatchError extends Error {
	constructor(
		readonly expected: string,
		readonly actual: string,
	) {
		super(`Model file hash mismatch: expected ${expected}, got ${actual}`);
		this.name = "ModelHashMismatchError";
	}
}

// --- pluggable blob storage --------------------------------------------------------------------------------------

/** Persistent binary store abstraction; default backs onto the Cache API, falling back to IndexedDB. */
export interface BlobStore {
	get(key: string): Promise<ArrayBuffer | undefined>;
	put(key: string, data: ArrayBuffer): Promise<void>;
	delete(key: string): Promise<void>;
	keys(): Promise<string[]>;
}

const BLOB_STORE_NAME = "xe-voice-models";
// Synthetic origin used to turn a string key into a cacheable Request URL for the Cache API.
const CACHE_KEY_ORIGIN = "https://xe-voice-model.local/";

function toCacheUrl(key: string): string {
	return `${CACHE_KEY_ORIGIN}${encodeURIComponent(key)}`;
}

function fromCacheUrl(url: string): string {
	return decodeURIComponent(url.slice(CACHE_KEY_ORIGIN.length));
}

class CacheApiBlobStore implements BlobStore {
	private open(): Promise<Cache> {
		return caches.open(BLOB_STORE_NAME);
	}

	async get(key: string): Promise<ArrayBuffer | undefined> {
		const cache = await this.open();
		const response = await cache.match(toCacheUrl(key));
		return response ? response.arrayBuffer() : undefined;
	}

	async put(key: string, data: ArrayBuffer): Promise<void> {
		const cache = await this.open();
		await cache.put(toCacheUrl(key), new Response(data));
	}

	async delete(key: string): Promise<void> {
		const cache = await this.open();
		await cache.delete(toCacheUrl(key));
	}

	async keys(): Promise<string[]> {
		const cache = await this.open();
		const requests = await cache.keys();
		return requests.map((request) => fromCacheUrl(request.url));
	}
}

class IndexedDbBlobStore implements BlobStore {
	private readonly dbName = BLOB_STORE_NAME;
	private readonly storeName = "blobs";

	private openDb(): Promise<IDBDatabase> {
		return new Promise((resolve, reject) => {
			const request = indexedDB.open(this.dbName, 1);
			request.onupgradeneeded = () => request.result.createObjectStore(this.storeName);
			request.onsuccess = () => resolve(request.result);
			request.onerror = () => reject(request.error ?? new Error("Failed to open IndexedDB"));
		});
	}

	private async run<T>(mode: IDBTransactionMode, action: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
		const db = await this.openDb();
		try {
			return await new Promise<T>((resolve, reject) => {
				const request = action(db.transaction(this.storeName, mode).objectStore(this.storeName));
				request.onsuccess = () => resolve(request.result);
				request.onerror = () => reject(request.error ?? new Error("IndexedDB request failed"));
			});
		} finally {
			db.close();
		}
	}

	async get(key: string): Promise<ArrayBuffer | undefined> {
		const value = await this.run<ArrayBuffer | undefined>("readonly", (store) => store.get(key));
		return value ?? undefined;
	}

	async put(key: string, data: ArrayBuffer): Promise<void> {
		await this.run("readwrite", (store) => store.put(data, key));
	}

	async delete(key: string): Promise<void> {
		await this.run("readwrite", (store) => store.delete(key));
	}

	async keys(): Promise<string[]> {
		const keys = await this.run<IDBValidKey[]>("readonly", (store) => store.getAllKeys());
		return keys.map(String);
	}
}

/** Picks the Cache API when available (faster, larger quota), else IndexedDB. */
export function createDefaultBlobStore(): BlobStore {
	if (typeof caches !== "undefined") {
		return new CacheApiBlobStore();
	}

	return new IndexedDbBlobStore();
}

// --- hashing + download ------------------------------------------------------------------------------------------

async function sha256Hex(data: ArrayBuffer): Promise<string> {
	const digest = await crypto.subtle.digest("SHA-256", data);
	return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

function concatChunks(chunks: readonly Uint8Array[], totalLength: number): ArrayBuffer {
	const merged = new Uint8Array(totalLength);
	let offset = 0;
	for (const chunk of chunks) {
		merged.set(chunk, offset);
		offset += chunk.length;
	}

	return merged.buffer;
}

// --- cache key helpers -------------------------------------------------------------------------------------------

const KEY_SEPARATOR = "::";

function makeKey(modelId: string, version: string, file: string): string {
	return `${modelId}${KEY_SEPARATOR}${version}${KEY_SEPARATOR}${file}`;
}

interface ParsedKey {
	readonly modelId: string;
	readonly version: string;
	readonly file: string;
}

function parseKey(key: string): ParsedKey | undefined {
	const parts = key.split(KEY_SEPARATOR);
	if (parts.length !== 3) {
		return undefined;
	}

	const [modelId, version, file] = parts as [string, string, string];
	return { modelId, version, file };
}

export class ModelCache {
	readonly onProgress = new Emitter<ModelDownloadProgress>();
	readonly onError = new Emitter<ModelDownloadError>();

	constructor(private readonly store: BlobStore = createDefaultBlobStore()) {}

	/**
	 * Returns the model file bytes, downloading + verifying + caching on first use. A cached current-version blob is
	 * returned immediately. On a version bump the older blob is evicted after a successful re-download. On a hash
	 * mismatch the blob is rejected (never cached). On a network failure a stale cached copy is returned with a
	 * warning when one exists; otherwise the (never-stored) partial is cleared and the error rethrown.
	 */
	async getModelFile(
		file: VoiceModelFile,
		modelId: string,
		version: string,
		options?: { readonly signal?: AbortSignal },
	): Promise<ArrayBuffer> {
		const key = makeKey(modelId, version, file.file);

		const cached = await this.store.get(key);
		if (cached) {
			return cached;
		}

		try {
			const data = await this.download(file, modelId, version, options?.signal);
			const actualHash = await sha256Hex(data);
			if (actualHash !== file.sha256.toLowerCase()) {
				throw new ModelHashMismatchError(file.sha256.toLowerCase(), actualHash);
			}

			await this.store.put(key, data);
			await this.evictOtherVersions(modelId, version);
			return data;
		} catch (error) {
			const normalized = error instanceof Error ? error : new Error(String(error));
			this.onError.emit({ modelId, version, file: file.file, error: normalized });

			if (!(normalized instanceof ModelHashMismatchError)) {
				const stale = await this.findStaleFallback(modelId, version, file.file);
				if (stale) {
					console.warn(`Voice model ${modelId}/${file.file} download failed; using cached copy.`);
					return stale;
				}
			}

			// Belt-and-suspenders: a partial/poisoned blob is never stored, but clear the key just in case.
			await this.store.delete(key);
			throw normalized;
		}
	}

	/** Evicts every cached blob for `modelId` whose version differs from `keepVersion`. */
	async evictOtherVersions(modelId: string, keepVersion: string): Promise<void> {
		const keys = await this.store.keys();
		const stale = keys.filter((key) => {
			const parsed = parseKey(key);
			return parsed?.modelId === modelId && parsed.version !== keepVersion;
		});

		await Promise.all(stale.map((key) => this.store.delete(key)));
	}

	/** Removes every cached voice model blob. */
	async clear(): Promise<void> {
		const keys = await this.store.keys();
		await Promise.all(keys.map((key) => this.store.delete(key)));
	}

	private async findStaleFallback(modelId: string, version: string, file: string): Promise<ArrayBuffer | undefined> {
		const keys = await this.store.keys();
		const fallbackKey = keys.find((key) => {
			const parsed = parseKey(key);
			return parsed?.modelId === modelId && parsed.file === file && parsed.version !== version;
		});

		return fallbackKey ? this.store.get(fallbackKey) : undefined;
	}

	private async download(
		file: VoiceModelFile,
		modelId: string,
		version: string,
		signal?: AbortSignal,
	): Promise<ArrayBuffer> {
		const response = await fetch(file.downloadUrl, signal ? { signal } : undefined);
		if (!response.ok) {
			throw new Error(`Failed to download ${file.downloadUrl}: HTTP ${response.status}`);
		}

		const headerTotal = Number(response.headers.get("content-length") ?? 0);
		const total = file.byteSize > 0 ? file.byteSize : headerTotal;

		if (!response.body) {
			const buffer = await response.arrayBuffer();
			const knownTotal = total > 0 ? total : buffer.byteLength;
			this.onProgress.emit({ modelId, version, file: file.file, loaded: buffer.byteLength, total: knownTotal });
			return buffer;
		}

		const reader = response.body.getReader();
		const chunks: Uint8Array[] = [];
		let loaded = 0;
		for (;;) {
			// biome-ignore lint/performance/noAwaitInLoops: streaming a download — each read must complete before the next.
			const { done, value } = await reader.read();
			if (done) {
				break;
			}

			chunks.push(value);
			loaded += value.length;
			this.onProgress.emit({ modelId, version, file: file.file, loaded, total: total > 0 ? total : loaded });
		}

		return concatChunks(chunks, loaded);
	}
}
