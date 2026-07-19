package media

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

type Config struct {
	Path           string
	MaxBytes       int64
	MaxObjectBytes int64
	CenterURL      string
	APIToken       string
	NodeID         string
}

type Stats struct {
	Entries   int    `json:"entries"`
	Bytes     int64  `json:"bytes"`
	MaxBytes  int64  `json:"max_bytes"`
	Hits      uint64 `json:"hits"`
	Misses    uint64 `json:"misses"`
	Evictions uint64 `json:"evictions"`
	Failures  uint64 `json:"failures"`
}

type Result struct {
	Body        []byte
	ContentType string
	ETag        string
	State       string
}

type entry struct {
	Key         string    `json:"key"`
	Filename    string    `json:"filename"`
	Size        int64     `json:"size"`
	ContentType string    `json:"content_type"`
	ETag        string    `json:"etag"`
	LastAccess  time.Time `json:"last_access"`
}

type flight struct {
	done   chan struct{}
	result Result
	err    error
}

type Cache struct {
	cfg      Config
	client   *http.Client
	mu       sync.Mutex
	entries  map[string]entry
	flights  map[string]*flight
	bytes    int64
	hits     uint64
	misses   uint64
	evicted  uint64
	failures uint64
}

func Open(cfg Config) (*Cache, error) {
	if strings.TrimSpace(cfg.Path) == "" {
		return nil, fmt.Errorf("media cache path is required")
	}
	if cfg.MaxBytes <= 0 {
		cfg.MaxBytes = 2 << 30
	}
	if cfg.MaxObjectBytes <= 0 {
		cfg.MaxObjectBytes = 10 << 20
	}
	if err := os.MkdirAll(cfg.Path, 0o700); err != nil {
		return nil, err
	}
	cache := &Cache{
		cfg: cfg, client: &http.Client{Timeout: 30 * time.Second},
		entries: make(map[string]entry), flights: make(map[string]*flight),
	}
	cache.loadIndex()
	cache.mu.Lock()
	cache.evictLocked()
	err := cache.persistLocked()
	cache.mu.Unlock()
	return cache, err
}

func (c *Cache) Get(ctx context.Context, namespaceID uint, entity string, entityID uint, version string) (Result, error) {
	key := mediaKey(namespaceID, entity, entityID, version)
	if result, ok := c.lookup(key); ok {
		return result, nil
	}

	c.mu.Lock()
	if existing := c.flights[key]; existing != nil {
		c.mu.Unlock()
		select {
		case <-ctx.Done():
			return Result{}, ctx.Err()
		case <-existing.done:
			return existing.result, existing.err
		}
	}
	current := &flight{done: make(chan struct{})}
	c.flights[key] = current
	c.misses++
	c.mu.Unlock()

	result, err := c.fetchAndStore(ctx, key, namespaceID, entity, entityID, version)
	c.mu.Lock()
	if err != nil {
		c.failures++
	}
	current.result, current.err = result, err
	delete(c.flights, key)
	close(current.done)
	c.mu.Unlock()
	return result, err
}

func (c *Cache) lookup(key string) (Result, bool) {
	c.mu.Lock()
	item, ok := c.entries[key]
	if ok {
		item.LastAccess = time.Now().UTC()
		c.entries[key] = item
	}
	c.mu.Unlock()
	if !ok {
		return Result{}, false
	}
	body, err := os.ReadFile(filepath.Join(c.cfg.Path, item.Filename))
	if err != nil || int64(len(body)) != item.Size {
		c.mu.Lock()
		c.removeLocked(item)
		_ = c.persistLocked()
		c.mu.Unlock()
		return Result{}, false
	}
	c.mu.Lock()
	c.hits++
	c.mu.Unlock()
	return Result{Body: body, ContentType: item.ContentType, ETag: item.ETag, State: "HIT"}, true
}

