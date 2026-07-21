package main

import (
	"errors"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"fscm-edge/internal/catalog"
	"fscm-edge/internal/printing"
	"fscm-edge/internal/registry"
	"github.com/gin-gonic/gin"
)

func TestCatalogCapabilitiesRequireReadyCatalog(t *testing.T) {
	withoutCatalog := catalogCapabilities(catalog.Status{Ready: false, BoxLabelsReady: false})
	withSKUCatalog := catalogCapabilities(catalog.Status{Ready: true, BoxLabelsReady: false})
	withFullCatalog := catalogCapabilities(catalog.Status{Ready: true, BoxLabelsReady: true})

	if hasCapability(withoutCatalog, "catalog_cache") || hasCapability(withoutCatalog, "box_label_catalog") {
		t.Fatalf("unready catalog advertised local capabilities: %v", withoutCatalog)
	}
	if !hasCapability(withSKUCatalog, "catalog_cache") || hasCapability(withSKUCatalog, "box_label_catalog") {
		t.Fatalf("unexpected SKU catalog capabilities: %v", withSKUCatalog)
	}
	if !hasCapability(withFullCatalog, "catalog_cache") || !hasCapability(withFullCatalog, "box_label_catalog") {
		t.Fatalf("unexpected full catalog capabilities: %v", withFullCatalog)
	}
}

