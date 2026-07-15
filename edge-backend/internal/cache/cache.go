package cache

import (
	"container/list"
	"net/http"
	"strings"
	"sync"
	"time"
)

type Mode string

const (
	Disabled   Mode = "disabled"
	Standard   Mode = "standard"
	Aggressive Mode = "aggressive"
)

type Config struct {
	Mode           Mode
	MaxEntries     int
	MaxBytes       int64
	MaxObjectBytes int64
	MaxStale       time.Duration
}

type Response struct {
	StatusCode int
	Header     http.Header
	Body       []byte
	StoredAt   time.Time
	ExpiresAt  time.Time
	Namespace  string
	Path       string
}

type Lookup struct {
	Response Response
	Fresh    bool
	Found    bool
}

type Stats struct {
	Mode      Mode              `json:"mode"`
	Entries   int               `json:"entries"`
	Bytes     int64             `json:"bytes"`
	Hits      uint64            `json:"hits"`
	Misses    uint64            `json:"misses"`
	StaleHits uint64            `json:"stale_hits"`
	Bypasses  uint64            `json:"bypasses"`
	Evictions uint64            `json:"evictions"`
	HotPaths  map[string]uint64 `json:"hot_paths"`
}

type entry struct {
	key   string
	value Response
	size  int64
}

type heat struct {
	window time.Time
	count  int
}

type Cache struct {
	mu                                           sync.Mutex
	cfg                                          Config
	items                                        map[string]*list.Element
	lru                                          *list.List
	heat                                         map[string]heat
	paths                                        map[string]uint64
	bytes                                        int64
	hits, misses, staleHits, bypasses, evictions uint64
}

func New(cfg Config) *Cache {
	if cfg.MaxEntries < 1 {
		cfg.MaxEntries = 5000
	}
	if cfg.MaxBytes < 1 {
		cfg.MaxBytes = 256 << 20
	}
	if cfg.MaxObjectBytes < 1 {
		cfg.MaxObjectBytes = 5 << 20
	}
	if cfg.MaxStale < 1 {
		cfg.MaxStale = 24 * time.Hour
	}
	return &Cache{cfg: cfg, items: make(map[string]*list.Element), lru: list.New(), heat: make(map[string]heat), paths: make(map[string]uint64)}
}

func (c *Cache) Enabled() bool { return c.cfg.Mode != Disabled }

func (c *Cache) Get(key string, now time.Time) Lookup {
	c.mu.Lock()
	defer c.mu.Unlock()
	element, ok := c.items[key]
	if !ok {
		c.misses++
		return Lookup{}
	}
	item := element.Value.(*entry)
	if now.Sub(item.value.StoredAt) > c.cfg.MaxStale {
		c.remove(element)
		c.misses++
		return Lookup{}
	}
	c.lru.MoveToFront(element)
	value := cloneResponse(item.value)
	if now.Before(item.value.ExpiresAt) {
		c.hits++
		return Lookup{Response: value, Fresh: true, Found: true}
	}
	return Lookup{Response: value, Found: true}
}

func (c *Cache) RecordStaleHit() { c.mu.Lock(); c.staleHits++; c.mu.Unlock() }
func (c *Cache) RecordBypass()   { c.mu.Lock(); c.bypasses++; c.mu.Unlock() }

func (c *Cache) Put(key, namespace, path string, response Response, now time.Time) bool {
	if c.cfg.Mode == Disabled || int64(len(response.Body)) > c.cfg.MaxObjectBytes {
		return false
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	ttl := c.ttlLocked(key, now)
	response.StoredAt, response.ExpiresAt, response.Namespace, response.Path = now, now.Add(ttl), namespace, path
	response = cloneResponse(response)
	size := int64(len(response.Body)) + headersSize(response.Header) + int64(len(key)+len(namespace)+len(path))
	if size > c.cfg.MaxBytes {
		return false
	}
	if existing, ok := c.items[key]; ok {
		c.remove(existing)
	}
	element := c.lru.PushFront(&entry{key: key, value: response, size: size})
	c.items[key], c.bytes = element, c.bytes+size
	for len(c.items) > c.cfg.MaxEntries || c.bytes > c.cfg.MaxBytes {
		c.remove(c.lru.Back())
	}
	return true
}

func (c *Cache) InvalidateNamespace(namespace string) int {
	c.mu.Lock()
	defer c.mu.Unlock()
	removed := 0
	for _, element := range c.items {
		item := element.Value.(*entry)
		if namespace == "" || item.value.Namespace == namespace {
			c.remove(element)
			removed++
		}
	}
	return removed
}

func (c *Cache) Clear() int { return c.InvalidateNamespace("") }

func (c *Cache) Status() Stats {
	c.mu.Lock()
	defer c.mu.Unlock()
	hot := make(map[string]uint64, len(c.paths))
	for path, count := range c.paths {
		hot[path] = count
	}
	return Stats{Mode: c.cfg.Mode, Entries: len(c.items), Bytes: c.bytes, Hits: c.hits, Misses: c.misses, StaleHits: c.staleHits, Bypasses: c.bypasses, Evictions: c.evictions, HotPaths: hot}
}

func (c *Cache) TrackPath(path string) {
	c.mu.Lock()
	c.paths[path]++
	c.mu.Unlock()
}

func (c *Cache) ttlLocked(key string, now time.Time) time.Duration {
	current := c.heat[key]
	if current.window.IsZero() || now.Sub(current.window) >= time.Minute {
		current = heat{window: now}
	}
	current.count++
	c.heat[key] = current
	if c.cfg.Mode == Aggressive {
		if current.count >= 10 {
			return 15 * time.Minute
		}
		if current.count >= 3 {
			return 5 * time.Minute
		}
		return 2 * time.Minute
	}
	if current.count >= 10 {
		return 5 * time.Minute
	}
	if current.count >= 3 {
		return 2 * time.Minute
	}
	return 30 * time.Second
}

func (c *Cache) remove(element *list.Element) {
	if element == nil {
		return
	}
	item := element.Value.(*entry)
	delete(c.items, item.key)
	c.lru.Remove(element)
	c.bytes -= item.size
	c.evictions++
}

func cloneResponse(value Response) Response {
	copyValue := value
	copyValue.Body = append([]byte(nil), value.Body...)
	copyValue.Header = value.Header.Clone()
	return copyValue
}

func headersSize(headers http.Header) int64 {
	var size int64
	for key, values := range headers {
		size += int64(len(key))
		for _, value := range values {
			size += int64(len(value))
		}
	}
	return size
}

func CacheControlDisallows(headers http.Header) bool {
	value := strings.ToLower(headers.Get("Cache-Control"))
	return strings.Contains(value, "no-store") || strings.Contains(value, "private") || headers.Get("Set-Cookie") != ""
}
