package registry

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"
)

var (
	ErrMobilePrintUnauthorized = fmt.Errorf("mobile print authorization failed")
	ErrMobilePrintNodeMissing  = fmt.Errorf("current edge node is unavailable to the mobile user")
)

type ResponseError struct {
	StatusCode int
}

func (e *ResponseError) Error() string { return fmt.Sprintf("registry returned %d", e.StatusCode) }

type Config struct {
	CenterURL, APIToken, NodeID, NodeName, LANBaseURL, Version, APIVersion, CacheMode string
	NamespaceID                                                                       uint
	Capabilities                                                                      []string
	Inventory                                                                         func() interface{}
	HeartbeatInterval                                                                 time.Duration
	OnCatalogRevision                                                                 func(uint64)
	OnTicketPublicKey                                                                 func(string)
}

type Status struct {
	Registered    bool      `json:"registered"`
	LastSuccessAt time.Time `json:"last_success_at,omitempty"`
	LastError     string    `json:"last_error,omitempty"`
}

type Client struct {
	cfg       Config
	client    *http.Client
	mu        sync.RWMutex
	publishMu sync.Mutex
	status    Status
}

func New(cfg Config) *Client {
	if cfg.HeartbeatInterval <= 0 {
		cfg.HeartbeatInterval = 15 * time.Second
	}
	return &Client{cfg: cfg, client: &http.Client{Timeout: 5 * time.Second}}
}

func (c *Client) Start(ctx context.Context) {
	if strings.TrimSpace(c.cfg.CenterURL) == "" || strings.TrimSpace(c.cfg.APIToken) == "" {
		return
	}
	go func() {
		c.register(ctx)
		ticker := time.NewTicker(c.cfg.HeartbeatInterval)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				c.heartbeat(ctx)
			}
		}
	}()
}

func (c *Client) Status() Status { c.mu.RLock(); defer c.mu.RUnlock(); return c.status }

// SyncNow publishes a changed local capability inventory without waiting for the next heartbeat.
func (c *Client) SyncNow(ctx context.Context) { c.heartbeat(ctx) }

func (c *Client) register(ctx context.Context) {

	if !c.publishMu.TryLock() {
		return
	}
	defer c.publishMu.Unlock()
	c.registerLocked(ctx)
}

func (c *Client) registerLocked(ctx context.Context) {
	payload := map[string]interface{}{"node_id": c.cfg.NodeID, "node_name": c.cfg.NodeName, "lan_base_url": c.cfg.LANBaseURL, "backend_version": c.cfg.Version, "edge_api_version": c.cfg.APIVersion, "schema_version": 1, "capabilities": c.cfg.Capabilities, "cache_mode": c.cfg.CacheMode, "inventory": c.inventory()}
	c.send(ctx, "/api/edge/nodes/register", payload, true)
}

func (c *Client) heartbeat(ctx context.Context) {
	if !c.publishMu.TryLock() {
		return
	}
	defer c.publishMu.Unlock()
	// Send the full registration fields on every heartbeat. The center can then
	// replace a stale LAN address after an adapter or network change.
	payload := map[string]interface{}{"node_id": c.cfg.NodeID, "node_name": c.cfg.NodeName, "lan_base_url": c.cfg.LANBaseURL, "backend_version": c.cfg.Version, "edge_api_version": c.cfg.APIVersion, "schema_version": 1, "capabilities": c.cfg.Capabilities, "cache_mode": c.cfg.CacheMode, "inventory": c.inventory()}
	if !c.send(ctx, "/api/edge/nodes/heartbeat", payload, false) {
		c.registerLocked(ctx)
	}
}

func (c *Client) inventory() interface{} {
	if c.cfg.Inventory == nil {
		return nil
	}
	return c.cfg.Inventory()
}

type ClaimedPrintJob struct {
	ID              uint            `json:"id"`
	BatchID         *uint           `json:"batch_id,omitempty"`
	SequenceNo      int             `json:"sequence_no"`
	AvailableAt     *time.Time      `json:"available_at,omitempty"`
	JobType         string          `json:"job_type"`
	AttemptCount    int             `json:"attempt_count"`
	TemplateCode    string          `json:"template_code"`
	PrinterName     string          `json:"printer_name"`
	Copies          int             `json:"copies"`
	PayloadSnapshot json.RawMessage `json:"payload_snapshot"`
	LeaseToken      string          `json:"lease_token"`
}

