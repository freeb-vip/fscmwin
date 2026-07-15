package main

import (
	"bytes"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"net/url"
	"path/filepath"
	"testing"
	"time"

	"fscm-edge/internal/cache"
	"fscm-edge/internal/catalog"
	edgeproxy "fscm-edge/internal/proxy"

	"github.com/gin-gonic/gin"
)

func TestCatalogSKUProxyHitAndFallback(t *testing.T) {
	gin.SetMode(gin.TestMode)
	store, err := catalog.Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	generation, err := store.BeginFullSync(7)
	if err != nil {
		t.Fatal(err)
	}
	if err = store.UpsertSKUs(7, generation, []catalog.SKU{{ID: 1, Code: "B10-RED", ProductID: 2, ProductCode: "B10"}}); err != nil {
		t.Fatal(err)
	}
	if err = store.FinishFullSync(7, generation, 1); err != nil {
		t.Fatal(err)
	}

	publicKey, privateKey, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	manager := catalog.NewManager(catalog.Config{NodeID: "edge-1", NamespaceID: 7, SKUQRPrefix: "T"}, store)
	manager.SetTicketPublicKey(base64.RawURLEncoding.EncodeToString(publicKey))

	upstreamCalls := 0
	center := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		upstreamCalls++
		writer.Header().Set("Content-Type", "application/json")
		_, _ = writer.Write([]byte(`{"code":0,"data":{"items":[{"code":"CENTER"}],"total":1},"msg":"ok"}`))
	}))
	defer center.Close()
	target, _ := url.Parse(center.URL)
	proxyHandler, err := edgeproxy.New(edgeproxy.Config{CenterURL: target.String(), NodeID: "edge-1", CacheWhitelist: []string{"/api/skus"}, MaxObjectBytes: 1 << 20}, cache.New(cache.Config{Mode: cache.Standard}))
	if err != nil {
		t.Fatal(err)
	}

	router := gin.New()
	router.GET("/api/skus", func(c *gin.Context) { serveCatalogSKUs(c, manager, proxyHandler) })
	request := httptest.NewRequest(http.MethodGet, "/api/skus?keyword=TB10-&match_mode=prefix", nil)
	request.Header.Set("X-Edge-Ticket", signedCatalogTicket(t, privateKey, 7, "edge-1"))
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Header().Get("X-FSCM-Cache") != "CATALOG-HIT" {
		t.Fatalf("catalog hit status=%d cache=%q", response.Code, response.Header().Get("X-FSCM-Cache"))
	}
	if upstreamCalls != 0 || !bytes.Contains(response.Body.Bytes(), []byte("B10-RED")) {
		t.Fatalf("expected local catalog response, calls=%d body=%s", upstreamCalls, response.Body.String())
	}

	request = httptest.NewRequest(http.MethodGet, "/api/skus?keyword=B10&match_mode=fuzzy", nil)
	response = httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK || upstreamCalls != 1 || !bytes.Contains(response.Body.Bytes(), []byte("CENTER")) {
		t.Fatalf("expected proxy fallback, calls=%d body=%s", upstreamCalls, response.Body.String())
	}
}

func signedCatalogTicket(t *testing.T, privateKey ed25519.PrivateKey, namespaceID uint, nodeID string) string {
	t.Helper()
	header, err := json.Marshal(map[string]string{"alg": "EdDSA", "typ": "JWT"})
	if err != nil {
		t.Fatal(err)
	}
	payload, err := json.Marshal(map[string]interface{}{"ns": namespaceID, "node": nodeID, "scope": "catalog:read", "iss": "fscm-edge-catalog", "exp": time.Now().Add(time.Hour).Unix()})
	if err != nil {
		t.Fatal(err)
	}
	encodedHeader, encodedPayload := base64.RawURLEncoding.EncodeToString(header), base64.RawURLEncoding.EncodeToString(payload)
	signature := ed25519.Sign(privateKey, []byte(encodedHeader+"."+encodedPayload))
	return encodedHeader + "." + encodedPayload + "." + base64.RawURLEncoding.EncodeToString(signature)
}
