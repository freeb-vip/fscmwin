package registry

import (
	"context"
	"net/http"
	"net/http/httptest"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func TestSyncNowCoalescesConcurrentHeartbeat(t *testing.T) {
	started := make(chan struct{})
	release := make(chan struct{})
	var once sync.Once
	var calls atomic.Int32
	center := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		calls.Add(1)
		once.Do(func() { close(started) })
		<-release
		writer.Header().Set("Content-Type", "application/json")
		_, _ = writer.Write([]byte(`{"data":{"catalog_revision":0}}`))
	}))
	defer center.Close()
	client := New(Config{CenterURL: center.URL, APIToken: "token", NodeID: "edge-1"})

	var group sync.WaitGroup
	group.Add(1)
	go func() {
		defer group.Done()
		client.SyncNow(context.Background())
	}()
	select {
	case <-started:
	case <-time.After(time.Second):
		t.Fatal("first heartbeat did not start")
	}
	client.SyncNow(context.Background())
	close(release)
	group.Wait()
	if calls.Load() != 1 {
		t.Fatalf("concurrent heartbeat calls=%d, want 1", calls.Load())
	}
}