type LocalPrintAudit struct {
	LocalJobID      string      `json:"local_job_id"`
	Source          string      `json:"source"`
	TemplateCode    string      `json:"template_code"`
	PrinterName     string      `json:"printer_name"`
	JobType         string      `json:"job_type"`
	Status          string      `json:"status"`
	LocalStatus     string      `json:"local_status,omitempty"`
	Copies          int         `json:"copies"`
	ContentSnapshot interface{} `json:"content_snapshot"`
	SubmittedAt     time.Time   `json:"submitted_at"`
	StartedAt       *time.Time  `json:"started_at,omitempty"`
	FinishedAt      *time.Time  `json:"finished_at,omitempty"`
	ErrorMessage    string      `json:"error_message,omitempty"`
}

func (c *Client) AuthorizeMobilePrint(ctx context.Context, authorization string) error {
	authorization = strings.TrimSpace(authorization)
	if authorization == "" {
		return ErrMobilePrintUnauthorized
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, strings.TrimRight(c.cfg.CenterURL, "/")+"/api/edge/nodes/available?page_size=100", nil)
	if err != nil {
		return err
	}
	request.Header.Set("Authorization", authorization)
	request.Header.Set("X-FSCM-Client", "mobile-app")
	if c.cfg.NamespaceID > 0 {
		request.Header.Set("X-Namespace-ID", strconv.FormatUint(uint64(c.cfg.NamespaceID), 10))
	}
	response, err := c.client.Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode == http.StatusUnauthorized || response.StatusCode == http.StatusForbidden {
		return ErrMobilePrintUnauthorized
	}
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return fmt.Errorf("mobile print authorization returned %d", response.StatusCode)
	}
	var envelope struct {
		Code int `json:"code"`
		Data struct {
			Items []struct {
				NodeID string `json:"node_id"`
			} `json:"items"`
		} `json:"data"`
	}
	if err := json.NewDecoder(response.Body).Decode(&envelope); err != nil {
		return err
	}
	if envelope.Code != 0 {
		return ErrMobilePrintUnauthorized
	}
	for _, node := range envelope.Data.Items {
		if strings.EqualFold(strings.TrimSpace(node.NodeID), strings.TrimSpace(c.cfg.NodeID)) {
			return nil
		}
	}
	return ErrMobilePrintNodeMissing
}

func (c *Client) SyncLocalPrintAudit(ctx context.Context, audit LocalPrintAudit) error {
	payload := map[string]interface{}{"node_id": c.cfg.NodeID, "audit": audit}
	return c.request(ctx, http.MethodPost, "/api/edge/nodes/print-jobs/local-audit", payload, nil)
}

// ClaimPrintJob leases one center task. A nil job means the queue is currently empty.
func (c *Client) ClaimPrintJob(ctx context.Context) (*ClaimedPrintJob, error) {
	job, _, err := c.ClaimPrintJobForBatch(ctx, 0)
	return job, err
}

// ClaimPrintJobForBatch keeps an active batch contiguous while remaining
// compatible with centers that ignore the optional batch_id field.
func (c *Client) ClaimPrintJobForBatch(ctx context.Context, batchID uint) (*ClaimedPrintJob, string, error) {
	var payload struct {
		Code int `json:"code"`
		Data struct {
			Job         *ClaimedPrintJob `json:"job"`
			LeaseToken  string           `json:"lease_token"`
			BatchStatus string           `json:"batch_status"`
		} `json:"data"`
		Msg string `json:"msg"`
	}
	request := map[string]interface{}{"node_id": c.cfg.NodeID}
	if batchID > 0 {
		request["batch_id"] = batchID
	}
	if err := c.request(ctx, http.MethodPost, "/api/edge/nodes/print-jobs/claim", request, &payload); err != nil {
		return nil, "", err
	}
	if payload.Data.Job != nil && payload.Data.Job.LeaseToken == "" {
		payload.Data.Job.LeaseToken = payload.Data.LeaseToken
	}
	return payload.Data.Job, payload.Data.BatchStatus, nil
}

func (c *Client) CompletePrintJob(ctx context.Context, jobID uint, leaseToken, status, errorMessage string, result interface{}) error {
	return c.CompletePrintJobWithCode(ctx, jobID, leaseToken, status, "", errorMessage, result)
}

func (c *Client) CompletePrintJobWithCode(ctx context.Context, jobID uint, leaseToken, status, errorCode, errorMessage string, result interface{}) error {
	payload := map[string]interface{}{"node_id": c.cfg.NodeID, "lease_token": leaseToken, "status": status, "error_code": errorCode, "error_message": errorMessage, "result": result}
	return c.request(ctx, http.MethodPost, fmt.Sprintf("/api/edge/nodes/print-jobs/%d/complete", jobID), payload, nil)
}

