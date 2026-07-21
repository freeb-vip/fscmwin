package printing

import (
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"testing"
	"time"
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

func TestCreateRejectsMoreThanFiveCopiesOfSameContent(t *testing.T) {
	t.Parallel()
	service, err := New(Config{JobsPath: filepath.Join(t.TempDir(), "edge.db")})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = service.Close() }()

	for _, request := range []Request{
		{Kind: "manual_text", Text: "A001", Copies: 6},
		{Items: []Item{{SKUCode: "A001", Quantity: 3}, {SKUCode: "A001", Quantity: 3}}},
	} {
		if _, _, err := service.Create(request); !errors.Is(err, ErrPrintCopiesExceeded) {
			t.Fatalf("expected copy protection error, got %v", err)
		}
	}
	if len(service.Jobs()) != 0 {
		t.Fatalf("blocked requests were persisted: %+v", service.Jobs())
	}
}

func TestCreateRejectsSameContentAcrossActiveJobs(t *testing.T) {
	t.Parallel()
	service, err := New(Config{JobsPath: filepath.Join(t.TempDir(), "edge.db")})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = service.Close() }()

	first, _, err := service.Create(Request{Kind: "manual_text", Text: "A001", Copies: 3})
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := service.Create(Request{Kind: "manual_text", Text: "A001", Copies: 3}); !errors.Is(err, ErrPrintCopiesExceeded) {
		t.Fatalf("expected active-job copy protection error, got %v", err)
	}
	if _, _, err := service.Create(Request{Kind: "manual_text", Text: "A002", Copies: 5}); err != nil {
		t.Fatalf("different content should be allowed: %v", err)
	}
	if _, _, err := service.Create(Request{Items: []Item{{SKUCode: "SKU-1", Quantity: 3}}}); err != nil {
		t.Fatal(err)
	}
	if _, _, err := service.Create(Request{Items: []Item{{SKUCode: "SKU-1", Quantity: 3}}}); !errors.Is(err, ErrPrintCopiesExceeded) {
		t.Fatalf("expected prefixed SKU copy protection error, got %v", err)
	}
	if _, _, err := service.SetStatus(first.ID, "completed"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := service.Create(Request{Kind: "manual_text", Text: "A001", Copies: 3}); err != nil {
		t.Fatalf("completed local job should not block a later request: %v", err)
	}
}

func TestCreateRejectsRepeatedContentWithinRemoteBatch(t *testing.T) {
	t.Parallel()
	service, err := New(Config{JobsPath: filepath.Join(t.TempDir(), "edge.db")})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = service.Close() }()
	batchID := uint(7)

	first, _, err := service.CreateRemote(Request{Kind: "batch_content", Text: "A001", Copies: 3, RemoteBatchID: &batchID}, 1, "lease-1")
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := service.SetStatus(first.ID, "completed"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := service.CreateRemote(Request{Kind: "batch_content", Text: "A001", Copies: 3, RemoteBatchID: &batchID}, 2, "lease-2"); !errors.Is(err, ErrPrintCopiesExceeded) {
		t.Fatalf("expected batch copy protection error, got %v", err)
	}
}

func TestRemoteCompletionOutboxAndClaimBackpressure(t *testing.T) {
	service, err := New(Config{JobsPath: filepath.Join(t.TempDir(), "edge.db")})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = service.Close() }()
	batchID := uint(41)
	job, _, err := service.CreateRemote(Request{
		JobID: "center-job-91", IdempotencyKey: "center-job-91", Kind: "batch_content", Text: "H01-00-01", Copies: 2,
		RemoteBatchID: &batchID, RemoteSequenceNo: 1, RemoteJobType: "batch_content", RemoteAttemptCount: 1,
	}, 91, "lease-1")
	if err != nil {
		t.Fatal(err)
	}
	if service.BeginRemoteClaim() {
		t.Fatal("queued local job did not block remote prefetch")
	}
	if _, _, err := service.SetStatusWithError(job.ID, "printing", ""); err != nil {
		t.Fatal(err)
	}
	if service.BeginRemoteClaim() {
		t.Fatal("printing local job did not block remote prefetch")
	}
	if _, _, err := service.SetStatusWithError(job.ID, "completed", ""); err != nil {
		t.Fatal(err)
	}
	completions, err := service.PendingRemoteCompletions(10)
	if err != nil || len(completions) != 1 {
		t.Fatalf("pending completions=%+v err=%v", completions, err)
	}
	completion := completions[0]
	if completion.RemoteJobID != 91 || completion.LeaseToken != "lease-1" || completion.Status != "succeeded" {
		t.Fatalf("unexpected completion: %+v", completion)
	}
	if !service.BeginRemoteClaim() {
		t.Fatal("terminal local job unexpectedly blocked the next claim")
	}
	if service.BeginRemoteClaim() {
		t.Fatal("concurrent remote claim was allowed")
	}
	service.EndRemoteClaim()
}

