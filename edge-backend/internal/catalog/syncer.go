package catalog

import (
	"context"
	"crypto/ed25519"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"
)

type Config struct {
	CenterURL   string
	APIToken    string
	NodeID      string
	NamespaceID uint
	SKUQRPrefix string
}

type Manager struct {
	cfg       Config
	store     *Store
	client    *http.Client
	mu        sync.Mutex
	running   bool
	ticketKey ed25519.PublicKey
}

func NewManager(cfg Config, store *Store) *Manager {
	return &Manager{cfg: cfg, store: store, client: &http.Client{Timeout: 30 * time.Second}}
}

func (m *Manager) Start(ctx context.Context) {
	go func() {
		m.RefreshIfDue(ctx)
		ticker := time.NewTicker(time.Hour)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				m.RefreshIfDue(ctx)
			}
		}
	}()
}

// OnRemoteRevision is called from the existing edge-node heartbeat response.
func (m *Manager) OnRemoteRevision(revision uint64) {
	status, err := m.Status()
	if err != nil || revision <= status.Revision {
		return
	}
	go func() { _ = m.SyncChanges(context.Background()) }()
}

func (m *Manager) SetTicketPublicKey(encoded string) {
	value, err := base64.RawURLEncoding.DecodeString(strings.TrimSpace(encoded))
	if err != nil || len(value) != ed25519.PublicKeySize {
		return
	}
	m.mu.Lock()
	m.ticketKey = append(ed25519.PublicKey(nil), value...)
	m.mu.Unlock()
}

func (m *Manager) AuthorizeTicket(value string) error {
	m.mu.Lock()
	key := append(ed25519.PublicKey(nil), m.ticketKey...)
	m.mu.Unlock()
	return validateTicket(value, key, m.cfg.NamespaceID, m.cfg.NodeID)
}

func (m *Manager) Status() (Status, error) { return m.store.Status(m.cfg.NamespaceID) }
func (m *Manager) SearchProducts(keyword string) ([]Product, error) {
	return m.store.SearchProducts(m.cfg.NamespaceID, keyword)
}
func (m *Manager) SearchSKUs(keyword string, productID *uint) ([]SKU, error) {
	return m.store.SearchSKUs(m.cfg.NamespaceID, keyword, m.cfg.SKUQRPrefix, productID)
}

func (m *Manager) RefreshIfDue(ctx context.Context) {
	status, err := m.Status()
	if err != nil || !status.Ready || time.Since(status.LastFullSyncAt) >= 24*time.Hour {
		_ = m.RefreshFull(ctx)
	}
}

func (m *Manager) RefreshFull(ctx context.Context) error {
	if !m.begin() {
		return nil
	}
	defer m.end()
	return m.refreshFullLocked(ctx)
}

func (m *Manager) refreshFullLocked(ctx context.Context) error {
	if err := m.validateConfig(); err != nil {
		m.store.RecordError(m.cfg.NamespaceID, err)
		return err
	}
	generation, err := m.store.BeginFullSync(m.cfg.NamespaceID)
	if err != nil {
		return err
	}
	var revision uint64
	if revision, err = m.syncSnapshotProducts(ctx, generation); err == nil {
		revision, err = m.syncSnapshotSKUs(ctx, generation, revision)
	}
	if err != nil {
		m.store.RecordError(m.cfg.NamespaceID, err)
		return err
	}
	if err = m.store.FinishFullSync(m.cfg.NamespaceID, generation, revision); err != nil {
		m.store.RecordError(m.cfg.NamespaceID, err)
		return err
	}
	return m.syncChangesLocked(ctx)
}

func (m *Manager) SyncChanges(ctx context.Context) error {
	if !m.begin() {
		return nil
	}
	defer m.end()
	return m.syncChangesLocked(ctx)
}

