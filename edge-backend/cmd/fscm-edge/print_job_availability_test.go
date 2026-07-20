package main

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"

	"fscm-edge/internal/printing"

	"github.com/gin-gonic/gin"
)

func newPrintAvailabilityTestService(t *testing.T) *printing.Service {
	t.Helper()
	templatesPath := filepath.Join(t.TempDir(), "print-templates.json")
	templates, err := json.Marshal([]printing.Template{{
		ID: "sku", Name: "SKU", Type: "label", Printer: "Zebra",
		LayoutStyle: "qr_left_text_right", SkuQRPrefix: "T", MaxDisplayLength: 16,
	}})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(templatesPath, templates, 0o600); err != nil {
		t.Fatal(err)
	}
	service, err := printing.New(printing.Config{
		DefaultPrinter: "Default Printer",
		TemplatesPath:  templatesPath,
		JobsPath:       filepath.Join(t.TempDir(), "edge.db"),
	})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = service.Close() })
	return service
}

func performPrintJobRequest(t *testing.T, service *printing.Service, availability *printerAvailability, request printing.Request) *httptest.ResponseRecorder {
	t.Helper()
	body, err := json.Marshal(request)
	if err != nil {
		t.Fatal(err)
	}
	recorder := httptest.NewRecorder()
	context, _ := gin.CreateTestContext(recorder)
	context.Request = httptest.NewRequest(http.MethodPost, "/edge/print-jobs", bytes.NewReader(body))
	context.Request.Header.Set("Content-Type", "application/json")
	createPrintJob(context, service, availability)
	return recorder
}

func TestCreatePrintJobRejectsUnavailablePrinterWithoutPersisting(t *testing.T) {
	gin.SetMode(gin.TestMode)
	service := newPrintAvailabilityTestService(t)
	availability := &printerAvailability{printers: make(map[string]struct{})}

	response := performPrintJobRequest(t, service, availability, printing.Request{
		TemplateID: "sku",
		Items:      []printing.Item{{SKUCode: "SKU-1", Quantity: 1}},
	})

	if response.Code != http.StatusConflict || !bytes.Contains(response.Body.Bytes(), []byte("PRINTER_UNAVAILABLE")) {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
	if len(service.Jobs()) != 0 {
		t.Fatalf("unavailable printer persisted jobs: %+v", service.Jobs())
	}
}

func TestCreatePrintJobAllowsAvailableTemplatePrinter(t *testing.T) {
	gin.SetMode(gin.TestMode)
	service := newPrintAvailabilityTestService(t)
	availability := &printerAvailability{printers: make(map[string]struct{})}
	availability.Set([]string{"Zebra"})

	response := performPrintJobRequest(t, service, availability, printing.Request{
		TemplateID: "sku",
		Items:      []printing.Item{{SKUCode: "SKU-1", Quantity: 1}},
	})

	if response.Code != http.StatusAccepted {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
	if len(service.Jobs()) != 1 || service.Jobs()[0].Printer != "Zebra" {
		t.Fatalf("unexpected jobs: %+v", service.Jobs())
	}
}

func TestCreatePrintJobRejectsLongHorizontalTextWithoutPersisting(t *testing.T) {
	gin.SetMode(gin.TestMode)
	service := newPrintAvailabilityTestService(t)
	availability := &printerAvailability{printers: map[string]struct{}{"Zebra": {}}}

	response := performPrintJobRequest(t, service, availability, printing.Request{
		TemplateID: "sku",
		Items:      []printing.Item{{SKUCode: "ABCDEFGHIJKL", Quantity: 1}},
	})

	if response.Code != http.StatusBadRequest || !bytes.Contains(response.Body.Bytes(), []byte("LABEL_TEXT_TOO_LONG")) {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
	if len(service.Jobs()) != 0 {
		t.Fatalf("long label persisted jobs: %+v", service.Jobs())
	}
}

func TestResolvePrintRequestPrinterUsesTemplateThenDefault(t *testing.T) {
	service := newPrintAvailabilityTestService(t)

	if actual := resolvePrintRequestPrinter(printing.Request{TemplateID: "sku", Printer: "Ignored"}, service); actual != "Zebra" {
		t.Fatalf("template printer=%q", actual)
	}
	if actual := resolvePrintRequestPrinter(printing.Request{}, service); actual != "Default Printer" {
		t.Fatalf("default printer=%q", actual)
	}
}

func TestPrinterAvailabilityBecomesReadyAfterFirstInventorySync(t *testing.T) {
	availability := &printerAvailability{printers: make(map[string]struct{})}
	if availability.Ready() {
		t.Fatal("availability was ready before the first inventory sync")
	}

	availability.Set(nil)
	if !availability.Ready() {
		t.Fatal("availability did not become ready after an empty inventory sync")
	}
}
