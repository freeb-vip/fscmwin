package scanner

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"sync"
	"time"

	"fscm-edge/internal/registry"

	"github.com/google/uuid"
)

type Center interface {
	ScannerBindings(context.Context) ([]registry.ScannerBinding, error)
	ScannerWorkSessions(context.Context) ([]registry.ScannerWorkSession, error)
	DispatchScanner(context.Context, string, json.RawMessage) (bool, error)
}
type binder interface {
	BindScanner(context.Context, interface{}) (registry.ScannerBinding, error)
}

type workSessionCommander interface {
	StartScannerWorkSession(context.Context, interface{}) (registry.ScannerWorkSession, error)
	EndScannerWorkSession(context.Context, interface{}) error
}

type Config struct {
	Enabled              bool   `json:"enabled"`
	UserPrefix           string `json:"user_prefix"`
	UnbindCode           string `json:"unbind_code"`
	ScanTimeoutMS        int    `json:"scan_timeout_ms"`
	UnbindConfirmSeconds int    `json:"unbind_confirm_seconds"`
	MaxScanLength        int    `json:"max_scan_length"`
}

type Device struct {
	Fingerprint        string `json:"fingerprint"`
	Transport          string `json:"transport"`
	SystemPath         string `json:"system_path,omitempty"`
	VendorID           string `json:"vendor_id,omitempty"`
	ProductID          string `json:"product_id,omitempty"`
	Name               string `json:"name"`
	State              string `json:"state"`
	IdentityConfidence string `json:"identity_confidence"`
}

type Binding struct {
	ID                uint   `json:"id"`
	UserID            uint   `json:"user_id"`
	BindingCode       string `json:"binding_code"`
	DeviceFingerprint string `json:"device_fingerprint"`
}

type WorkSession struct {
	ID                uint      `json:"id"`
	BindingID         uint      `json:"binding_id"`
	UserID            uint      `json:"user_id"`
	OperationTypeCode string    `json:"operation_type_code"`
	OperationTypeName string    `json:"operation_type_name"`
	ExpiresAt         time.Time `json:"expires_at"`
}

type Input struct {
	CaptureID         string    `json:"capture_id"`
	DeviceFingerprint string    `json:"device_fingerprint"`
	Payload           string    `json:"payload"`
	ScannedAt         time.Time `json:"scanned_at"`
}

type InputResult struct {
	Outcome   string `json:"outcome"`
	Message   string `json:"message,omitempty"`
	BindingID uint   `json:"binding_id,omitempty"`
	UserID    uint   `json:"user_id,omitempty"`
	EventID   string `json:"event_id,omitempty"`
}

type Status struct {
	Enabled     bool      `json:"enabled"`
	Health      string    `json:"health"`
	Devices     []Device  `json:"devices"`
	Bindings    []Binding `json:"bindings"`
	Pending     int       `json:"pending"`
	DeadLetters int       `json:"dead_letters"`
	LastError   string    `json:"last_error,omitempty"`
}

type pendingUnbind struct {
	ID string
	At time.Time
}

type Coordinator struct {
	mu        sync.RWMutex
	cfg       Config
	center    Center
	store     *Store
	devices   map[string]Device
	bindings  map[string]Binding
	work      map[uint]WorkSession
	pending   map[string]pendingUnbind
	lastError string
}

func New(path string, center Center) (*Coordinator, error) {
	store, err := OpenStore(path)
	if err != nil {
		return nil, err
	}
	bindings, err := store.loadBindings()
	if err != nil {
		_ = store.Close()
		return nil, err
	}
	work, err := store.loadWorkSessions()
	if err != nil {
		_ = store.Close()
		return nil, err
	}
	return &Coordinator{cfg: normalizeConfig(Config{}), center: center, store: store, devices: map[string]Device{}, bindings: bindings, work: work, pending: map[string]pendingUnbind{}}, nil
}

func (c *Coordinator) Close() error { return c.store.Close() }