func (m *Manager) syncChangesLocked(ctx context.Context) error {
	if err := m.validateConfig(); err != nil {
		m.store.RecordError(m.cfg.NamespaceID, err)
		return err
	}
	status, err := m.Status()
	if err != nil || !status.Ready {
		return m.refreshFullLocked(ctx)
	}
	for {
		var response struct {
			Items            []Change `json:"items"`
			NextRevision     uint64   `json:"next_revision"`
			CatalogRevision  uint64   `json:"catalog_revision"`
			FullSyncRequired bool     `json:"full_sync_required"`
		}
		if err := m.get(ctx, "/api/edge/catalog/changes", url.Values{"after_revision": []string{strconv.FormatUint(status.Revision, 10)}, "limit": []string{"500"}}, &response); err != nil {
			m.store.RecordError(m.cfg.NamespaceID, err)
			return err
		}
		if response.FullSyncRequired {
			return m.refreshFullLocked(ctx)
		}
		if len(response.Items) == 0 {
			if response.CatalogRevision > status.Revision {
				return fmt.Errorf("catalog revision advanced without changes")
			}
			return nil
		}
		if err := m.store.ApplyChanges(m.cfg.NamespaceID, response.Items, response.NextRevision); err != nil {
			m.store.RecordError(m.cfg.NamespaceID, err)
			return err
		}
		status.Revision = response.NextRevision
	}
}

func (m *Manager) syncSnapshotProducts(ctx context.Context, generation int64) (uint64, error) {
	var after uint
	var revision uint64
	for {
		var response struct {
			Items           []Product `json:"items"`
			NextAfterID     uint      `json:"next_after_id"`
			Done            bool      `json:"done"`
			CatalogRevision uint64    `json:"catalog_revision"`
		}
		if err := m.get(ctx, "/api/edge/catalog/products/snapshot", url.Values{"after_id": []string{strconv.FormatUint(uint64(after), 10)}, "limit": []string{"500"}}, &response); err != nil {
			return 0, err
		}
		if err := m.store.UpsertProducts(m.cfg.NamespaceID, generation, response.Items); err != nil {
			return 0, err
		}
		if revision == 0 {
			revision = response.CatalogRevision
		}
		if response.Done {
			return revision, nil
		}
		if response.NextAfterID <= after {
			return 0, fmt.Errorf("products snapshot cursor did not advance")
		}
		after = response.NextAfterID
	}
}

func (m *Manager) syncSnapshotSKUs(ctx context.Context, generation int64, revision uint64) (uint64, error) {
	var after uint
	for {
		var response struct {
			Items           []SKU  `json:"items"`
			NextAfterID     uint   `json:"next_after_id"`
			Done            bool   `json:"done"`
			CatalogRevision uint64 `json:"catalog_revision"`
		}
		if err := m.get(ctx, "/api/edge/catalog/skus/snapshot", url.Values{"after_id": []string{strconv.FormatUint(uint64(after), 10)}, "limit": []string{"500"}}, &response); err != nil {
			return 0, err
		}
		if err := m.store.UpsertSKUs(m.cfg.NamespaceID, generation, response.Items); err != nil {
			return 0, err
		}
		if response.Done {
			return revision, nil
		}
		if response.NextAfterID <= after {
			return 0, fmt.Errorf("skus snapshot cursor did not advance")
		}
		after = response.NextAfterID
	}
}

func (m *Manager) get(ctx context.Context, path string, query url.Values, output any) error {
	endpoint := strings.TrimRight(m.cfg.CenterURL, "/") + path
	if len(query) > 0 {
		endpoint += "?" + query.Encode()
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint, nil)
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Bearer "+m.cfg.APIToken)
	req.Header.Set("X-API-Token", m.cfg.APIToken)
	req.Header.Set("X-FSCM-Edge-Node-ID", m.cfg.NodeID)
	req.Header.Set("X-Namespace-ID", strconv.FormatUint(uint64(m.cfg.NamespaceID), 10))
	response, err := m.client.Do(req)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return fmt.Errorf("catalog center returned %d", response.StatusCode)
	}
	var envelope struct {
		Code int             `json:"code"`
		Data json.RawMessage `json:"data"`
		Msg  string          `json:"msg"`
	}
	if err := json.NewDecoder(response.Body).Decode(&envelope); err != nil {
		return err
	}
	if envelope.Code != 0 {
		return fmt.Errorf("catalog center rejected request: %s", envelope.Msg)
	}
	return json.Unmarshal(envelope.Data, output)
}

func (m *Manager) begin() bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.running {
		return false
	}
	m.running = true
	return true
}
func (m *Manager) end() { m.mu.Lock(); m.running = false; m.mu.Unlock() }
func (m *Manager) validateConfig() error {
	if strings.TrimSpace(m.cfg.CenterURL) == "" || strings.TrimSpace(m.cfg.APIToken) == "" || strings.TrimSpace(m.cfg.NodeID) == "" || m.cfg.NamespaceID == 0 {
		return fmt.Errorf("catalog sync is not configured")
	}
	return nil
}
