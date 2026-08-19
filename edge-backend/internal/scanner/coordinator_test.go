package scanner

import (
	"context"
	"encoding/json"
	"errors"
	"sync"
	"testing"
	"time"

	"fscm-edge/internal/registry"
)

type fakeCenter struct {
	mu         sync.Mutex
	nextID     uint
	bindings   []registry.ScannerBinding
	work       []registry.ScannerWorkSession
	dispatched []string
	bindResult registry.ScannerBinding
	bindErr    error
}

func (f *fakeCenter) ScannerBindings(context.Context) ([]registry.ScannerBinding, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]registry.ScannerBinding(nil), f.bindings...), nil
}

func (f *fakeCenter) ScannerWorkSessions(context.Context) ([]registry.ScannerWorkSession, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]registry.ScannerWorkSession(nil), f.work...), nil
}

func (f *fakeCenter) DispatchScanner(_ context.Context, eventType string, _ json.RawMessage) (bool, error) {
	f.mu.Lock()
	f.dispatched = append(f.dispatched, eventType)
	f.mu.Unlock()
	return false, nil
}

func (f *fakeCenter) BindScanner(context.Context, interface{}) (registry.ScannerBinding, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.bindErr != nil {
		return registry.ScannerBinding{}, f.bindErr
	}
	return f.bindResult, nil
}

func TestUserQRCodeBindingNormalizesPayloadAndReturnsResult(t *testing.T) {
	center := &fakeCenter{bindResult: registry.ScannerBinding{ID: 8, UserID: 42, BindingCode: "USER_EMP-20", DeviceFingerprint: "gun"}}
	coordinator, err := New(t.TempDir()+"/edge.db", center)
	if err != nil {
		t.Fatal(err)
	}
	defer coordinator.Close()
	coordinator.ApplyConfig(Config{Enabled: true})
	if err := coordinator.UpdateDevices([]Device{{Fingerprint: "gun", State: "online"}}); err != nil {
		t.Fatal(err)
	}
	result, err := coordinator.Input(t.Context(), Input{CaptureID: "bind-1", DeviceFingerprint: "gun", Payload: "\uFEFFUSER_EMP-20\r\n"})
	if err != nil {
		t.Fatal(err)
	}
	if result.Outcome != "bound" || result.BindingID != 8 || result.UserID != 42 {
		t.Fatalf("unexpected binding result: %+v", result)
	}
	if bindings := coordinator.Status().Bindings; len(bindings) != 1 || bindings[0].UserID != 42 {
		t.Fatalf("binding cache was not updated: %+v", bindings)
	}
}

func TestUserQRCodeBindingFailureDoesNotUpdateCache(t *testing.T) {
	center := &fakeCenter{bindErr: errors.New("binding rejected")}
	coordinator, err := New(t.TempDir()+"/edge.db", center)
	if err != nil {
		t.Fatal(err)
	}
	defer coordinator.Close()
	coordinator.ApplyConfig(Config{Enabled: true})
	if err := coordinator.UpdateDevices([]Device{{Fingerprint: "gun", State: "online"}}); err != nil {
		t.Fatal(err)
	}
	result, err := coordinator.Input(t.Context(), Input{CaptureID: "bind-fail", DeviceFingerprint: "gun", Payload: "USER_ACCOUNT_USER"})
	if err != nil {
		t.Fatal(err)
	}
	if result.Outcome != "rejected" || result.Message != "binding rejected" {
		t.Fatalf("unexpected failure result: %+v", result)
	}
	if bindings := coordinator.Status().Bindings; len(bindings) != 0 {
		t.Fatalf("failed binding polluted cache: %+v", bindings)
	}
}
func TestMultipleDevicesRemainBoundToSameUser(t *testing.T) {
	center := &fakeCenter{}
	coordinator, err := New(t.TempDir()+"/edge.db", center)
	if err != nil {
		t.Fatal(err)
	}
	defer coordinator.Close()
	coordinator.ApplyConfig(Config{Enabled: true})
	if err := coordinator.UpdateDevices([]Device{{Fingerprint: "gun-1", State: "online"}, {Fingerprint: "gun-2", State: "online"}}); err != nil {
		t.Fatal(err)
	}
	center.bindings = []registry.ScannerBinding{{ID: 1, UserID: 7, BindingCode: "USER_7", DeviceFingerprint: "gun-1"}, {ID: 2, UserID: 7, BindingCode: "USER_7", DeviceFingerprint: "gun-2"}}
	coordinator.sync(t.Context())
	status := coordinator.Status()
	if len(status.Bindings) != 2 || status.Bindings[0].UserID != 7 || status.Bindings[1].UserID != 7 {
		t.Fatalf("unexpected bindings: %+v", status.Bindings)
	}
}

func TestScanRequiresWorkSessionAndIsIdempotent(t *testing.T) {
	center := &fakeCenter{}
	coordinator, err := New(t.TempDir()+"/edge.db", center)
	if err != nil {
		t.Fatal(err)
	}
	defer coordinator.Close()
	coordinator.ApplyConfig(Config{Enabled: true})
	_ = coordinator.UpdateDevices([]Device{{Fingerprint: "gun", State: "online"}})
	center.bindings = []registry.ScannerBinding{{ID: 1, UserID: 7, BindingCode: "USER_7", DeviceFingerprint: "gun"}}
	center.work = []registry.ScannerWorkSession{{ID: 99, BindingID: 1, UserID: 7, OperationTypeCode: "PICKING", ExpiresAt: time.Now().Add(time.Hour)}}
	coordinator.sync(t.Context())
	input := Input{CaptureID: "scan-1", DeviceFingerprint: "gun", Payload: "ORDER123", ScannedAt: time.Now().UTC()}
	first, err := coordinator.Input(t.Context(), input)
	if err != nil || first.Outcome != "queued" {
		t.Fatalf("first scan: %+v err=%v", first, err)
	}
	second, err := coordinator.Input(t.Context(), input)
	if err != nil || second.EventID != first.EventID {
		t.Fatalf("idempotent scan: first=%+v second=%+v err=%v", first, second, err)
	}
}

func TestBindingAndWorkSessionSurviveRestart(t *testing.T) {
	path := t.TempDir() + "/edge.db"
	center := &fakeCenter{}
	first, err := New(path, center)
	if err != nil {
		t.Fatal(err)
	}
	first.ApplyConfig(Config{Enabled: true})
	_ = first.UpdateDevices([]Device{{Fingerprint: "gun", State: "online"}})
	center.bindings = []registry.ScannerBinding{{ID: 1, UserID: 7, BindingCode: "USER_7", DeviceFingerprint: "gun"}}
	center.work = []registry.ScannerWorkSession{{ID: 2, BindingID: 1, UserID: 7, OperationTypeCode: "PACKING", ExpiresAt: time.Now().Add(time.Hour)}}
	first.sync(t.Context())
	_ = first.Close()

	second, err := New(path, center)
	if err != nil {
		t.Fatal(err)
	}
	defer second.Close()
	second.ApplyConfig(Config{Enabled: true})
	_ = second.UpdateDevices([]Device{{Fingerprint: "gun", State: "online"}})
	result, err := second.Input(t.Context(), Input{CaptureID: "after-restart", DeviceFingerprint: "gun", Payload: "ORDER456"})
	if err != nil || result.Outcome != "queued" {
		t.Fatalf("restart scan: %+v err=%v", result, err)
	}
}