func normalizeConfig(value Config) Config {
	value.UserPrefix = strings.ToUpper(strings.TrimSpace(value.UserPrefix))
	if value.UserPrefix == "" {
		value.UserPrefix = "USER_"
	}
	value.UnbindCode = strings.ToUpper(strings.TrimSpace(value.UnbindCode))
	if value.UnbindCode == "" {
		value.UnbindCode = "UNBIND"
	}
	if value.ScanTimeoutMS <= 0 {
		value.ScanTimeoutMS = 2000
	}
	if value.UnbindConfirmSeconds <= 0 {
		value.UnbindConfirmSeconds = 10
	}
	if value.MaxScanLength <= 0 {
		value.MaxScanLength = 2048
	}
	return value
}

func (c *Coordinator) ApplyConfig(value Config) Config {
	value = normalizeConfig(value)
	c.mu.Lock()
	c.cfg = value
	c.mu.Unlock()
	return value
}

func (c *Coordinator) UpdateDevices(items []Device) error {
	updated := make(map[string]Device, len(items))
	payload := make([]map[string]string, 0, len(items))
	for _, item := range items {
		item.Fingerprint = strings.TrimSpace(item.Fingerprint)
		if item.Fingerprint == "" {
			return fmt.Errorf("device fingerprint is required")
		}
		if item.State == "" {
			item.State = "online"
		}
		if item.IdentityConfidence == "" {
			item.IdentityConfidence = "stable"
		}
		if strings.Contains(item.Name, "?") || strings.TrimSpace(item.Name) == "" {
			item.Name = fmt.Sprintf("HID scanner (VID %s / PID %s)", strings.ToUpper(item.VendorID), strings.ToUpper(item.ProductID))
		}
		updated[item.Fingerprint] = item
		payload = append(payload, map[string]string{"fingerprint": item.Fingerprint, "event_path": item.SystemPath, "vendor_id": item.VendorID, "product_id": item.ProductID, "name": item.Name, "state": item.State, "identity_confidence": item.IdentityConfidence})
	}
	c.mu.Lock()
	c.devices = updated
	c.mu.Unlock()
	return c.store.enqueue(uuid.NewString(), "scanner.device_sync", map[string]interface{}{"devices": payload})
}

