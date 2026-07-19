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
	catalogmedia "fscm-edge/internal/media"
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
	if err = store.UpsertSKUs(7, generation, []catalog.SKU{{ID: 1, Code: "B4-RED", ProductID: 2, ProductCode: "B4"}}); err != nil {
		t.Fatal(err)
	}
	if err = store.FinishFullSync(7, generation, 1); err != nil {
		t.Fatal(err)
	}

	publicKey, privateKey, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	upstreamCalls := 0
	center := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		upstreamCalls++
		writer.Header().Set("Content-Type", "application/json")
		_, _ = writer.Write([]byte(`{"code":0,"data":{"items":[{"code":"CENTER"}],"total":1},"msg":"ok"}`))
	}))
	defer center.Close()
	manager := catalog.NewManager(catalog.Config{CenterURL: center.URL, APIToken: "edge-token", NodeID: "edge-1", NamespaceID: 7, SKUQRPrefix: "T"}, store)
	if manager.TicketKeyReady() {
		t.Fatal("ticket key must not be ready before it is configured")
	}
	manager.SetTicketPublicKey(base64.RawURLEncoding.EncodeToString(publicKey))
	if !manager.TicketKeyReady() {
		t.Fatal("ticket key must be ready after it is configured")
	}
	target, _ := url.Parse(center.URL)
	proxyHandler, err := edgeproxy.New(edgeproxy.Config{CenterURL: target.String(), NodeID: "edge-1", CacheWhitelist: []string{"/api/skus"}, MaxObjectBytes: 1 << 20}, cache.New(cache.Config{Mode: cache.Standard}))
	if err != nil {
		t.Fatal(err)
	}

	router := gin.New()
	router.GET("/api/skus", func(c *gin.Context) { serveCatalogSKUs(c, manager, proxyHandler) })
	request := httptest.NewRequest(http.MethodGet, "/api/skus?keyword=TB4-&match_mode=prefix", nil)
	request.Header.Set("X-Edge-Ticket", signedCatalogTicket(t, privateKey, 7, "edge-1"))
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Header().Get("X-FSCM-Cache") != "CATALOG-HIT" {
		t.Fatalf("catalog hit status=%d cache=%q", response.Code, response.Header().Get("X-FSCM-Cache"))
	}
	if upstreamCalls != 0 || !bytes.Contains(response.Body.Bytes(), []byte("B4-RED")) {
		t.Fatalf("expected local catalog response, calls=%d body=%s", upstreamCalls, response.Body.String())
	}

	request = httptest.NewRequest(http.MethodGet, "/api/skus?keyword=B4&match_mode=fuzzy", nil)
	request.Header.Set("X-Edge-Ticket", signedCatalogTicket(t, privateKey, 7, "edge-1"))
	response = httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Header().Get("X-FSCM-Cache") == "CATALOG-HIT" || upstreamCalls != 1 || !bytes.Contains(response.Body.Bytes(), []byte("CENTER")) {
		t.Fatalf("expected proxy fallback, calls=%d body=%s", upstreamCalls, response.Body.String())
	}
}

