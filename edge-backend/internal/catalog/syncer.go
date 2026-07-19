package catalog

import (
	"context"
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
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
	cfg           Config
	store         *Store
	client        *http.Client
	mu            sync.Mutex
	running       bool
	lastConfirmAt time.Time
	ticketKey     ed25519.PublicKey
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

func (m *Manager) TicketKeyReady() bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	return len(m.ticketKey) == ed25519.PublicKeySize
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
func (m *Manager) SearchProductsPage(keyword string, page PageFilter) ([]Product, int64, error) {
	return m.store.SearchProductsPage(m.cfg.NamespaceID, keyword, page)
}
func (m *Manager) SearchSKUs(keyword string, productID *uint) ([]SKU, error) {
	return m.store.SearchSKUs(m.cfg.NamespaceID, keyword, m.cfg.SKUQRPrefix, productID)
}
func (m *Manager) SearchSKUsPage(keyword string, productID *uint, page PageFilter) ([]SKU, int64, error) {
	return m.store.SearchSKUsPage(m.cfg.NamespaceID, keyword, m.cfg.SKUQRPrefix, productID, page)
}
func (m *Manager) SearchSKUsPageMode(keyword string, productID *uint, matchMode string, page PageFilter) ([]SKU, int64, error) {
	return m.store.SearchSKUsPageMode(m.cfg.NamespaceID, keyword, m.cfg.SKUQRPrefix, productID, matchMode, page)
}
func (m *Manager) GetSKU(idOrCode string) (*SKU, error) {
	return m.store.GetSKU(m.cfg.NamespaceID, idOrCode)
}
func (m *Manager) SearchBoxLabels(filter BoxLabelFilter) ([]BoxLabel, int64, error) {
	return m.store.SearchBoxLabels(m.cfg.NamespaceID, filter)
}
func (m *Manager) GetBoxLabel(id uint) (*BoxLabel, error) {
	return m.store.GetBoxLabel(m.cfg.NamespaceID, id)
}
func (m *Manager) ResolveBoxLabel(raw string) (*BoxLabel, error) {
	return m.store.ResolveBoxLabel(m.cfg.NamespaceID, raw)
}
func (m *Manager) CacheBoxLabels(items []BoxLabel) error {
	return m.store.CacheBoxLabels(m.cfg.NamespaceID, items)
}
func (m *Manager) CacheProducts(items []Product) error {
	return m.store.CacheProducts(m.cfg.NamespaceID, items)
}
func (m *Manager) CacheSKUs(items []SKU) error {
	return m.store.CacheSKUs(m.cfg.NamespaceID, items)
}

func (m *Manager) FetchAndCacheProducts(ctx context.Context, query url.Values) ([]Product, int64, error) {
	var response struct {
		Items   []Product `json:"items"`
		Data    []Product `json:"data"`
		List    []Product `json:"list"`
		Records []Product `json:"records"`
		Total   int64     `json:"total"`
	}
	if err := m.get(ctx, "/api/products", query, &response); err != nil {
		return nil, 0, err
	}
	items := firstProductRows(response.Items, response.Data, response.List, response.Records)
	for index := range items {
		normalizeProductMedia(&items[index])
	}
	if len(items) > 0 {
		if err := m.CacheProducts(items); err != nil {
			go func() { _ = m.RefreshFull(context.Background()) }()
		}
	}
	total := response.Total
	if total == 0 {
		total = int64(len(items))
	}
	return items, total, nil
}

func (m *Manager) FetchAndCacheSKUs(ctx context.Context, query url.Values) ([]SKU, int64, error) {
	var response struct {
		Items   []SKU `json:"items"`
		Data    []SKU `json:"data"`
		List    []SKU `json:"list"`
		Records []SKU `json:"records"`
		Total   int64 `json:"total"`
	}
	if err := m.get(ctx, "/api/skus", query, &response); err != nil {
		return nil, 0, err
	}
	items := firstSKURows(response.Items, response.Data, response.List, response.Records)
	for index := range items {
		normalizeSKUMedia(&items[index])
	}
	if len(items) > 0 {
		if err := m.CacheSKUs(items); err != nil {
			go func() { _ = m.RefreshFull(context.Background()) }()
		}
	}
	total := response.Total
	if total == 0 {
		total = int64(len(items))
	}
	return items, total, nil
}

func (m *Manager) FetchAndCacheSKU(ctx context.Context, idOrCode string) (*SKU, error) {
	var item SKU
	if err := m.get(ctx, "/api/skus/"+url.PathEscape(strings.TrimSpace(idOrCode)), nil, &item); err != nil {
		return nil, err
	}
	if item.ID == 0 && strings.TrimSpace(item.Code) == "" {
		return nil, nil
	}
	normalizeSKUMedia(&item)
	if err := m.CacheSKUs([]SKU{item}); err != nil {
		go func() { _ = m.RefreshFull(context.Background()) }()
	}
	return &item, nil
}

func firstProductRows(candidates ...[]Product) []Product {
	for _, items := range candidates {
		if len(items) > 0 {
			return items
		}
	}
	return []Product{}
}

func firstSKURows(candidates ...[]SKU) []SKU {
	for _, items := range candidates {
		if len(items) > 0 {
			return items
		}
	}
	return []SKU{}
}

func normalizeProductMedia(item *Product) {
	if item == nil || item.ID == 0 || item.Media != nil {
		return
	}
	reference := firstNonBlank(item.ThumbnailURL, item.ImageURL)
	item.Media = buildMediaRef("product", item.ID, reference)
}

func normalizeSKUMedia(item *SKU) {
	if item == nil || item.ID == 0 || item.Media != nil {
		return
	}
	if reference := firstNonBlank(item.ThumbnailURL, item.ImageURL); reference != "" {
		item.Media = buildMediaRef("sku", item.ID, reference)
		return
	}
	if item.Product != nil {
		normalizeProductMedia(item.Product)
		item.Media = item.Product.Media
	}
}

func buildMediaRef(entity string, id uint, centralURL string) *MediaRef {
	centralURL = strings.TrimSpace(centralURL)
	if centralURL == "" {
		return nil
	}
	stable := centralURL
	if parsed, err := url.Parse(centralURL); err == nil {
		parsed.RawQuery, parsed.Fragment = "", ""
		stable = parsed.String()
	}
	digest := sha256.Sum256([]byte(stable))
	version := hex.EncodeToString(digest[:8])
	return &MediaRef{
		ID: fmt.Sprintf("%s:%d", entity, id), Version: version,
		ThumbnailPath: fmt.Sprintf("/edge/v2/catalog/media/%s/%d/thumbnail?v=%s", entity, id, version),
		CentralURL:    centralURL,
	}
}

func firstNonBlank(values ...string) string {
	for _, value := range values {
		if value = strings.TrimSpace(value); value != "" {
			return value
		}
	}
	return ""
}

func (m *Manager) RefreshIfDue(ctx context.Context) {
	status, err := m.Status()
	if err != nil || !status.Ready || !status.BoxLabelsReady || time.Since(status.LastFullSyncAt) >= 24*time.Hour {
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
	if err == nil {
		revision, err = m.syncSnapshotBoxLabels(ctx, generation, revision)
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

// ConfirmChangesIfDue checks the center in the background while callers keep
// serving the active local generation. Concurrent and high-frequency terminal
// requests collapse into one confirmation per interval.
func (m *Manager) ConfirmChangesIfDue(minInterval time.Duration) bool {
	m.mu.Lock()
	if m.running || (!m.lastConfirmAt.IsZero() && time.Since(m.lastConfirmAt) < minInterval) {
		m.mu.Unlock()
		return false
	}
	m.running = true
	m.lastConfirmAt = time.Now()
	m.mu.Unlock()
	go func() {
		defer m.end()
		ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
		defer cancel()
		_ = m.syncChangesLocked(ctx)
	}()
	return true
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

func (m *Manager) syncSnapshotBoxLabels(ctx context.Context, generation int64, revision uint64) (uint64, error) {
	var after uint
	for {
		var response struct {
			Items           []BoxLabel `json:"items"`
			NextAfterID     uint       `json:"next_after_id"`
			Done            bool       `json:"done"`
			CatalogRevision uint64     `json:"catalog_revision"`
		}
		if err := m.get(ctx, "/api/edge/catalog/box-labels/snapshot", url.Values{"after_id": []string{strconv.FormatUint(uint64(after), 10)}, "limit": []string{"500"}}, &response); err != nil {
			return 0, err
		}
		if err := m.store.UpsertBoxLabels(m.cfg.NamespaceID, generation, response.Items); err != nil {
			return 0, err
		}
		if response.Done {
			if response.CatalogRevision > revision {
				return response.CatalogRevision, nil
			}
			return revision, nil
		}
		if response.NextAfterID <= after {
			return 0, fmt.Errorf("box labels snapshot cursor did not advance")
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