func (c *Coordinator) Input(ctx context.Context, input Input) (InputResult, error) {
	input.CaptureID = strings.TrimSpace(input.CaptureID)
	input.DeviceFingerprint = strings.TrimSpace(input.DeviceFingerprint)
	input.Payload = normalizeScanPayload(input.Payload)
	if input.CaptureID == "" || input.DeviceFingerprint == "" || input.Payload == "" {
		return InputResult{}, fmt.Errorf("capture_id, device_fingerprint and payload are required")
	}
	if input.ScannedAt.IsZero() {
		input.ScannedAt = time.Now().UTC()
	}
	if result, ok, err := c.store.captureResult(input.CaptureID); err != nil || ok {
		return result, err
	}
	c.mu.RLock()
	cfg := c.cfg
	device, deviceOK := c.devices[input.DeviceFingerprint]
	binding, bound := c.bindings[input.DeviceFingerprint]
	c.mu.RUnlock()
	result := InputResult{}
	command := parseScannerCommand(input.Payload)
	switch {
	case !cfg.Enabled:
		result = InputResult{Outcome: "rejected", Message: "scanner is disabled"}
	case !deviceOK || strings.EqualFold(device.State, "offline"):
		result = InputResult{Outcome: "rejected", Message: "scanner device is unavailable"}
	case len(input.Payload) > cfg.MaxScanLength:
		result = InputResult{Outcome: "rejected", Message: "scan exceeds maximum length"}
	case strings.HasPrefix(strings.ToUpper(input.Payload), cfg.UserPrefix):
		service, ok := c.center.(binder)
		if !ok {
			result = InputResult{Outcome: "rejected", Message: "scanner binding service is unavailable"}
			break
		}
		binding, err := service.BindScanner(ctx, map[string]interface{}{"command_id": uuid.NewString(), "device_fingerprint": input.DeviceFingerprint, "binding_code": input.Payload, "scanned_at": input.ScannedAt.UTC(), "event_path": device.SystemPath, "vendor_id": device.VendorID, "product_id": device.ProductID, "device_name": device.Name})
		if err != nil {
			result = InputResult{Outcome: "rejected", Message: err.Error()}
			break
		}
		result = InputResult{Outcome: "bound", BindingID: binding.ID, UserID: binding.UserID}
		c.mu.Lock()
		c.bindings[input.DeviceFingerprint] = Binding{ID: binding.ID, UserID: binding.UserID, BindingCode: binding.BindingCode, DeviceFingerprint: binding.DeviceFingerprint}
		_ = c.store.replaceBindings(c.bindings)
		c.mu.Unlock()
	case !bound:
		result = InputResult{Outcome: "rejected", Message: "scanner is not bound"}
	case strings.EqualFold(input.Payload, cfg.UnbindCode):
		result = InputResult{Outcome: "rejected", Message: "scanner unbinding is managed by the web console"}
	case command.kind == scannerCommandStart:
		service, ok := c.center.(workSessionCommander)
		if !ok {
			result = InputResult{Outcome: "rejected", Message: "scanner work session service is unavailable", BindingID: binding.ID, UserID: binding.UserID}
			break
		}
		session, err := service.StartScannerWorkSession(ctx, map[string]interface{}{
			"command_id": uuid.NewString(), "binding_id": binding.ID, "device_fingerprint": input.DeviceFingerprint,
			"operation_type_code": command.operationCode, "scanned_at": input.ScannedAt.UTC(),
		})
		if err != nil {
			result = InputResult{Outcome: "rejected", Message: err.Error(), BindingID: binding.ID, UserID: binding.UserID}
			break
		}
		c.mu.Lock()
		c.work[binding.ID] = WorkSession{
			ID: session.ID, BindingID: session.BindingID, UserID: session.UserID,
			OperationTypeCode: session.OperationTypeCode, OperationTypeName: session.OperationTypeName, ExpiresAt: session.ExpiresAt,
		}
		_ = c.store.replaceWorkSessions(c.work)
		c.mu.Unlock()
		result = InputResult{Outcome: "work_started", BindingID: binding.ID, UserID: binding.UserID}
	case command.kind == scannerCommandEnd:
		service, ok := c.center.(workSessionCommander)
		if !ok {
			result = InputResult{Outcome: "rejected", Message: "scanner work session service is unavailable", BindingID: binding.ID, UserID: binding.UserID}
			break
		}
		err := service.EndScannerWorkSession(ctx, map[string]interface{}{
			"command_id": uuid.NewString(), "binding_id": binding.ID, "device_fingerprint": input.DeviceFingerprint,
			"end_reason": "operator_scan", "scanned_at": input.ScannedAt.UTC(),
		})
		if err != nil {
			result = InputResult{Outcome: "rejected", Message: err.Error(), BindingID: binding.ID, UserID: binding.UserID}
			break
		}
		c.mu.Lock()
		delete(c.work, binding.ID)
		_ = c.store.replaceWorkSessions(c.work)
		c.mu.Unlock()
		result = InputResult{Outcome: "work_ended", BindingID: binding.ID, UserID: binding.UserID}
	default:
		c.mu.RLock()
		work, ok := c.work[binding.ID]
		c.mu.RUnlock()
		if !ok || (!work.ExpiresAt.IsZero() && !input.ScannedAt.Before(work.ExpiresAt)) {
			result = InputResult{Outcome: "rejected", Message: "scanner work session is missing or expired", BindingID: binding.ID, UserID: binding.UserID}
			break
		}
		eventID := uuid.NewString()
		event := map[string]interface{}{"event_id": eventID, "binding_id": binding.ID, "device_fingerprint": input.DeviceFingerprint, "payload": input.Payload, "scanned_at": input.ScannedAt.UTC(), "work_session_id": work.ID, "operation_type_code": work.OperationTypeCode}
		if err := c.store.enqueue(eventID, "scanner.scan", event); err != nil {
			return InputResult{}, err
		}
		result = InputResult{Outcome: "queued", BindingID: binding.ID, UserID: binding.UserID, EventID: eventID}
	}
	if err := c.store.saveCapture(input.CaptureID, result); err != nil {
		return InputResult{}, err
	}
	return result, nil
}