func IsLeaseInvalid(err error) bool {
	var responseErr *ResponseError
	return errors.As(err, &responseErr) && responseErr.StatusCode == http.StatusConflict
}

func (c *Client) request(ctx context.Context, method, path string, payload, output interface{}) error {
	body, err := json.Marshal(payload)
	if err != nil {
		return err
	}
	request, err := http.NewRequestWithContext(ctx, method, strings.TrimRight(c.cfg.CenterURL, "/")+path, bytes.NewReader(body))
	if err != nil {
		return err
	}
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("Authorization", "Bearer "+c.cfg.APIToken)
	request.Header.Set("X-API-Token", c.cfg.APIToken)
	if c.cfg.NamespaceID > 0 {
		request.Header.Set("X-FSCM-Edge-Node-ID", c.cfg.NodeID)
		request.Header.Set("X-Namespace-ID", strconv.FormatUint(uint64(c.cfg.NamespaceID), 10))
	}
	response, err := c.client.Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return &ResponseError{StatusCode: response.StatusCode}
	}
	if output != nil {
		return json.NewDecoder(response.Body).Decode(output)
	}
	return nil
}

func (c *Client) send(ctx context.Context, path string, payload interface{}, registration bool) bool {
	body, _ := json.Marshal(payload)
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, strings.TrimRight(c.cfg.CenterURL, "/")+path, bytes.NewReader(body))
	if err == nil {
		request.Header.Set("Content-Type", "application/json")
		request.Header.Set("Authorization", "Bearer "+c.cfg.APIToken)
		request.Header.Set("X-API-Token", c.cfg.APIToken)
		if c.cfg.NamespaceID > 0 {
			request.Header.Set("X-Namespace-ID", strconv.FormatUint(uint64(c.cfg.NamespaceID), 10))
		}
		var response *http.Response
		response, err = c.client.Do(request)
		if err == nil {
			defer response.Body.Close()
			if response.StatusCode < 200 || response.StatusCode >= 300 {
				err = fmt.Errorf("registry returned %d", response.StatusCode)
			} else if c.cfg.OnCatalogRevision != nil || c.cfg.OnTicketPublicKey != nil {
				var result struct {
					Data struct {
						CatalogRevision uint64 `json:"catalog_revision"`
						TicketPublicKey string `json:"ticket_public_key"`
					} `json:"data"`
				}
				if decodeErr := json.NewDecoder(response.Body).Decode(&result); decodeErr == nil {
					if c.cfg.OnCatalogRevision != nil {
						c.cfg.OnCatalogRevision(result.Data.CatalogRevision)
					}
					if c.cfg.OnTicketPublicKey != nil && result.Data.TicketPublicKey != "" {
						c.cfg.OnTicketPublicKey(result.Data.TicketPublicKey)
					}
				}
			}
		}
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	if err != nil {
		c.status.LastError = err.Error()
		return false
	}
	// A successful heartbeat also proves that the node is registered.
	c.status.Registered = c.status.Registered || registration
	c.status.LastSuccessAt, c.status.LastError = time.Now(), ""
	return true
}

type TerminalObservation struct {
	TerminalID  string    `json:"terminal_id"`
	State       string    `json:"state"`
	LANIP       string    `json:"lan_ip,omitempty"`
	ConnectedAt time.Time `json:"connected_at,omitempty"`
	LastSeenAt  time.Time `json:"last_seen_at,omitempty"`
}

type TerminalStatus struct {
	TerminalID             string     `json:"terminal_id"`
	Name                   string     `json:"terminal_name"`
	IP                     string     `json:"lan_ip"`
	Status                 string     `json:"status"`
	Capabilities           []string   `json:"capabilities"`
	LastSeenAt             time.Time  `json:"last_seen_at"`
	LANStatus              string     `json:"lan_status"`
	HealthStatus           string     `json:"health_status"`
	HealthReason           string     `json:"health_reason"`
	IsAlert                bool       `json:"is_alert"`
	LANOnlineSince         *time.Time `json:"lan_online_since"`
	LANLastSeenAt          *time.Time `json:"lan_last_seen_at"`
	AssignedEdgeNodeName   string     `json:"assigned_edge_node_name"`
	ObservedEdgeNodeName   string     `json:"observed_edge_node_name"`
	OnlineDurationSeconds  int64      `json:"online_duration_seconds"`
	OfflineDurationSeconds int64      `json:"offline_duration_seconds"`
}

