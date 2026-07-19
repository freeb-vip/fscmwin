package registry

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestAuthorizeMobilePrintForwardsCredentialAndMatchesNode(t *testing.T) {
	var authorization, namespace, client string
	center := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		authorization = r.Header.Get("Authorization")
		namespace = r.Header.Get("X-Namespace-ID")
		client = r.Header.Get("X-FSCM-Client")
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"code":0,"data":{"items":[{"node_id":"edge-01"}]},"msg":"ok"}`))
	}))
	defer center.Close()

	registry := New(Config{CenterURL: center.URL, NodeID: "edge-01", NamespaceID: 8})
	if err := registry.AuthorizeMobilePrint(context.Background(), "Bearer mobile-token"); err != nil {
		t.Fatal(err)
	}
	if authorization != "Bearer mobile-token" || namespace != "8" || client != "mobile-app" {
		t.Fatalf("unexpected forwarded headers: authorization=%q namespace=%q client=%q", authorization, namespace, client)
	}
}

func TestAuthorizeMobilePrintRejectsMissingNode(t *testing.T) {
	center := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"code":0,"data":{"items":[]},"msg":"ok"}`))
	}))
	defer center.Close()

	registry := New(Config{CenterURL: center.URL, NodeID: "edge-01"})
	err := registry.AuthorizeMobilePrint(context.Background(), "Bearer mobile-token")
	if !errors.Is(err, ErrMobilePrintNodeMissing) {
		t.Fatalf("expected missing-node error, got %v", err)
	}
}