func TestCatalogMediaRequiresTicketAndCachesCenterImage(t *testing.T) {
	gin.SetMode(gin.TestMode)
	publicKey, privateKey, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	centerCalls := 0
	center := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		centerCalls++
		if request.Header.Get("X-API-Token") != "edge-token" || request.Header.Get("X-FSCM-Edge-Node-ID") != "edge-1" {
			t.Fatalf("missing node credentials: %#v", request.Header)
		}
		writer.Header().Set("Content-Type", "image/png")
		_, _ = writer.Write([]byte{0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0x0d, 0x49, 0x48, 0x44, 0x52})
	}))
	defer center.Close()
	store, err := catalog.Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	manager := catalog.NewManager(catalog.Config{CenterURL: center.URL, APIToken: "edge-token", NodeID: "edge-1", NamespaceID: 7}, store)
	manager.SetTicketPublicKey(base64.RawURLEncoding.EncodeToString(publicKey))
	mediaCache, err := catalogmedia.Open(catalogmedia.Config{Path: t.TempDir(), MaxBytes: 1024, MaxObjectBytes: 1024, CenterURL: center.URL, APIToken: "edge-token", NodeID: "edge-1"})
	if err != nil {
		t.Fatal(err)
	}
	defer mediaCache.Close()
	router := gin.New()
	router.GET("/edge/v2/catalog/media/:entity/:id/thumbnail", func(c *gin.Context) { serveCatalogMedia(c, manager, mediaCache, 7) })

	unauthorized := httptest.NewRecorder()
	router.ServeHTTP(unauthorized, httptest.NewRequest(http.MethodGet, "/edge/v2/catalog/media/sku/1/thumbnail?v=v1", nil))
	if unauthorized.Code != http.StatusUnauthorized || centerCalls != 0 {
		t.Fatalf("unauthorized status=%d centerCalls=%d", unauthorized.Code, centerCalls)
	}

	ticket := signedCatalogTicket(t, privateKey, 7, "edge-1")
	for index, expectedState := range []string{"MISS", "HIT"} {
		request := httptest.NewRequest(http.MethodGet, "/edge/v2/catalog/media/sku/1/thumbnail?v=v1", nil)
		request.Header.Set("X-Edge-Ticket", ticket)
		response := httptest.NewRecorder()
		router.ServeHTTP(response, request)
		if response.Code != http.StatusOK || response.Header().Get("X-FSCM-Media-Cache") != expectedState {
			t.Fatalf("request %d status=%d cache=%q body=%s", index, response.Code, response.Header().Get("X-FSCM-Media-Cache"), response.Body.String())
		}
	}
	if centerCalls != 1 {
		t.Fatalf("expected one center image request, got %d", centerCalls)
	}
}

func TestCatalogProductsServeEmptyKeywordPageAndUseAuthenticatedCenterFill(t *testing.T) {
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
	if err = store.UpsertProducts(7, generation, []catalog.Product{{ID: 1, Code: "P-001"}, {ID: 2, Code: "P-002"}}); err != nil {
		t.Fatal(err)
	}
	if err = store.FinishFullSync(7, generation, 1); err != nil {
		t.Fatal(err)
	}
	publicKey, privateKey, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	centerCalls := 0
	center := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		centerCalls++
		if request.Header.Get("X-API-Token") != "edge-token" || request.Header.Get("X-FSCM-Edge-Node-ID") != "edge-1" || request.Header.Get("X-Namespace-ID") != "7" {
			writer.WriteHeader(http.StatusUnauthorized)
			return
		}
		writer.Header().Set("Content-Type", "application/json")
		_, _ = writer.Write([]byte(`{"code":0,"data":{"items":[{"id":3,"code":"CENTER-003"}],"total":1},"msg":"ok"}`))
	}))
	defer center.Close()
	manager := catalog.NewManager(catalog.Config{CenterURL: center.URL, APIToken: "edge-token", NodeID: "edge-1", NamespaceID: 7}, store)
	manager.SetTicketPublicKey(base64.RawURLEncoding.EncodeToString(publicKey))
	proxyHandler, err := edgeproxy.New(edgeproxy.Config{CenterURL: center.URL, NodeID: "edge-1", MaxObjectBytes: 1 << 20}, cache.New(cache.Config{Mode: cache.Standard}))
	if err != nil {
		t.Fatal(err)
	}
	router := gin.New()
	router.GET("/api/products", func(c *gin.Context) { serveCatalogProducts(c, manager, proxyHandler) })
	ticket := signedCatalogTicket(t, privateKey, 7, "edge-1")

	request := httptest.NewRequest(http.MethodGet, "/api/products?page=1&page_size=1", nil)
	request.Header.Set("X-Edge-Ticket", ticket)
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Header().Get("X-FSCM-Cache") != "CATALOG-HIT" || centerCalls != 0 || !bytes.Contains(response.Body.Bytes(), []byte(`"total":2`)) {
		t.Fatalf("local product page status=%d cache=%q centerCalls=%d body=%s", response.Code, response.Header().Get("X-FSCM-Cache"), centerCalls, response.Body.String())
	}

	request = httptest.NewRequest(http.MethodGet, "/api/products?keyword=CENTER&region=remote&page=1&page_size=20", nil)
	request.Header.Set("X-Edge-Ticket", ticket)
	response = httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Header().Get("X-FSCM-Cache") != "CATALOG-FILL" || centerCalls != 1 || !bytes.Contains(response.Body.Bytes(), []byte("CENTER-003")) {
		t.Fatalf("center product fill status=%d cache=%q centerCalls=%d body=%s", response.Code, response.Header().Get("X-FSCM-Cache"), centerCalls, response.Body.String())
	}

	request = httptest.NewRequest(http.MethodGet, "/api/products?keyword=DENIED", nil)
	response = httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusUnauthorized || centerCalls != 1 {
		t.Fatalf("invalid ticket status=%d centerCalls=%d body=%s", response.Code, centerCalls, response.Body.String())
	}
}