func normalizeScanPayload(value string) string {
	return strings.TrimSpace(strings.TrimPrefix(value, "\uFEFF"))
}

func cloneBindings(source map[string]Binding) map[string]Binding {
	result := make(map[string]Binding, len(source))
	for key, value := range source {
		result[key] = value
	}
	return result
}

func (c *Coordinator) Start(ctx context.Context) {
	go c.syncLoop(ctx)
	go c.dispatchLoop(ctx)
}

func (c *Coordinator) syncLoop(ctx context.Context) {
	ticker := time.NewTicker(2 * time.Second)
	defer ticker.Stop()
	for {
		c.sync(ctx)
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

func (c *Coordinator) sync(ctx context.Context) {
	c.mu.RLock()
	enabled := c.cfg.Enabled
	c.mu.RUnlock()
	if !enabled {
		return
	}
	bindings, err := c.center.ScannerBindings(ctx)
	if err != nil {
		c.setError(err)
		return
	}
	mapped := map[string]Binding{}
	for _, item := range bindings {
		mapped[item.DeviceFingerprint] = Binding{ID: item.ID, UserID: item.UserID, BindingCode: item.BindingCode, DeviceFingerprint: item.DeviceFingerprint}
	}
	work, err := c.center.ScannerWorkSessions(ctx)
	if err != nil {
		c.setError(err)
		return
	}
	sessions := map[uint]WorkSession{}
	for _, item := range work {
		sessions[item.BindingID] = WorkSession{ID: item.ID, BindingID: item.BindingID, UserID: item.UserID, OperationTypeCode: item.OperationTypeCode, OperationTypeName: item.OperationTypeName, ExpiresAt: item.ExpiresAt}
	}
	c.mu.Lock()
	c.bindings = mapped
	c.work = sessions
	c.lastError = ""
	c.mu.Unlock()
	_ = c.store.replaceBindings(mapped)
	_ = c.store.replaceWorkSessions(sessions)
}

func (c *Coordinator) dispatchLoop(ctx context.Context) {
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			c.dispatch(ctx)
		}
	}
}

func (c *Coordinator) dispatch(ctx context.Context) {
	items, err := c.store.due(50)
	if err != nil {
		c.setError(err)
		return
	}
	for _, item := range items {
		permanent, sendErr := c.center.DispatchScanner(ctx, item.EventType, item.Payload)
		if sendErr == nil {
			_ = c.store.delivered(item.ID)
			continue
		}
		c.setError(sendErr)
		if permanent {
			_ = c.store.deadLetter(item, sendErr.Error())
			continue
		}
		attempts := item.Attempts
		if attempts > 8 {
			attempts = 8
		}
		_ = c.store.retry(item, sendErr.Error(), time.Now().Add(time.Duration(1<<attempts)*time.Second))
	}
}

func (c *Coordinator) setError(err error) {
	c.mu.Lock()
	c.lastError = err.Error()
	c.mu.Unlock()
}

func (c *Coordinator) Status() Status {
	c.mu.RLock()
	status := Status{Enabled: c.cfg.Enabled, Health: "disabled", LastError: c.lastError}
	if c.cfg.Enabled {
		status.Health = "healthy"
	}
	for _, value := range c.devices {
		status.Devices = append(status.Devices, value)
	}
	for _, value := range c.bindings {
		status.Bindings = append(status.Bindings, value)
	}
	c.mu.RUnlock()
	queueError := ""
	status.Pending, status.DeadLetters, queueError = c.store.queueStatus()
	if queueError != "" {
		status.LastError = queueError
	}
	if status.Enabled && status.LastError != "" {
		status.Health = "degraded"
	}
	return status
}
