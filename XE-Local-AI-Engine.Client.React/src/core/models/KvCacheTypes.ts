// The KV-cache element types a llama.cpp spawn accepts, mirroring the backend `LlamaServerKvCacheTypes` allow-list.
// `f16` emits no -ctk/-ctv at all; the quantized types halve (q8_0) or quarter (q4_0) the KV bytes and require flash
// attention. Node settings pick one for every chat spawn; a benchmark cell picks one per run or leaves it "Auto".
// One list, because a fourth type must reach both surfaces or neither.
export const kvCacheTypes = ["f16", "q8_0", "q4_0"] as const;

export type KvCacheType = (typeof kvCacheTypes)[number];
