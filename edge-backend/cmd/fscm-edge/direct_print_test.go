package main

import (
	"bytes"
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"

	"fscm-edge/internal/printing"

	"github.com/gin-gonic/gin"
)

func newDirectPrintTestService(t *testing.T) (*printing.Service, *printerAvailability) {
	t.Helper()
	templatesPath := filepath.Join(t.TempDir(), "print-templates.json")
	templates, err := json.Marshal([]printing.Template{{
		ID: "sku_60x40", Name: "SKU label", Printer: "Zebra", Type: "label",
		LayoutStyle: "qr_left_text_right", MaxDisplayLength: 16,
	}})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(templatesPath, templates, 0o600); err != nil {
		t.Fatal(err)
	}
	service, err := printing.New(printing.Config{
		TemplatesPath: templatesPath,
		JobsPath:      filepath.Join(t.TempDir(), "edge.db"),
	})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = service.Close() })
	return service, &printerAvailability{printers: map[string]struct{}{"Zebra": {}}}
}

func newQuadBoxMarkTestService(t *testing.T) (*printing.Service, *printerAvailability) {
	t.Helper()
	templatesPath := filepath.Join(t.TempDir(), "print-templates.json")
	templates, err := json.Marshal([]printing.Template{{
		ID: "manufacturer_box_mark_quad_100x150mm", Name: "Quad box mark", Type: "manufacturer_box_mark",
		Printer: "Zebra", WidthMillimeters: 100, HeightMillimeters: 150, Orientation: "portrait", LayoutStyle: "box_mark_quad_qr",
	}})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(templatesPath, templates, 0o600); err != nil {
		t.Fatal(err)
	}
	service, err := printing.New(printing.Config{TemplatesPath: templatesPath, JobsPath: filepath.Join(t.TempDir(), "edge.db")})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = service.Close() })
	return service, &printerAvailability{printers: map[string]struct{}{"Zebra": {}}}
}

func performDirectPrintRequest(t *testing.T, service *printing.Service, availability *printerAvailability, payload interface{}) *httptest.ResponseRecorder {
	t.Helper()
	body, err := json.Marshal(payload)
	if err != nil {
		t.Fatal(err)
	}
	recorder := httptest.NewRecorder()
	ginContext, _ := gin.CreateTestContext(recorder)
	ginContext.Request = httptest.NewRequest(http.MethodPost, "/edge/print-jobs/direct", bytes.NewReader(body))
	ginContext.Request.Header.Set("Content-Type", "application/json")
	ginContext.Request.Header.Set("Authorization", "Bearer mobile-token")
	createDirectPrintJob(ginContext, service, availability, func(context.Context, string) error { return nil })
	return recorder
}

func TestDirectPrintRequiresAuthorization(t *testing.T) {
	gin.SetMode(gin.TestMode)
	service, availability := newDirectPrintTestService(t)
	recorder := httptest.NewRecorder()
	ginContext, _ := gin.CreateTestContext(recorder)
	ginContext.Request = httptest.NewRequest(http.MethodPost, "/edge/print-jobs/direct", bytes.NewReader([]byte(`{}`)))
	createDirectPrintJob(ginContext, service, availability, func(context.Context, string) error { return nil })
	if recorder.Code != http.StatusUnauthorized {
		t.Fatalf("status=%d body=%s", recorder.Code, recorder.Body.String())
	}
}

