package main

import (
	"net/http"
	"net/http/httptest"
	"testing"

	"fscm-edge/internal/catalog"
	"fscm-edge/internal/printing"
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

func TestAvailableLabelTemplatesRequiresOnlineLabelPrinter(t *testing.T) {
	availability := &printerAvailability{printers: map[string]struct{}{"Label Printer": {}}}
	templates := []printing.Template{
		{ID: "other", Type: "label", Printer: "Label Printer", WidthMillimeters: 75, HeightMillimeters: 50},
		{ID: "large", Type: "label", Printer: "Label Printer", WidthMillimeters: 100, HeightMillimeters: 150, Orientation: "portrait"},
		{ID: "label", Type: "label", Printer: "Label Printer", WidthMillimeters: 60, HeightMillimeters: 40},
		{ID: "shipping", Type: "shipping", Printer: "Label Printer"},
		{ID: "offline", Type: "label", Printer: "Offline Printer"},
	}
	available := availableLabelTemplates(templates, availability)
	if len(available) != 3 || available[0].ID != "label" || available[1].ID != "large" || available[2].ID != "other" {
		t.Fatalf("unexpected available templates: %+v", available)
	}
}

func TestPrintInventoryOrdersPreferredLabelTemplates(t *testing.T) {
	availability := &printerAvailability{printers: map[string]struct{}{"Label Printer": {}}}
	inventory := printInventory([]printing.Template{
		{ID: "shipping", Type: "shipping", Printer: "Label Printer", WidthMillimeters: 100, HeightMillimeters: 150},
		{ID: "other", Type: "label", Printer: "Label Printer", WidthMillimeters: 75, HeightMillimeters: 50},
		{ID: "large", Type: "label", Printer: "Label Printer", WidthMillimeters: 100, HeightMillimeters: 150, Orientation: "portrait"},
		{ID: "small", TemplateNumber: "T01", Type: "label", Printer: "Label Printer", WidthMillimeters: 60, HeightMillimeters: 40},
	}, "", availability)
	templates := inventory["templates"].([]map[string]interface{})
	if templates[0]["code"] != "small" || templates[1]["code"] != "large" || templates[2]["code"] != "other" || templates[3]["code"] != "shipping" {
		t.Fatalf("unexpected inventory order: %+v", templates)
	}
	if inventory["schema_version"] != 2 || templates[0]["layout_style"] != "stacked" || templates[0]["text_font_size_pt"] != float64(16) {
		t.Fatalf("unexpected inventory contract: %+v", inventory)
	}
	if templates[0]["number"] != "T01" {
		t.Fatalf("unexpected template number: %+v", templates[0])
	}
	if templates[0]["version"] == "1" || len(templates[0]["version"].(string)) != 12 {
		t.Fatalf("unexpected template version: %+v", templates[0])
	}
}

func TestTemplateVersionUsesStableCrossPlatformContract(t *testing.T) {
	template := printing.Template{
		ID: "label_60x40mm", Type: "label", Printer: "Zebra", WidthMillimeters: 60, HeightMillimeters: 40,
		Orientation: "portrait", Mode: "fit", Copies: 1, SkuQRPrefix: "T", LabelQRPrefix: "BOX-",
		LayoutStyle: "qr_left_text_right", TextFontSizePoints: 18, MaxDisplayLength: 16,
	}
	if actual := templateVersion(template); actual != "a0fc8a8f57fd" {
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