func (c *Client) ObserveTerminals(ctx context.Context, values []TerminalObservation) error {
	if len(values) == 0 {
		return nil
	}
	return c.request(ctx, http.MethodPost, "/api/edge/nodes/terminals/observations", map[string]interface{}{"node_id": c.cfg.NodeID, "observed_at": time.Now().UTC(), "terminals": values}, nil)
}

func (c *Client) TerminalSnapshot(ctx context.Context) ([]TerminalStatus, error) {
	var result struct {
		Data struct {
			Items []TerminalStatus `json:"items"`
		} `json:"data"`
	}
	if err := c.request(ctx, http.MethodGet, "/api/edge/nodes/terminals/snapshot", nil, &result); err != nil {
		return nil, err
	}
	return result.Data.Items, nil
}

type ScannerBinding struct {
	ID                uint   `json:"id"`
	UserID            uint   `json:"user_id"`
	BindingCode       string `json:"binding_code"`
	DeviceFingerprint string `json:"device_fingerprint"`
}

type ScannerWorkSession struct {
	ID                uint      `json:"id"`
	BindingID         uint      `json:"binding_id"`
	UserID            uint      `json:"user_id"`
	OperationTypeCode string    `json:"operation_type_code"`
	OperationTypeName string    `json:"operation_type_name"`
	ExpiresAt         time.Time `json:"expires_at"`
}

func (c *Client) BindScanner(ctx context.Context, payload interface{}) (ScannerBinding, error) {
	var response struct {
		Data struct {
			Binding ScannerBinding `json:"binding"`
		} `json:"data"`
	}
	err := c.request(ctx, http.MethodPost, "/api/edge/scanners/bindings", payload, &response)
	return response.Data.Binding, err
}

func (c *Client) ScannerBindings(ctx context.Context) ([]ScannerBinding, error) {
	var response struct {
		Data struct {
			Items []ScannerBinding `json:"items"`
		} `json:"data"`
	}
	err := c.request(ctx, http.MethodGet, "/api/edge/scanners/bindings/active", nil, &response)
	return response.Data.Items, err
}

func (c *Client) ScannerWorkSessions(ctx context.Context) ([]ScannerWorkSession, error) {
	var response struct {
		Data struct {
			Items        []ScannerWorkSession `json:"items"`
			Sessions     []ScannerWorkSession `json:"sessions"`
			WorkSessions []ScannerWorkSession `json:"work_sessions"`
			Tasks        []ScannerWorkSession `json:"tasks"`
		} `json:"data"`
	}
	err := c.request(ctx, http.MethodGet, "/api/edge/scanners/work-sessions", nil, &response)
	if err != nil {
		return nil, err
	}

	collections := [][]ScannerWorkSession{
		response.Data.Items,
		response.Data.Sessions,
		response.Data.WorkSessions,
		response.Data.Tasks,
	}
	for _, items := range collections {
		if items != nil {
			return items, nil
		}
	}
	return []ScannerWorkSession{}, nil
}

func (c *Client) StartScannerWorkSession(ctx context.Context, payload interface{}) (ScannerWorkSession, error) {
	var response struct {
		Data ScannerWorkSession `json:"data"`
	}
	err := c.request(ctx, http.MethodPost, "/api/edge/scanners/work-sessions/start", payload, &response)
	return response.Data, err
}

func (c *Client) EndScannerWorkSession(ctx context.Context, payload interface{}) error {
	return c.request(ctx, http.MethodPost, "/api/edge/scanners/work-sessions/end", payload, nil)
}

func (c *Client) DispatchScanner(ctx context.Context, eventType string, payload json.RawMessage) (bool, error) {
	path := map[string]string{
		"scanner.release":     "/api/edge/scanners/bindings/release",
		"scanner.scan":        "/api/edge/scanners/events/batch",
		"scanner.device_sync": "/api/edge/scanners/devices/sync",
	}[eventType]
	if path == "" {
		return true, fmt.Errorf("unsupported scanner event type %q", eventType)
	}
	if eventType == "scanner.scan" {
		wrapped, _ := json.Marshal(map[string]interface{}{"events": []json.RawMessage{payload}})
		payload = wrapped
	}
	err := c.request(ctx, http.MethodPost, path, payload, nil)
	if err == nil {
		return false, nil
	}
	var responseErr *ResponseError
	if errors.As(err, &responseErr) && responseErr.StatusCode >= 400 && responseErr.StatusCode < 500 && responseErr.StatusCode != http.StatusTooManyRequests {
		return true, err
	}
	return false, err
}