func TestDirectPrintCreatesSKUJobAndDeduplicates(t *testing.T) {
	gin.SetMode(gin.TestMode)
	service, availability := newDirectPrintTestService(t)
	version := templateVersion(service.Templates()[0])
	payload := map[string]interface{}{
		"source": "mobile-app", "template_code": "sku_60x40", "copies": 2,
		"type": "sku_label", "idempotency_key": "sku-print-1",
		"payload_snapshot": map[string]interface{}{
			"type": "sku_label", "sku_id": 8, "sku_code": "SKU-8", "sku_name": "Test SKU",
			"qr_payload": "TSKU-8", "template_version": version,
		},
	}

	first := performDirectPrintRequest(t, service, availability, payload)
	second := performDirectPrintRequest(t, service, availability, payload)
	if first.Code != http.StatusAccepted || second.Code != http.StatusAccepted {
		t.Fatalf("unexpected statuses: first=%d second=%d", first.Code, second.Code)
	}
	var response struct {
		Duplicate bool         `json:"duplicate"`
		Job       printing.Job `json:"job"`
	}
	if err := json.Unmarshal(second.Body.Bytes(), &response); err != nil {
		t.Fatal(err)
	}
	if !response.Duplicate || response.Job.Kind != "sku_qr" || response.Job.IdempotencyKey != "sku-print-1" {
		t.Fatalf("unexpected duplicate SKU response: %+v", response)
	}
	if len(response.Job.Items) != 1 || response.Job.Items[0].SKUCode != "SKU-8" || response.Job.Items[0].QRCodeContent != "TSKU-8" || response.Job.Items[0].Quantity != 2 {
		t.Fatalf("unexpected SKU print item: %+v", response.Job.Items)
	}
	if len(service.Jobs()) != 1 {
		t.Fatalf("duplicate request created multiple jobs: %+v", service.Jobs())
	}
	pending, err := service.PendingAudits(10)
	if err != nil || len(pending) != 1 || !bytes.Contains(pending[0].PayloadSnapshot, []byte(`"template_version":"`+version+`"`)) {
		t.Fatalf("original payload snapshot was not retained for audit: jobs=%+v err=%v", pending, err)
	}
}

func TestDirectPrintCreatesCustomLabelJob(t *testing.T) {
	gin.SetMode(gin.TestMode)
	service, availability := newDirectPrintTestService(t)
	version := templateVersion(service.Templates()[0])
	recorder := performDirectPrintRequest(t, service, availability, map[string]interface{}{
		"source": "mobile-app", "template_code": "sku_60x40", "copies": 3,
		"type": "custom_label", "idempotency_key": "custom-print-1",
		"payload_snapshot": map[string]interface{}{
			"type": "custom_label", "label_content": "BOX-001", "text": "BOX-001",
			"qr_payload": "Q-BOX-001", "template_code": "sku_60x40", "template_version": version,
		},
	})
	if recorder.Code != http.StatusAccepted {
		t.Fatalf("status=%d body=%s", recorder.Code, recorder.Body.String())
	}
	job := service.Jobs()[0]
	if job.Kind != "custom_label" || job.Text != "BOX-001" || job.Copies != 3 || len(job.Items) != 1 || job.Items[0].QRCodeContent != "Q-BOX-001" {
		t.Fatalf("unexpected custom label job: %+v", job)
	}
}

func TestDirectPrintRejectsLongHorizontalTextWithoutPersisting(t *testing.T) {
	gin.SetMode(gin.TestMode)
	service, availability := newDirectPrintTestService(t)
	version := templateVersion(service.Templates()[0])
	recorder := performDirectPrintRequest(t, service, availability, map[string]interface{}{
		"source": "mobile-app", "template_code": "sku_60x40", "copies": 1,
		"type": "sku_label", "idempotency_key": "long-label",
		"payload_snapshot": map[string]interface{}{
			"type": "sku_label", "sku_code": "ABCDEFGHIJKLM",
			"qr_payload": "ABCDEFGHIJKLM", "template_version": version,
		},
	})

	if recorder.Code != http.StatusBadRequest || !bytes.Contains(recorder.Body.Bytes(), []byte("LABEL_TEXT_TOO_LONG")) {
		t.Fatalf("status=%d body=%s", recorder.Code, recorder.Body.String())
	}
	if len(service.Jobs()) != 0 {
		t.Fatalf("long direct label persisted jobs: %+v", service.Jobs())
	}
}

func TestWebLabelPrintRejectsLongLocationTextWithoutPersisting(t *testing.T) {
	gin.SetMode(gin.TestMode)
	service, availability := newDirectPrintTestService(t)
	template := service.Templates()[0]
	template.LayoutStyle = "location_code_quad_qr"
	if err := service.SaveTemplate(template); err != nil {
		t.Fatal(err)
	}
	body, err := json.Marshal(map[string]interface{}{
		"template_id": template.ID,
		"text":        "ABCDEFGHIJKLM",
		"copies":      1,
	})
	if err != nil {
		t.Fatal(err)
	}
	recorder := httptest.NewRecorder()
	ginContext, _ := gin.CreateTestContext(recorder)
	ginContext.Request = httptest.NewRequest(http.MethodPost, "/edge/web/label-jobs", bytes.NewReader(body))
	ginContext.Request.Header.Set("Content-Type", "application/json")
	createManualTextJob(ginContext, service, availability)

	if recorder.Code != http.StatusBadRequest || !bytes.Contains(recorder.Body.Bytes(), []byte("LABEL_TEXT_TOO_LONG")) {
		t.Fatalf("status=%d body=%s", recorder.Code, recorder.Body.String())
	}
	if len(service.Jobs()) != 0 {
		t.Fatalf("long web label persisted jobs: %+v", service.Jobs())
	}
}