func TestRepeatedRemoteLeaseRefreshesReceiptWithoutDuplicateJob(t *testing.T) {
	service, err := New(Config{JobsPath: filepath.Join(t.TempDir(), "edge.db")})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = service.Close() }()
	request := Request{JobID: "center-job-92", IdempotencyKey: "center-job-92", Kind: "batch_content", Text: "BOX-009-Z", Copies: 1}
	job, _, err := service.CreateRemote(request, 92, "expired-lease")
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := service.SetStatusWithError(job.ID, "completed", ""); err != nil {
		t.Fatal(err)
	}
	if err := service.MarkRemoteCompletionSynced(92); err != nil {
		t.Fatal(err)
	}
	reused, duplicate, err := service.CreateRemote(request, 92, "fresh-lease")
	if err != nil {
		t.Fatal(err)
	}
	if !duplicate || reused.ID != job.ID || reused.RemoteLeaseToken != "fresh-lease" || len(service.Jobs()) != 1 {
		t.Fatalf("remote replay created a duplicate or kept stale lease: duplicate=%v job=%+v", duplicate, reused)
	}
	completions, err := service.PendingRemoteCompletions(10)
	if err != nil || len(completions) != 1 || completions[0].LeaseToken != "fresh-lease" {
		t.Fatalf("completion was not refreshed: %+v err=%v", completions, err)
	}
}

func TestRestartQueuesFailedRemoteCompletion(t *testing.T) {
	path := filepath.Join(t.TempDir(), "edge.db")
	service, err := New(Config{JobsPath: path})
	if err != nil {
		t.Fatal(err)
	}
	job, _, err := service.CreateRemote(Request{JobID: "center-job-93", IdempotencyKey: "center-job-93", Kind: "batch_content", Text: "A001"}, 93, "lease-restart")
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
	completions, err := restarted.PendingRemoteCompletions(10)
	if err != nil || len(completions) != 1 {
		t.Fatalf("pending completions=%+v err=%v", completions, err)
	}
	if completions[0].Status != "failed" || completions[0].ErrorCode != "EDGE_RESTART_INTERRUPTED" || completions[0].RemoteJobID != 93 {
		t.Fatalf("unexpected restart completion: %+v", completions[0])
	}
	status, err := restarted.RemoteCompletionStatus()
	if err != nil || status.Pending != 1 {
		t.Fatalf("unexpected outbox status: %+v err=%v", status, err)
	}
	if err := restarted.MarkRemoteCompletionFailed(93, errors.New("center unavailable"), time.Now().Add(-time.Second)); err != nil {
		t.Fatal(err)
	}
	retry, err := restarted.PendingRemoteCompletions(10)
	if err != nil || len(retry) != 1 || retry[0].Attempts != 1 {
		t.Fatalf("completion retry was not persisted: %+v err=%v", retry, err)
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

func TestCancelRemoteBatchStopsClaimedJobAndKeepsTerminalStateSticky(t *testing.T) {
	service, err := New(Config{JobsPath: filepath.Join(t.TempDir(), "edge.db")})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = service.Close() }()
	batchID := uint(81)
	otherBatchID := uint(82)
	job, _, err := service.CreateRemote(Request{
		JobID: "center-job-181", IdempotencyKey: "center-job-181", Kind: "batch_content", Text: "WRONG-001", RemoteBatchID: &batchID,
	}, 181, "lease-181")
	if err != nil {
		t.Fatal(err)
	}
	other, _, err := service.CreateRemote(Request{
		JobID: "center-job-182", IdempotencyKey: "center-job-182", Kind: "batch_content", Text: "RIGHT-001", RemoteBatchID: &otherBatchID,
	}, 182, "lease-182")
	if err != nil {
		t.Fatal(err)
	}

	stopped, err := service.CancelRemoteBatch(batchID)
	if err != nil || len(stopped) != 1 || stopped[0].ID != job.ID || stopped[0].Status != "cancelled" {
		t.Fatalf("unexpected stopped jobs: %+v err=%v", stopped, err)
	}
	unchanged, ok := service.Find(other.ID)
	if !ok || unchanged.Status != "queued" {
		t.Fatalf("unrelated batch job changed: %+v", unchanged)
	}
	staleStart, ok, err := service.SetStatus(job.ID, "printing")
	if err != nil || !ok || staleStart.Status != "cancelled" {
		t.Fatalf("cancelled job restarted: %+v ok=%v err=%v", staleStart, ok, err)
	}
	manualRetry, ok, err := service.SetStatus(job.ID, "queued")
	if err != nil || !ok || manualRetry.Status != "cancelled" {
		t.Fatalf("cancelled job was manually retried: %+v ok=%v err=%v", manualRetry, ok, err)
	}
	completions, err := service.PendingRemoteCompletions(10)
	if err != nil || len(completions) != 1 || completions[0].ErrorCode != "BATCH_CANCELLED_BY_OPERATOR" {
		t.Fatalf("unexpected cancellation receipt: %+v err=%v", completions, err)
	}
}

func TestPreferredRemoteBatchCannotBeOverwrittenWhileActive(t *testing.T) {
	service, err := New(Config{JobsPath: filepath.Join(t.TempDir(), "edge.db")})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = service.Close() }()

	if !service.SetPreferredRemoteBatchID(7) {
		t.Fatal("first batch was not activated")
	}
	if service.SetPreferredRemoteBatchID(8) {
		t.Fatal("a second batch replaced the active batch")
	}
	if got := service.PreferredRemoteBatchID(); got != 7 {
		t.Fatalf("active batch changed to %d", got)
	}
	service.ClearPreferredRemoteBatchID(7)
	if !service.SetPreferredRemoteBatchID(8) || service.PreferredRemoteBatchID() != 8 {
		t.Fatal("next batch could not start after the active batch cleared")
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
