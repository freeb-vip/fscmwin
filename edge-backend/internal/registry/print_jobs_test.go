package registry

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestClaimPrintJobReadsBatchMetadata(t *testing.T) {
	availableAt := time.Now().UTC().Truncate(time.Second)
	center := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(map[string]interface{}{"code": 0, "data": map[string]interface{}{
			"job": map[string]interface{}{
				"id": 9, "batch_id": 3, "sequence_no": 2, "available_at": availableAt, "job_type": "batch_content", "attempt_count": 1,
				"template_code": "label", "printer_name": "Zebra", "copies": 2, "payload_snapshot": map[string]interface{}{"kind": "batch_content", "text": "A002"},
			}, "lease_token": "lease-9",
		}})
	}))
	defer center.Close()

	client := New(Config{CenterURL: center.URL, NodeID: "edge-1"})
	job, err := client.ClaimPrintJob(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if job == nil || job.BatchID == nil || *job.BatchID != 3 || job.SequenceNo != 2 || job.JobType != "batch_content" || job.AttemptCount != 1 || job.LeaseToken != "lease-9" {
		t.Fatalf("unexpected claimed job: %+v", job)
	}
	if job.AvailableAt == nil || !job.AvailableAt.Equal(availableAt) {
		t.Fatalf("available_at was not decoded: %+v", job.AvailableAt)
	}
}

func TestClaimPrintJobKeepsLegacyResponseCompatible(t *testing.T) {
	center := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"code":0,"data":{"job":{"id":7,"template_code":"label","printer_name":"Zebra","copies":1,"payload_snapshot":{"sku_code":"SKU-1"}},"lease_token":"legacy-lease"}}`))
	}))
	defer center.Close()

	job, err := New(Config{CenterURL: center.URL, NodeID: "edge-1"}).ClaimPrintJob(context.Background())
	if err != nil || job == nil || job.ID != 7 || job.BatchID != nil || job.SequenceNo != 0 || job.LeaseToken != "legacy-lease" {
		t.Fatalf("legacy claim failed: job=%+v err=%v", job, err)
	}
}

func TestCompletePrintJobReportsConflictAsInvalidLease(t *testing.T) {
	center := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusConflict)
	}))
	defer center.Close()

	err := New(Config{CenterURL: center.URL, NodeID: "edge-1"}).CompletePrintJobWithCode(context.Background(), 4, "expired", "failed", "PRINT_FAILED", "offline", nil)
	if !registryErrorStatus(err, http.StatusConflict) || !IsLeaseInvalid(err) {
		t.Fatalf("expected typed lease conflict, got %v", err)
	}
}

func registryErrorStatus(err error, status int) bool {
	var responseErr *ResponseError
	return errors.As(err, &responseErr) && responseErr.StatusCode == status
}