func TestSyncRemoteCompletionsRetriesServerFailure(t *testing.T) {
	var calls atomic.Int32
	center := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if calls.Add(1) == 1 {
			w.WriteHeader(http.StatusInternalServerError)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"code":0,"data":{},"msg":"ok"}`))
	}))
	defer center.Close()

	service, err := printing.New(printing.Config{JobsPath: filepath.Join(t.TempDir(), "edge.db")})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = service.Close() }()
	if err := service.EnqueueRemoteFailure(11, "lease-11", "PRINT_FAILED", "offline", map[string]string{"phase": "test"}); err != nil {
		t.Fatal(err)
	}
	client := registry.New(registry.Config{CenterURL: center.URL, NodeID: "edge-1"})
	if err := syncRemoteCompletions(t.Context(), client, service); err == nil {
		t.Fatal("server failure was not reported")
	}
	if err := service.MarkRemoteCompletionFailed(11, errors.New("retry now"), time.Now().Add(-time.Second)); err != nil {
		t.Fatal(err)
	}
	if err := syncRemoteCompletions(t.Context(), client, service); err != nil {
		t.Fatal(err)
	}
	status, err := service.RemoteCompletionStatus()
	if err != nil || status.Pending != 0 || calls.Load() != 2 {
		t.Fatalf("completion was not cleared: status=%+v calls=%d err=%v", status, calls.Load(), err)
	}
}

func TestSyncRemoteCompletionsDropsExpiredLeaseReceipt(t *testing.T) {
	center := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusConflict)
	}))
	defer center.Close()
	service, err := printing.New(printing.Config{JobsPath: filepath.Join(t.TempDir(), "edge.db")})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = service.Close() }()
	if err := service.EnqueueRemoteFailure(12, "expired", "PRINT_FAILED", "offline", nil); err != nil {
		t.Fatal(err)
	}
	if err := syncRemoteCompletions(t.Context(), registry.New(registry.Config{CenterURL: center.URL, NodeID: "edge-1"}), service); err != nil {
		t.Fatal(err)
	}
	status, err := service.RemoteCompletionStatus()
	if err != nil || status.Pending != 0 {
		t.Fatalf("expired receipt was retained: status=%+v err=%v", status, err)
	}
}

func hasCapability(capabilities []string, want string) bool {
	for _, capability := range capabilities {
		if capability == want {
			return true
		}
	}
	return false
}

func TestAdminMiddlewareRequiresConfiguredToken(t *testing.T) {
	gin.SetMode(gin.TestMode)
	for _, test := range []struct {
		name       string
		configured string
		provided   string
		wantStatus int
	}{
		{name: "blank configuration denies loopback", wantStatus: http.StatusUnauthorized},
		{name: "missing token denied", configured: "secret", wantStatus: http.StatusUnauthorized},
		{name: "matching token accepted", configured: "secret", provided: "secret", wantStatus: http.StatusNoContent},
	} {
		t.Run(test.name, func(t *testing.T) {
			router := gin.New()
			router.GET("/managed", adminMiddleware(test.configured), func(c *gin.Context) { c.Status(http.StatusNoContent) })
			request := httptest.NewRequest(http.MethodGet, "/managed", nil)
			request.RemoteAddr = "127.0.0.1:12345"
			request.Header.Set("X-Edge-Admin-Token", test.provided)
			response := httptest.NewRecorder()
			router.ServeHTTP(response, request)
			if response.Code != test.wantStatus {
				t.Fatalf("status=%d, want %d", response.Code, test.wantStatus)
			}
		})
	}
}

func TestReadCatalogPageValidatesPagination(t *testing.T) {
	gin.SetMode(gin.TestMode)
	router := gin.New()
	router.GET("/page", func(c *gin.Context) {
		page, ok := readCatalogPage(c)
		if !ok {
			return
		}
		c.JSON(http.StatusOK, gin.H{"page": page.Page, "page_size": page.PageSize})
	})

	for _, test := range []struct {
		query      string
		wantStatus int
	}{
		{query: "?page=2&page_size=50", wantStatus: http.StatusOK},
		{query: "?page=0&page_size=20", wantStatus: http.StatusBadRequest},
		{query: "?page=1&page_size=101", wantStatus: http.StatusBadRequest},
		{query: "?page=nope&page_size=20", wantStatus: http.StatusBadRequest},
	} {
		response := httptest.NewRecorder()
		request := httptest.NewRequest(http.MethodGet, "/page"+test.query, nil)
		router.ServeHTTP(response, request)
		if response.Code != test.wantStatus {
			t.Fatalf("query=%s status=%d, want %d", test.query, response.Code, test.wantStatus)
		}
	}
}

func TestValidClaimPayload(t *testing.T) {
	tests := []struct {
		name    string
		kind    string
		skuCode string
		text    string
		items   []printing.BoxMark
		valid   bool
	}{
		{name: "manual text", kind: "manual_text", text: "Test label", valid: true},
		{name: "empty manual text", kind: "manual_text", valid: false},
		{name: "batch content", kind: "batch_content", text: "H01-00-01", valid: true},
		{name: "empty batch content", kind: "batch_content", valid: false},
		{name: "sku payload", skuCode: "SKU-001", valid: true},
		{name: "empty sku payload", valid: false},
		{name: "box mark", kind: "manufacturer_box_mark", items: []printing.BoxMark{{BoxUID: "BOX-1"}}, valid: true},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			if actual := validClaimPayload(test.kind, test.skuCode, test.text, test.items); actual != test.valid {
				t.Fatalf("validClaimPayload() = %v, want %v", actual, test.valid)
			}
		})
	}
}

func TestBatchContentUsesTextLabelValidation(t *testing.T) {
	template := printing.Template{LayoutStyle: "qr_left_text_right"}
	request := printing.Request{Kind: "batch_content", Text: strings.Repeat("A", 13)}
	text, message := validateLabelDisplayText(request, template)
	if text != request.Text || message == "" {
		t.Fatalf("batch content did not use text-label validation: text=%q message=%q", text, message)
	}
	if isTextLabelKind("sku_qr") || !isTextLabelKind("BATCH_CONTENT") || !isTextLabelKind("manual_text") {
		t.Fatal("unexpected text-label kind classification")
	}
}

func TestAvailableLabelTemplatesRequiresOnlineLabelPrinter(t *testing.T) {
	availability := &printerAvailability{printers: map[string]struct{}{"Label Printer": {}}}
	templates := []printing.Template{
		{ID: "other", Type: "label", Printer: "Label Printer", SortOrder: 2, WidthMillimeters: 75, HeightMillimeters: 50},
		{ID: "large", Type: "label", Printer: "Label Printer", SortOrder: 3, WidthMillimeters: 100, HeightMillimeters: 150, Orientation: "portrait"},
		{ID: "label", Type: "label", Printer: "Label Printer", SortOrder: 1, WidthMillimeters: 60, HeightMillimeters: 40},
		{ID: "shipping", Type: "shipping", Printer: "Label Printer"},
		{ID: "offline", Type: "label", Printer: "Offline Printer"},
	}
	available := availableLabelTemplates(templates, availability)
	if len(available) != 3 || available[0].ID != "label" || available[1].ID != "other" || available[2].ID != "large" {
		t.Fatalf("unexpected available templates: %+v", available)
	}
}

func TestPrintInventoryUsesConfiguredTemplateOrder(t *testing.T) {
	availability := &printerAvailability{printers: map[string]struct{}{"Label Printer": {}}}
	inventory := printInventory([]printing.Template{
		{ID: "shipping", Type: "shipping", Printer: "Label Printer", SortOrder: 4, WidthMillimeters: 100, HeightMillimeters: 150},
		{ID: "other", Type: "label", Printer: "Label Printer", SortOrder: 1, WidthMillimeters: 75, HeightMillimeters: 50},
		{ID: "large", Type: "label", Printer: "Label Printer", SortOrder: 3, WidthMillimeters: 100, HeightMillimeters: 150, Orientation: "portrait"},
		{ID: "small", TemplateNumber: "T01", Type: "label", Printer: "Label Printer", SortOrder: 2, WidthMillimeters: 60, HeightMillimeters: 40},
	}, "", availability)
	templates := inventory["templates"].([]map[string]interface{})
	if templates[0]["code"] != "other" || templates[1]["code"] != "small" || templates[2]["code"] != "large" || templates[3]["code"] != "shipping" {
		t.Fatalf("unexpected inventory order: %+v", templates)
	}
	if inventory["schema_version"] != 2 || templates[0]["sort_order"] != 1 || templates[1]["sort_order"] != 2 {
		t.Fatalf("unexpected inventory contract: %+v", inventory)
	}
	if templates[1]["number"] != "T01" {
		t.Fatalf("unexpected template number: %+v", templates[1])
	}
	if templates[1]["version"] == "1" || len(templates[1]["version"].(string)) != 12 {
		t.Fatalf("unexpected template version: %+v", templates[1])
	}
}

func TestOrderPrintTemplatesKeepsFileOrderWhenSortOrderIsMissing(t *testing.T) {
	templates := orderPrintTemplates([]printing.Template{{ID: "third"}, {ID: "first"}, {ID: "second"}})
	if templates[0].ID != "third" || templates[1].ID != "first" || templates[2].ID != "second" {
		t.Fatalf("unexpected fallback order: %+v", templates)
	}
}

func TestTemplateVersionUsesStableCrossPlatformContract(t *testing.T) {
	template := printing.Template{
		ID: "label_60x40mm", Type: "label", Printer: "Zebra", WidthMillimeters: 60, HeightMillimeters: 40,
		Orientation: "portrait", Mode: "fit", Copies: 1, SkuQRPrefix: "T", LabelQRPrefix: "BOX-",
		LayoutStyle: "qr_left_text_right", TextFontSizePoints: 18, MaxDisplayLength: 16,
	}
	if actual := templateVersion(template); actual != "ae98823ba78f" {
		t.Fatalf("templateVersion()=%q", actual)
	}
}

func TestPrintInventoryPublishesLocationCodeLayout(t *testing.T) {
	availability := &printerAvailability{printers: map[string]struct{}{"Location Printer": {}}}
	inventory := printInventory([]printing.Template{{
		ID: "location_100x150mm_landscape", Name: "Location", Type: "label", Printer: "Location Printer",
		WidthMillimeters: 100, HeightMillimeters: 150, Orientation: "landscape", Mode: "fit", Copies: 1,
		LayoutStyle: "location_code_quad_qr", TextFontSizePoints: 28, MaxDisplayLength: 12,
	}}, "", availability)
	template := inventory["templates"].([]map[string]interface{})[0]
	if template["layout_style"] != "location_code_quad_qr" || template["text_font_size_pt"] != float64(28) || template["max_display_length"] != 12 {
		t.Fatalf("unexpected location template inventory: %+v", template)
	}
	if template["label_qr_prefix"] != "" || len(template["version"].(string)) != 12 {
		t.Fatalf("unexpected location template contract: %+v", template)
	}
}

func TestPrintInventoryPublishesQuadBoxMarkLayout(t *testing.T) {
	availability := &printerAvailability{printers: map[string]struct{}{"Box Printer": {}}}
	inventory := printInventory([]printing.Template{{
		ID: "manufacturer_box_mark_quad_100x150mm", Name: "Quad", Type: "manufacturer_box_mark", Printer: "Box Printer",
		WidthMillimeters: 100, HeightMillimeters: 150, Orientation: "portrait", LayoutStyle: "box_mark_quad_qr",
	}}, "", availability)
	template := inventory["templates"].([]map[string]interface{})[0]
	if template["layout_style"] != "box_mark_quad_qr" || template["type"] != "manufacturer_box_mark" {
		t.Fatalf("unexpected quad box mark inventory: %+v", template)
	}
}

func TestRestrictedLayoutsPublishTwelveCharacterLimit(t *testing.T) {
	for _, layout := range []string{"qr_left_text_right", "location_code_quad_qr"} {
		template := printing.Template{LayoutStyle: layout, MaxDisplayLength: 16}
		if actual := normalizedMaxDisplayLength(template); actual != 12 {
			t.Fatalf("layout=%s max_display_length=%d", layout, actual)
		}
	}
	if actual := normalizedMaxDisplayLength(printing.Template{LayoutStyle: "stacked", MaxDisplayLength: 16}); actual != 16 {
		t.Fatalf("stacked max_display_length=%d", actual)
	}
}

func TestRestrictedLabelTextCountsUnicodeScalars(t *testing.T) {
	template := printing.Template{LayoutStyle: "qr_left_text_right"}
	if message := validateRestrictedLabelText(template, strings.Repeat("\U0001F600", 12)); message != "" {
		t.Fatalf("twelve Unicode scalars were rejected: %s", message)
	}
	if message := validateRestrictedLabelText(template, strings.Repeat("\U0001F600", 13)); message == "" {
		t.Fatal("thirteen Unicode scalars were accepted")
	}
}

func TestCenterLocalPrintStatus(t *testing.T) {
	tests := map[string]string{
		"queued": "queued", "printing": "printing", "completed": "succeeded",
		"failed": "failed", "cancelled": "failed", "interrupted": "unknown",
	}
	for input, expected := range tests {
		if actual := centerLocalPrintStatus(input); actual != expected {
			t.Fatalf("centerLocalPrintStatus(%q)=%q, want %q", input, actual, expected)
		}
	}
}