func TestCatalogSKUMissFetchesCenterAndFillsLocalCache(t *testing.T) {
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
	if err = store.FinishFullSync(7, generation, 1); err != nil {
		t.Fatal(err)
	}

	centerCalls := 0
	center := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		centerCalls++
		writer.Header().Set("Content-Type", "application/json")
		if request.URL.Path == "/api/skus/56" {
			_, _ = writer.Write([]byte(`{"code":0,"data":{"id":56,"code":"B6-BLUE","product_id":6,"product_code":"B6"},"msg":"ok"}`))
			return
		}
		_, _ = writer.Write([]byte(`{"code":0,"data":{"items":[{"id":55,"code":"B5-GREEN","product_id":5,"product_code":"B5"}],"total":1},"msg":"ok"}`))
	}))
	defer center.Close()

	publicKey, privateKey, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	manager := catalog.NewManager(catalog.Config{CenterURL: center.URL, APIToken: "edge-token", NodeID: "edge-1", NamespaceID: 7, SKUQRPrefix: "T"}, store)
	manager.SetTicketPublicKey(base64.RawURLEncoding.EncodeToString(publicKey))
	proxyHandler, err := edgeproxy.New(edgeproxy.Config{CenterURL: center.URL, NodeID: "edge-1", MaxObjectBytes: 1 << 20}, cache.New(cache.Config{Mode: cache.Standard}))
	if err != nil {
		t.Fatal(err)
	}
	router := gin.New()
	router.GET("/api/skus", func(c *gin.Context) { serveCatalogSKUs(c, manager, proxyHandler) })
	router.GET("/api/skus/:id", func(c *gin.Context) { serveCatalogSKU(c, manager, proxyHandler) })
	ticket := signedCatalogTicket(t, privateKey, 7, "edge-1")

	request := httptest.NewRequest(http.MethodGet, "/api/skus?keyword=B5-GREEN&match_mode=exact&page=1", nil)
	request.Header.Set("X-Edge-Ticket", ticket)
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Header().Get("X-FSCM-Cache") != "CATALOG-FILL" || !bytes.Contains(response.Body.Bytes(), []byte("B5-GREEN")) {
		t.Fatalf("center fill status=%d cache=%q body=%s", response.Code, response.Header().Get("X-FSCM-Cache"), response.Body.String())
	}
	response = httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Header().Get("X-FSCM-Cache") != "CATALOG-HIT" || centerCalls != 1 {
		t.Fatalf("cached exact status=%d cache=%q centerCalls=%d", response.Code, response.Header().Get("X-FSCM-Cache"), centerCalls)
	}

	request = httptest.NewRequest(http.MethodGet, "/api/skus/56", nil)
	request.Header.Set("X-Edge-Ticket", ticket)
	response = httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Header().Get("X-FSCM-Cache") != "CATALOG-FILL" || !bytes.Contains(response.Body.Bytes(), []byte("B6-BLUE")) {
		t.Fatalf("detail fill status=%d cache=%q body=%s", response.Code, response.Header().Get("X-FSCM-Cache"), response.Body.String())
	}
	response = httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Header().Get("X-FSCM-Cache") != "CATALOG-HIT" || centerCalls != 2 {
		t.Fatalf("cached detail status=%d cache=%q centerCalls=%d", response.Code, response.Header().Get("X-FSCM-Cache"), centerCalls)
	}
}