func (c *Cache) fetchAndStore(ctx context.Context, key string, namespaceID uint, entity string, entityID uint, version string) (Result, error) {
	endpoint := strings.TrimRight(c.cfg.CenterURL, "/") + "/api/edge/catalog/media/" + entity + "/" + strconv.FormatUint(uint64(entityID), 10) + "/thumbnail"
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint, nil)
	if err != nil {
		return Result{}, err
	}
	request.Header.Set("X-API-Token", c.cfg.APIToken)
	request.Header.Set("X-FSCM-Edge-Node-ID", c.cfg.NodeID)
	request.Header.Set("X-Namespace-ID", strconv.FormatUint(uint64(namespaceID), 10))
	response, err := c.client.Do(request)
	if err != nil {
		return Result{}, err
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return Result{}, fmt.Errorf("catalog media center returned %d", response.StatusCode)
	}
	contentType := strings.TrimSpace(strings.Split(response.Header.Get("Content-Type"), ";")[0])
	if !strings.HasPrefix(strings.ToLower(contentType), "image/") {
		return Result{}, fmt.Errorf("catalog media center returned non-image content")
	}
	body, err := io.ReadAll(io.LimitReader(response.Body, c.cfg.MaxObjectBytes+1))
	if err != nil {
		return Result{}, err
	}
	if len(body) == 0 || int64(len(body)) > c.cfg.MaxObjectBytes {
		return Result{}, fmt.Errorf("catalog media exceeds cache object limit")
	}
	detectedContentType := http.DetectContentType(body)
	if !strings.HasPrefix(strings.ToLower(detectedContentType), "image/") {
		return Result{}, fmt.Errorf("catalog media body is not an image")
	}
	etag := `"` + version + `"`
	filename := key + ".bin"
	temporary, err := os.CreateTemp(c.cfg.Path, key+"-*.tmp")
	if err != nil {
		return Result{}, err
	}
	temporaryName := temporary.Name()
	defer os.Remove(temporaryName)
	if _, err = temporary.Write(body); err == nil {
		err = temporary.Sync()
	}
	if closeErr := temporary.Close(); err == nil {
		err = closeErr
	}
	if err != nil {
		return Result{}, err
	}
	if err = os.Rename(temporaryName, filepath.Join(c.cfg.Path, filename)); err != nil {
		return Result{}, err
	}

	c.mu.Lock()
	item := entry{Key: key, Filename: filename, Size: int64(len(body)), ContentType: contentType, ETag: etag, LastAccess: time.Now().UTC()}
	if previous, ok := c.entries[key]; ok {
		c.bytes -= previous.Size
	}
	c.entries[key], c.bytes = item, c.bytes+item.Size
	c.evictLocked()
	err = c.persistLocked()
	c.mu.Unlock()
	if err != nil {
		return Result{}, err
	}
	return Result{Body: body, ContentType: contentType, ETag: etag, State: "MISS"}, nil
}

func (c *Cache) Status() Stats {
	c.mu.Lock()
	defer c.mu.Unlock()
	return Stats{Entries: len(c.entries), Bytes: c.bytes, MaxBytes: c.cfg.MaxBytes, Hits: c.hits, Misses: c.misses, Evictions: c.evicted, Failures: c.failures}
}

func (c *Cache) Close() error {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.persistLocked()
}

func (c *Cache) evictLocked() {
	items := make([]entry, 0, len(c.entries))
	for _, item := range c.entries {
		items = append(items, item)
	}
	sort.Slice(items, func(i, j int) bool { return items[i].LastAccess.Before(items[j].LastAccess) })
	for _, item := range items {
		if c.bytes <= c.cfg.MaxBytes {
			break
		}
		c.removeLocked(item)
		c.evicted++
	}
}

func (c *Cache) removeLocked(item entry) {
	delete(c.entries, item.Key)
	c.bytes -= item.Size
	_ = os.Remove(filepath.Join(c.cfg.Path, item.Filename))
}

func (c *Cache) loadIndex() {
	body, err := os.ReadFile(filepath.Join(c.cfg.Path, "index.json"))
	if err != nil {
		return
	}
	var items []entry
	if json.Unmarshal(body, &items) != nil {
		return
	}
	for _, item := range items {
		info, statErr := os.Stat(filepath.Join(c.cfg.Path, item.Filename))
		if statErr != nil || info.Size() != item.Size {
			continue
		}
		c.entries[item.Key] = item
		c.bytes += item.Size
	}
}

func (c *Cache) persistLocked() error {
	items := make([]entry, 0, len(c.entries))
	for _, item := range c.entries {
		items = append(items, item)
	}
	body, err := json.Marshal(items)
	if err != nil {
		return err
	}
	temporary := filepath.Join(c.cfg.Path, "index.json.tmp")
	if err = os.WriteFile(temporary, body, 0o600); err != nil {
		return err
	}
	return os.Rename(temporary, filepath.Join(c.cfg.Path, "index.json"))
}

func mediaKey(namespaceID uint, entity string, entityID uint, version string) string {
	raw := fmt.Sprintf("%d\x00%s\x00%d\x00%s", namespaceID, entity, entityID, version)
	digest := sha256.Sum256([]byte(raw))
	return hex.EncodeToString(digest[:])
}
