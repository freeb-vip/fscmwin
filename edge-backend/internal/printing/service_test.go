package printing

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

func TestCreateUsesTemplatePrefixAndIdempotency(t *testing.T) {
	t.Parallel()
	templatesPath := filepath.Join(t.TempDir(), "print-templates.json")
	templates := []Template{{ID: "label", Name: "Label", Printer: "Zebra", SkuQRPrefix: "BOX-"}}
	data, err := json.Marshal(templates)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(templatesPath, data, 0o600); err != nil {
		t.Fatal(err)
	}

	service, err := New(Config{TemplatesPath: templatesPath, JobsPath: filepath.Join(t.TempDir(), "edge.db"), QRCodePrefix: "T"})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = service.Close() }()
	request := Request{
		TemplateID:     "label",
		JobID:          "job-1",
		IdempotencyKey: "same-request",
		Items:          []Item{{SKUID: 7, SKUCode: "ABC001", Quantity: 2}},
	}
	first, duplicate, err := service.Create(request)
	if err != nil {
		t.Fatal(err)
	}
	if duplicate {
		t.Fatal("first request was unexpectedly marked duplicate")
	}
	if first.Items[0].QRCodeContent != "BOX-ABC001" {
		t.Fatalf("unexpected QR payload: %q", first.Items[0].QRCodeContent)
	}

	second, duplicate, err := service.Create(request)
	if err != nil {
		t.Fatal(err)
	}
	if !duplicate || second.ID != first.ID {
		t.Fatalf("idempotency did not return original job: duplicate=%v id=%q", duplicate, second.ID)
	}
}

func TestSetStatusWithErrorTracksLifecycle(t *testing.T) {
	service, err := New(Config{JobsPath: filepath.Join(t.TempDir(), "edge.db")})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = service.Close() }()
	job, _, err := service.Create(Request{JobID: "job-2", Items: []Item{{SKUCode: "ABC001"}}})
	if err != nil {
		t.Fatal(err)
	}
	started, ok, err := service.SetStatusWithError(job.ID, "printing", "")
	if err != nil {
		t.Fatal(err)
	}
	if !ok || started.StartedAt == nil || started.Status != "printing" {
		t.Fatalf("printing transition was not recorded: %+v", started)
	}
	failed, ok, err := service.SetStatusWithError(job.ID, "failed", "printer offline")
	if err != nil {
		t.Fatal(err)
	}
	if !ok || failed.FinishedAt == nil || failed.Error != "printer offline" || failed.Status != "failed" {
		t.Fatalf("failure transition was not recorded: %+v", failed)
	}
}

func TestCreatePreservesCenterPayloadAndManualText(t *testing.T) {
	service, err := New(Config{JobsPath: filepath.Join(t.TempDir(), "edge.db")})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = service.Close() }()
	job, _, err := service.CreateRemote(Request{
		JobID:          "manual-1",
		IdempotencyKey: "manual-1",
		Kind:           "manual_text",
		Text:           "WAREHOUSE-A01",
		Copies:         3,
	}, 88, "lease-token")
	if err != nil {
		t.Fatal(err)
	}
	if job.Text != "WAREHOUSE-A01" || job.Copies != 3 || len(job.Items) != 0 {
		t.Fatalf("manual label was not preserved: %+v", job)
	}

	skuJob, _, err := service.Create(Request{
		JobID: "sku-1",
		Items: []Item{{
			SKUCode:       "ABC001",
			QRCodeContent: "CENTER-ABC001",
		}},
	})
	if err != nil {
		t.Fatal(err)
	}
	if skuJob.Items[0].QRCodeContent != "CENTER-ABC001" {
		t.Fatalf("center QR payload was overwritten: %q", skuJob.Items[0].QRCodeContent)
	}
}

func TestRestartInterruptsPrintingAndKeepsAudit(t *testing.T) {
	path := filepath.Join(t.TempDir(), "edge.db")
	service, err := New(Config{JobsPath: path})
	if err != nil {
		t.Fatal(err)
	}
	job, _, err := service.Create(Request{JobID: "restart-job", Source: "web", Kind: "manual_text", Text: "A"})
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := service.SetStatus(job.ID, "printing"); err != nil {
		t.Fatal(err)
	}
	if err := service.Close(); err != nil {
		t.Fatal(err)
	}

	restarted, err := New(Config{JobsPath: path})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = restarted.Close() }()
	loaded, ok := restarted.Find(job.ID)
	if !ok || loaded.Status != "interrupted" {
		t.Fatalf("expected interrupted job after restart, got %+v", loaded)
	}
	pending, err := restarted.PendingAudits(10)
	if err != nil || len(pending) != 1 || pending[0].ID != job.ID {
		t.Fatalf("expected pending local audit, jobs=%+v err=%v", pending, err)
	}
}
