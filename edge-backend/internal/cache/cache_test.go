package cache

import (
	"net/http"
	"testing"
	"time"
)

func TestAdaptiveTTLAndNamespaceInvalidation(t *testing.T) {
	store := New(Config{Mode: Standard, MaxEntries: 10, MaxBytes: 1024 * 1024, MaxObjectBytes: 1024, MaxStale: time.Hour})
	now := time.Now()
	response := Response{StatusCode: http.StatusOK, Header: http.Header{"Content-Type": []string{"application/json"}}, Body: []byte(`{"ok":true}`)}
	for i := 0; i < 3; i++ {
		if !store.Put("key", "7", "/api/products", response, now.Add(time.Duration(i)*time.Second)) {
			t.Fatal("expected cache put")
		}
	}
	lookup := store.Get("key", now.Add(3*time.Second))
	if !lookup.Found || !lookup.Fresh {
		t.Fatalf("expected fresh entry: %#v", lookup)
	}
	if ttl := lookup.Response.ExpiresAt.Sub(lookup.Response.StoredAt); ttl != 2*time.Minute {
		t.Fatalf("expected warm TTL, got %s", ttl)
	}
	if removed := store.InvalidateNamespace("7"); removed != 1 {
		t.Fatalf("expected one removed entry, got %d", removed)
	}
}

func TestLRUEvictsByEntryLimit(t *testing.T) {
	store := New(Config{Mode: Standard, MaxEntries: 2, MaxBytes: 1024 * 1024, MaxObjectBytes: 1024, MaxStale: time.Hour})
	response := Response{StatusCode: 200, Header: http.Header{}, Body: []byte("x")}
	store.Put("a", "1", "/a", response, time.Now())
	store.Put("b", "1", "/b", response, time.Now())
	store.Put("c", "1", "/c", response, time.Now())
	if store.Get("a", time.Now()).Found {
		t.Fatal("least recently used entry should be evicted")
	}
	if status := store.Status(); status.Entries != 2 {
		t.Fatalf("expected 2 entries, got %d", status.Entries)
	}
}
