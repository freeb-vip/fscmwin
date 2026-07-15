package proxy

import (
	"net/http"
	"net/http/httptest"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"fscm-edge/internal/cache"
)

func TestCacheIsolationAndHeaderStripping(t *testing.T) {
	var calls atomic.Int32
	center := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		if r.Header.Get("X-Edge-Ticket") != "" {
			t.Error("edge ticket was forwarded")
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"items":[1]}`))
	}))
	defer center.Close()
	handler := newTestHandler(t, center.URL)

	request := httptest.NewRequest(http.MethodGet, "http://edge/api/products?page=1", nil)
	request.Header.Set("Authorization", "Bearer user-a")
	request.Header.Set("X-Namespace-ID", "7")
	request.Header.Set("X-Edge-Ticket", "local")
	first := httptest.NewRecorder()
	handler.ServeHTTP(first, request)
	second := httptest.NewRecorder()
	handler.ServeHTTP(second, request.Clone(request.Context()))
	if first.Header().Get("X-FSCM-Cache") != "MISS" || second.Header().Get("X-FSCM-Cache") != "HIT" {
		t.Fatalf("unexpected cache states: %s %s", first.Header().Get("X-FSCM-Cache"), second.Header().Get("X-FSCM-Cache"))
	}
	other := request.Clone(request.Context())
	other.Header = request.Header.Clone()
	other.Header.Set("Authorization", "Bearer user-b")
	handler.ServeHTTP(httptest.NewRecorder(), other)
	if calls.Load() != 2 {
		t.Fatalf("expected isolated token cache, center calls=%d", calls.Load())
	}
}

func TestPrepareRequestRemovesHopByHopHeaders(t *testing.T) {
	handler := newTestHandler(t, "http://center.invalid")
	request := httptest.NewRequest(http.MethodGet, "/api/products", nil)
	request.Header.Set("Connection", "Keep-Alive, X-Temporary")
	request.Header.Set("Keep-Alive", "timeout=5")
	request.Header.Set("X-Temporary", "secret")

	handler.prepareRequest(request)

	for _, name := range []string{"Connection", "Keep-Alive", "X-Temporary"} {
		if request.Header.Get(name) != "" {
			t.Fatalf("hop-by-hop header %s was not removed", name)
		}
	}
}

func TestConcurrentMissIsCoalesced(t *testing.T) {
	var calls atomic.Int32
	center := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		calls.Add(1)
		time.Sleep(40 * time.Millisecond)
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"ok":true}`))
	}))
	defer center.Close()
	handler := newTestHandler(t, center.URL)
	var group sync.WaitGroup
	for i := 0; i < 12; i++ {
		group.Add(1)
		go func() {
			defer group.Done()
			request := httptest.NewRequest(http.MethodGet, "http://edge/api/products", nil)
			request.Header.Set("Authorization", "Bearer same")
			request.Header.Set("X-Namespace-ID", "1")
			recorder := httptest.NewRecorder()
			handler.ServeHTTP(recorder, request)
			if recorder.Code != 200 {
				t.Errorf("unexpected status %d", recorder.Code)
			}
		}()
	}
	group.Wait()
	if calls.Load() != 1 {
		t.Fatalf("expected one upstream call, got %d", calls.Load())
	}
}

func TestSuccessfulMutationInvalidatesNamespace(t *testing.T) {
	var gets atomic.Int32
	center := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodGet {
			gets.Add(1)
			w.Header().Set("Content-Type", "application/json")
			_, _ = w.Write([]byte(`{"ok":true}`))
			return
		}
		w.WriteHeader(http.StatusOK)
	}))
	defer center.Close()
	handler := newTestHandler(t, center.URL)
	newRequest := func(method string) *http.Request {
		request := httptest.NewRequest(method, "http://edge/api/products", nil)
		request.Header.Set("Authorization", "Bearer a")
		request.Header.Set("X-Namespace-ID", "4")
		return request
	}
	handler.ServeHTTP(httptest.NewRecorder(), newRequest(http.MethodGet))
	handler.ServeHTTP(httptest.NewRecorder(), newRequest(http.MethodGet))
	handler.ServeHTTP(httptest.NewRecorder(), newRequest(http.MethodPost))
	handler.ServeHTTP(httptest.NewRecorder(), newRequest(http.MethodGet))
	if gets.Load() != 2 {
		t.Fatalf("expected cache refill after mutation, GET calls=%d", gets.Load())
	}
}

func TestStaleIfCenterFails(t *testing.T) {
	center := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusServiceUnavailable) }))
	defer center.Close()
	store := cache.New(cache.Config{Mode: cache.Standard, MaxEntries: 10, MaxBytes: 1 << 20, MaxObjectBytes: 1 << 20, MaxStale: 24 * time.Hour})
	handler, err := New(Config{CenterURL: center.URL, CacheWhitelist: []string{"/api/products"}, StaleIfError: true, MaxObjectBytes: 1 << 20}, store)
	if err != nil {
		t.Fatal(err)
	}
	request := httptest.NewRequest(http.MethodGet, "http://edge/api/products", nil)
	request.Header.Set("Authorization", "Bearer a")
	request.Header.Set("X-Namespace-ID", "1")
	store.Put(cacheKey(request), "1", request.URL.Path, cache.Response{StatusCode: 200, Header: http.Header{"Content-Type": []string{"application/json"}}, Body: []byte(`{"cached":true}`)}, time.Now().Add(-time.Hour))
	recorder := httptest.NewRecorder()
	handler.ServeHTTP(recorder, request)
	if recorder.Code != 200 || recorder.Header().Get("X-FSCM-Cache") != "STALE" {
		t.Fatalf("expected stale response, code=%d cache=%s", recorder.Code, recorder.Header().Get("X-FSCM-Cache"))
	}
}

func newTestHandler(t *testing.T, centerURL string) *Handler {
	t.Helper()
	store := cache.New(cache.Config{Mode: cache.Standard, MaxEntries: 100, MaxBytes: 1 << 20, MaxObjectBytes: 1 << 20, MaxStale: time.Hour})
	handler, err := New(Config{CenterURL: centerURL, CacheWhitelist: []string{"/api/products"}, StaleIfError: true, MaxObjectBytes: 1 << 20}, store)
	if err != nil {
		t.Fatal(err)
	}
	return handler
}
