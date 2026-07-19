package media

import (
	"context"
	"net/http"
	"net/http/httptest"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

var tinyPNG = []byte{0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52}

func TestCachePersistsHitAcrossRestartAndUsesNodeCredentials(t *testing.T) {
	var calls atomic.Int32
	center := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		calls.Add(1)
		if request.Header.Get("X-API-Token") != "edge-token" || request.Header.Get("X-FSCM-Edge-Node-ID") != "edge-1" || request.Header.Get("X-Namespace-ID") != "7" {
			t.Fatalf("missing node credentials: %#v", request.Header)
		}
		writer.Header().Set("Content-Type", "image/png")
		_, _ = writer.Write(tinyPNG)
	}))
	defer center.Close()

	path := t.TempDir()
	config := Config{Path: path, MaxBytes: 1024, MaxObjectBytes: 1024, CenterURL: center.URL, APIToken: "edge-token", NodeID: "edge-1"}
	cache, err := Open(config)
	if err != nil {
		t.Fatal(err)
	}
	first, err := cache.Get(context.Background(), 7, "sku", 9, "v1")
	if err != nil || first.State != "MISS" {
		t.Fatalf("first result=%+v err=%v", first, err)
	}
	second, err := cache.Get(context.Background(), 7, "sku", 9, "v1")
	if err != nil || second.State != "HIT" || calls.Load() != 1 {
		t.Fatalf("second result=%+v calls=%d err=%v", second, calls.Load(), err)
	}
	if err := cache.Close(); err != nil {
		t.Fatal(err)
	}

	reopened, err := Open(config)
	if err != nil {
		t.Fatal(err)
	}
	defer reopened.Close()
	afterRestart, err := reopened.Get(context.Background(), 7, "sku", 9, "v1")
	if err != nil || afterRestart.State != "HIT" || calls.Load() != 1 {
		t.Fatalf("restart result=%+v calls=%d err=%v", afterRestart, calls.Load(), err)
	}
}

func TestCacheCoalescesConcurrentMisses(t *testing.T) {
	var calls atomic.Int32
	center := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, _ *http.Request) {
		calls.Add(1)
		time.Sleep(25 * time.Millisecond)
		writer.Header().Set("Content-Type", "image/png")
		_, _ = writer.Write(tinyPNG)
	}))
	defer center.Close()
	cache, err := Open(Config{Path: t.TempDir(), MaxBytes: 1024, MaxObjectBytes: 1024, CenterURL: center.URL, APIToken: "token", NodeID: "edge"})
	if err != nil {
		t.Fatal(err)
	}
	defer cache.Close()

	var wait sync.WaitGroup
	for range 8 {
		wait.Add(1)
		go func() {
			defer wait.Done()
			if _, getErr := cache.Get(context.Background(), 1, "product", 3, "v1"); getErr != nil {
				t.Errorf("get: %v", getErr)
			}
		}()
	}
	wait.Wait()
	if calls.Load() != 1 {
		t.Fatalf("expected one center request, got %d", calls.Load())
	}
}

func TestCacheRejectsNonImageAndEvictsLeastRecentlyUsed(t *testing.T) {
	center := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		if request.URL.Path == "/api/edge/catalog/media/sku/99/thumbnail" {
			writer.Header().Set("Content-Type", "application/json")
			_, _ = writer.Write([]byte(`{"error":"no"}`))
			return
		}
		writer.Header().Set("Content-Type", "image/png")
		_, _ = writer.Write(tinyPNG)
	}))
	defer center.Close()
	cache, err := Open(Config{Path: t.TempDir(), MaxBytes: int64(len(tinyPNG)) + 1, MaxObjectBytes: 1024, CenterURL: center.URL, APIToken: "token", NodeID: "edge"})
	if err != nil {
		t.Fatal(err)
	}
	defer cache.Close()
	if _, err = cache.Get(context.Background(), 1, "sku", 1, "v1"); err != nil {
		t.Fatal(err)
	}
	if _, err = cache.Get(context.Background(), 1, "sku", 2, "v1"); err != nil {
		t.Fatal(err)
	}
	if status := cache.Status(); status.Entries != 1 || status.Evictions != 1 {
		t.Fatalf("unexpected eviction status: %+v", status)
	}
	if _, err = cache.Get(context.Background(), 1, "sku", 99, "v1"); err == nil {
		t.Fatal("expected non-image response to be rejected")
	}
}
