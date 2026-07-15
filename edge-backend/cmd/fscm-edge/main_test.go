package main

import (
	"net/http"
	"net/http/httptest"
	"testing"

	"fscm-edge/internal/printing"
	"github.com/gin-gonic/gin"
)

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
		{ID: "label", Type: "label", Printer: "Label Printer"},
		{ID: "shipping", Type: "shipping", Printer: "Label Printer"},
		{ID: "offline", Type: "label", Printer: "Offline Printer"},
	}
	available := availableLabelTemplates(templates, availability)
	if len(available) != 1 || available[0].ID != "label" {
		t.Fatalf("unexpected available templates: %+v", available)
	}
}