func TestDirectPrintRejectsChangedTemplateVersion(t *testing.T) {
	gin.SetMode(gin.TestMode)
	service, availability := newDirectPrintTestService(t)
	recorder := performDirectPrintRequest(t, service, availability, map[string]interface{}{
		"template_code": "sku_60x40", "copies": 1, "type": "sku_label", "idempotency_key": "stale-template",
		"payload_snapshot": map[string]interface{}{
			"type": "sku_label", "sku_code": "SKU-1", "qr_payload": "TSKU-1", "template_version": "stale",
		},
	})
	if recorder.Code != http.StatusConflict || !bytes.Contains(recorder.Body.Bytes(), []byte("LABEL_TEMPLATE_CHANGED")) {
		t.Fatalf("status=%d body=%s", recorder.Code, recorder.Body.String())
	}
}

func TestDirectPrintRejectsInvalidContract(t *testing.T) {
	gin.SetMode(gin.TestMode)
	base := map[string]interface{}{
		"template_code": "sku_60x40", "copies": 1, "type": "sku_label", "idempotency_key": "print-1",
		"payload_snapshot": map[string]interface{}{"type": "sku_label", "sku_code": "SKU-1", "qr_payload": "TSKU-1"},
	}
	for _, test := range []struct {
		name   string
		mutate func(map[string]interface{})
	}{
		{name: "missing template", mutate: func(value map[string]interface{}) { delete(value, "template_code") }},
		{name: "missing idempotency", mutate: func(value map[string]interface{}) { delete(value, "idempotency_key") }},
		{name: "zero copies", mutate: func(value map[string]interface{}) { value["copies"] = 0 }},
		{name: "too many copies", mutate: func(value map[string]interface{}) { value["copies"] = 6 }},
		{name: "missing snapshot", mutate: func(value map[string]interface{}) { delete(value, "payload_snapshot") }},
	} {
		t.Run(test.name, func(t *testing.T) {
			service, availability := newDirectPrintTestService(t)
			value := make(map[string]interface{}, len(base))
			for key, item := range base {
				value[key] = item
			}
			test.mutate(value)
			recorder := performDirectPrintRequest(t, service, availability, value)
			if recorder.Code != http.StatusBadRequest {
				t.Fatalf("status=%d body=%s", recorder.Code, recorder.Body.String())
			}
		})
	}
}

func TestDirectPrintRejectsUnavailableTemplate(t *testing.T) {
	gin.SetMode(gin.TestMode)
	service, availability := newDirectPrintTestService(t)
	recorder := performDirectPrintRequest(t, service, availability, map[string]interface{}{
		"template_code": "outdated", "copies": 1, "type": "sku_label", "idempotency_key": "print-1",
		"payload_snapshot": map[string]interface{}{"type": "sku_label", "sku_code": "SKU-1", "qr_payload": "TSKU-1"},
	})
	if recorder.Code != http.StatusConflict || !bytes.Contains(recorder.Body.Bytes(), []byte("LABEL_TEMPLATE_UNAVAILABLE")) {
		t.Fatalf("status=%d body=%s", recorder.Code, recorder.Body.String())
	}
}

