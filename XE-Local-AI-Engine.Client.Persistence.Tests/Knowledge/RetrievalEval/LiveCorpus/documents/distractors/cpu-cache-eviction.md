# CPU cache eviction policies

A processor cache is small and fast. When it is full, every new cache line forces an eviction, and the eviction
policy decides which cache entry leaves.

## Least recently used

LRU evicts the entry that has not been touched for the longest time. True LRU is expensive in hardware, so most
caches use a pseudo-LRU tree that approximates it with a few bits per cache set.

## Time to live is a software idea

Hardware caches have no time to live: an entry stays until it is evicted or invalidated. Software caches, such as a
DNS cache or an HTTP cache, add a time to live per entry and a maximum number of entries, and often a memory budget
in bytes, so the cache cannot grow without bound.

## Hashing the key

A cache set is chosen by hashing part of the memory address. Poor hashing makes many addresses collide in the same
set, which causes evictions even when the cache as a whole is nearly empty.
