package scanner

import (
	"context"
	"testing"
	"time"

	"fscm-edge/internal/registry"
)

type commandCenter struct {
	*fakeCenter
	startPayload map[string]interface{}
	endPayload   map[string]interface{}
	startResult  registry.ScannerWorkSession
}

func (c *commandCenter) StartScannerWorkSession(_ context.Context, payload interface{}) (registry.ScannerWorkSession, error) {
	c.startPayload, _ = payload.(map[string]interface{})
	return c.startResult, nil
}

func (c *commandCenter) EndScannerWorkSession(_ context.Context, payload interface{}) error {
	c.endPayload, _ = payload.(map[string]interface{})
	return nil
}

func TestParseScannerCommand(t *testing.T) {
	tests := []struct {
		payload string
		kind    scannerCommandKind
		code    string
	}{
		{payload: " FSCM_JOB:packing ", kind: scannerCommandStart, code: "PACKING"},
		{payload: "fscm_job:END", kind: scannerCommandEnd},
		{payload: "ORDER-100", kind: scannerCommandOrder},
		{payload: "FSCM_JOB:", kind: scannerCommandOrder},
	}
	for _, test := range tests {
		command := parseScannerCommand(test.payload)
		if command.kind != test.kind || command.operationCode != test.code {
			t.Fatalf("%q parsed as %+v", test.payload, command)
		}
	}
}

func TestJobQRCodeStartsAndEndsScannerWork(t *testing.T) {
	base := &fakeCenter{
		bindings: []registry.ScannerBinding{{
			ID: 7, UserID: 42, BindingCode: "USER_42", DeviceFingerprint: "gun",
		}},
	}
	center := &commandCenter{
		fakeCenter: base,
		startResult: registry.ScannerWorkSession{
			ID: 99, BindingID: 7, UserID: 42, OperationTypeCode: "PACKING",
			OperationTypeName: "Packing", ExpiresAt: time.Now().Add(time.Hour),
		},
	}
	coordinator, err := New(t.TempDir()+"/edge.db", center)
	if err != nil {
		t.Fatal(err)
	}
	defer coordinator.Close()
	coordinator.ApplyConfig(Config{Enabled: true})
	if err := coordinator.UpdateDevices([]Device{{Fingerprint: "gun", State: "online"}}); err != nil {
		t.Fatal(err)
	}
	coordinator.sync(t.Context())

	start, err := coordinator.Input(t.Context(), Input{
		CaptureID: "start-1", DeviceFingerprint: "gun", Payload: "fscm_job:packing",
	})
	if err != nil || start.Outcome != "work_started" {
		t.Fatalf("start result=%+v err=%v", start, err)
	}
	if center.startPayload["operation_type_code"] != "PACKING" || center.startPayload["binding_id"] != uint(7) {
		t.Fatalf("unexpected start payload: %+v", center.startPayload)
	}

	order, err := coordinator.Input(t.Context(), Input{
		CaptureID: "order-1", DeviceFingerprint: "gun", Payload: "ORDER-100",
	})
	if err != nil || order.Outcome != "queued" {
		t.Fatalf("order result=%+v err=%v", order, err)
	}

	end, err := coordinator.Input(t.Context(), Input{
		CaptureID: "end-1", DeviceFingerprint: "gun", Payload: "FSCM_JOB:END",
	})
	if err != nil || end.Outcome != "work_ended" {
		t.Fatalf("end result=%+v err=%v", end, err)
	}
	if center.endPayload["end_reason"] != "operator_scan" || center.endPayload["binding_id"] != uint(7) {
		t.Fatalf("unexpected end payload: %+v", center.endPayload)
	}

	afterEnd, err := coordinator.Input(t.Context(), Input{
		CaptureID: "order-2", DeviceFingerprint: "gun", Payload: "ORDER-101",
	})
	if err != nil || afterEnd.Outcome != "rejected" {
		t.Fatalf("after-end result=%+v err=%v", afterEnd, err)
	}
}

var _ Center = (*commandCenter)(nil)
var _ workSessionCommander = (*commandCenter)(nil)