func TestDirectPrintRejectsIdempotencyKeyWithDifferentPayload(t *testing.T) {
	gin.SetMode(gin.TestMode)
	service, availability := newDirectPrintTestService(t)
	base := map[string]interface{}{
		"source": "mobile-app", "template_code": "sku_60x40", "copies": 1,
		"type": "sku_label", "idempotency_key": "same-key",
		"payload_snapshot": map[string]interface{}{"type": "sku_label", "sku_code": "SKU-1", "qr_payload": "TSKU-1"},
	}
	if response := performDirectPrintRequest(t, service, availability, base); response.Code != http.StatusAccepted {
		t.Fatalf("first status=%d body=%s", response.Code, response.Body.String())
	}
	base["payload_snapshot"] = map[string]interface{}{"type": "sku_label", "sku_code": "SKU-2", "qr_payload": "TSKU-2"}
	response := performDirectPrintRequest(t, service, availability, base)
	if response.Code != http.StatusConflict || !bytes.Contains(response.Body.Bytes(), []byte("IDEMPOTENCY_CONFLICT")) {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
}

func TestDirectPrintCreatesManufacturerBoxMarkJob(t *testing.T) {
	gin.SetMode(gin.TestMode)
	templatesPath := filepath.Join(t.TempDir(), "print-templates.json")
	templates, _ := json.Marshal([]printing.Template{{
		ID: "manufacturer_box_mark_100x150mm", Name: "Box mark", Type: "manufacturer_box_mark",
		Printer: "Zebra", WidthMillimeters: 100, HeightMillimeters: 150, Orientation: "portrait",
	}})
	if err := os.WriteFile(templatesPath, templates, 0o600); err != nil {
		t.Fatal(err)
	}
	service, err := printing.New(printing.Config{TemplatesPath: templatesPath, JobsPath: filepath.Join(t.TempDir(), "edge.db")})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = service.Close() })
	availability := &printerAvailability{printers: map[string]struct{}{"Zebra": {}}}
	response := performDirectPrintRequest(t, service, availability, map[string]interface{}{
		"source": "mobile-app", "template_code": "manufacturer_box_mark_100x150mm", "copies": 1,
		"type": "manufacturer_box_mark", "idempotency_key": "box-mark-1",
		"payload_snapshot": map[string]interface{}{
			"kind": "manufacturer_box_mark", "document_version": "manufacturer_box_mark.v1",
			"items": []map[string]interface{}{{"box_plan_id": 7, "box_uid": "BOX-7", "box_qr_payload": "BOX-7"}},
		},
	})
	if response.Code != http.StatusAccepted {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
	job := service.Jobs()[0]
	if job.Kind != "manufacturer_box_mark" || len(job.BoxMarks) != 1 || job.BoxMarks[0].BoxPlanID != 7 {
		t.Fatalf("unexpected box mark job: %+v", job)
	}
}

func TestDirectPrintCreatesQuadBoxMarkV2Job(t *testing.T) {
	gin.SetMode(gin.TestMode)
	service, availability := newQuadBoxMarkTestService(t)
	response := performDirectPrintRequest(t, service, availability, map[string]interface{}{
		"source": "mobile-app", "template_code": "manufacturer_box_mark_quad_100x150mm", "copies": 1,
		"type": "manufacturer_box_mark", "idempotency_key": "quad-box-mark-1",
		"payload_snapshot": map[string]interface{}{
			"kind": "manufacturer_box_mark", "document_version": "manufacturer_box_mark.v2",
			"items": []map[string]interface{}{{
				"box_plan_id": 7, "shop": "BX-7", "box_qr_payload": "BOX-7",
				"sku_code": "SKU-7", "sku_name": "Test SKU", "qty_per_box": 24, "sku_qr_payload": "SKU-7",
			}},
		},
	})
	if response.Code != http.StatusAccepted {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
	job := service.Jobs()[0]
	if len(job.BoxMarks) != 1 || job.BoxMarks[0].SKUName != "Test SKU" || job.BoxMarks[0].QtyPerBox != 24 {
		t.Fatalf("unexpected quad box mark job: %+v", job)
	}
}

func TestDirectPrintRejectsIncompleteQuadBoxMarkWithoutPersisting(t *testing.T) {
	gin.SetMode(gin.TestMode)
	service, availability := newQuadBoxMarkTestService(t)
	response := performDirectPrintRequest(t, service, availability, map[string]interface{}{
		"source": "mobile-app", "template_code": "manufacturer_box_mark_quad_100x150mm", "copies": 1,
		"type": "manufacturer_box_mark", "idempotency_key": "quad-box-mark-invalid",
		"payload_snapshot": map[string]interface{}{
			"kind": "manufacturer_box_mark", "document_version": "manufacturer_box_mark.v2",
			"items": []map[string]interface{}{{
				"shop": "BX-7", "box_qr_payload": "BOX-7", "sku_code": "SKU-7", "sku_name": "Test SKU",
			}},
		},
	})
	if response.Code != http.StatusBadRequest || !bytes.Contains(response.Body.Bytes(), []byte("INVALID_BOX_MARK_SKU_CONTENT")) {
		t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
	}
	if len(service.Jobs()) != 0 {
		t.Fatalf("invalid quad box mark persisted jobs: %+v", service.Jobs())
	}
}