func TestCatalogBoxLabelListAndDetailPreferLocalGeneration(t *testing.T) {
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
	label := catalog.BoxLabel{
		ID: 31, LabelCode: "BX-LOCAL-31", StatusGroup: "normal",
		SupplierOrder: catalog.DocumentRef{Code: "SO-31"},
		Receiving:     catalog.BoxLabelReceiving{Status: "pending"},
		CreatedAt:     time.Now().UTC(),
		SKUItems:      []catalog.BoxLabelSKUItem{{SKUID: 8, SKUCode: "SKU-8", ProductID: 9}},
	}
	if err = store.UpsertBoxLabels(7, generation, []catalog.BoxLabel{label}); err != nil {
		t.Fatal(err)
	}
	if err = store.FinishFullSync(7, generation, 1); err != nil {
		t.Fatal(err)
	}

	publicKey, privateKey, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	manager := catalog.NewManager(catalog.Config{NodeID: "edge-1", NamespaceID: 7}, store)
	manager.SetTicketPublicKey(base64.RawURLEncoding.EncodeToString(publicKey))

	upstreamCalls := 0
	center := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		upstreamCalls++
		writer.Header().Set("Content-Type", "application/json")
		_, _ = writer.Write([]byte(`{"code":0,"data":{"data":[],"total":0},"msg":"ok"}`))
	}))
	defer center.Close()
	proxyHandler, err := edgeproxy.New(edgeproxy.Config{CenterURL: center.URL, NodeID: "edge-1", MaxObjectBytes: 1 << 20}, cache.New(cache.Config{Mode: cache.Standard}))
	if err != nil {
		t.Fatal(err)
	}

	router := gin.New()
	router.GET("/api/box-labels", func(c *gin.Context) { serveCatalogBoxLabels(c, manager, proxyHandler, false) })
	router.GET("/api/box-labels/:id/resolve", func(c *gin.Context) { serveCatalogBoxLabelResolve(c, manager, proxyHandler, false) })
	router.GET("/api/box-labels/:id", func(c *gin.Context) { serveCatalogBoxLabel(c, manager, proxyHandler, false) })
	ticket := signedCatalogTicket(t, privateKey, 7, "edge-1")

	request := httptest.NewRequest(http.MethodGet, "/api/box-labels?page=1&page_size=20&product_id=9", nil)
	request.Header.Set("X-Edge-Ticket", ticket)
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Header().Get("X-FSCM-Cache") != "CATALOG-HIT" || !bytes.Contains(response.Body.Bytes(), []byte("BX-LOCAL-31")) {
		t.Fatalf("local box list status=%d cache=%q body=%s", response.Code, response.Header().Get("X-FSCM-Cache"), response.Body.String())
	}

	request = httptest.NewRequest(http.MethodGet, "/api/box-labels/31", nil)
	request.Header.Set("X-Edge-Ticket", ticket)
	response = httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Header().Get("X-FSCM-Cache") != "CATALOG-HIT" || !bytes.Contains(response.Body.Bytes(), []byte("BX-LOCAL-31")) {
		t.Fatalf("local box detail status=%d cache=%q body=%s", response.Code, response.Header().Get("X-FSCM-Cache"), response.Body.String())
	}

	request = httptest.NewRequest(http.MethodGet, "/api/box-labels/BX-LOCAL-31/resolve", nil)
	request.Header.Set("X-Edge-Ticket", ticket)
	response = httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Header().Get("X-FSCM-Cache") != "CATALOG-HIT" || !bytes.Contains(response.Body.Bytes(), []byte(`"recognized":true`)) {
		t.Fatalf("local box resolve status=%d cache=%q body=%s", response.Code, response.Header().Get("X-FSCM-Cache"), response.Body.String())
	}
	if upstreamCalls != 0 {
		t.Fatalf("center was called %d times for local box-label hits", upstreamCalls)
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
