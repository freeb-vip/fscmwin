package catalog

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	_ "modernc.org/sqlite"
)

type Product struct {
	ID           uint      `json:"id"`
	Code         string    `json:"code"`
	Name         string    `json:"name"`
	Brand        string    `json:"brand"`
	ImageURL     string    `json:"image_url,omitempty"`
	ThumbnailURL string    `json:"thumbnail_url,omitempty"`
	Media        *MediaRef `json:"media,omitempty"`
}

type SKU struct {
	ID           uint      `json:"id"`
	Code         string    `json:"code"`
	Name         string    `json:"name"`
	Spec         string    `json:"spec"`
	Color        string    `json:"color"`
	ProductID    uint      `json:"product_id"`
	ProductCode  string    `json:"product_code"`
	ImageURL     string    `json:"image_url,omitempty"`
	ThumbnailURL string    `json:"thumbnail_url,omitempty"`
	Product      *Product  `json:"product,omitempty"`
	Media        *MediaRef `json:"media,omitempty"`
}

type MediaRef struct {
	ID            string `json:"id"`
	Version       string `json:"version"`
	ThumbnailPath string `json:"thumbnail_path"`
	CentralURL    string `json:"central_url,omitempty"`
}

type DocumentRef struct {
	ID     uint   `json:"id"`
	Code   string `json:"code"`
	Status string `json:"status"`
}

type BoxLabelSKUItem struct {
	SKUID       uint   `json:"sku_id"`
	SKUCode     string `json:"sku_code"`
	SKUName     string `json:"sku_name"`
	ProductID   uint   `json:"product_id"`
	ProductCode string `json:"product_code"`
	ProductName string `json:"product_name"`
	QtyPerBox   int    `json:"qty_per_box"`
}

type BoxLabelReceiving struct {
	Status            string       `json:"status"`
	WarehouseCode     string       `json:"warehouse_code"`
	LocationCode      string       `json:"location_code"`
	Session           *DocumentRef `json:"session,omitempty"`
	ScanStatus        string       `json:"scan_status,omitempty"`
	ScannedAt         *time.Time   `json:"scanned_at,omitempty"`
	ActualReceivedQty int          `json:"actual_received_qty"`
	ReceivingRecord   *DocumentRef `json:"receiving_record,omitempty"`
}

type BoxMark struct {
	BoxPlanID    uint     `json:"box_plan_id"`
	SeaMark      string   `json:"sea_mark"`
	Shop         string   `json:"shop"`
	StyleCode    string   `json:"style_code"`
	SKULines     []string `json:"sku_lines"`
	Spec         string   `json:"spec"`
	PCS          string   `json:"pcs"`
	SKUBoxs      string   `json:"sku_boxs"`
	Batch        string   `json:"batch"`
	SKUQRPayload string   `json:"sku_qr_payload"`
	BoxQRPayload string   `json:"box_qr_payload"`
	BoxUID       string   `json:"box_uid"`
	InboundCode  string   `json:"inbound_code"`
}

type BoxLabel struct {
	ID                     uint              `json:"id"`
	LabelCode              string            `json:"label_code"`
	BoxUID                 string            `json:"box_uid"`
	BoxNo                  string            `json:"box_no"`
	Status                 string            `json:"status"`
	StatusGroup            string            `json:"status_group"`
	StatusLabel            string            `json:"status_label"`
	IsMixed                bool              `json:"is_mixed"`
	SKUItems               []BoxLabelSKUItem `json:"sku_items"`
	PlannedBoxQty          int               `json:"planned_box_qty"`
	CaseSpecName           string            `json:"case_spec_name"`
	ManufacturerID         uint              `json:"manufacturer_id,omitempty"`
	ManufacturerName       string            `json:"manufacturer_name,omitempty"`
	SupplierOrder          DocumentRef       `json:"supplier_order"`
	PurchaseOrder          *DocumentRef      `json:"purchase_order,omitempty"`
	CentralReceipt         *DocumentRef      `json:"central_receipt,omitempty"`
	ConsolidationOrder     *DocumentRef      `json:"consolidation_order,omitempty"`
	ConsolidationContainer string            `json:"consolidation_container,omitempty"`
	Receiving              BoxLabelReceiving `json:"receiving"`
	CreatedAt              time.Time         `json:"created_at"`
	Printable              bool              `json:"printable"`
	PrintSnapshot          *BoxMark          `json:"print_snapshot,omitempty"`
}

type BoxLabelFilter struct {
	Keyword, ConsolidationOrderCode, StatusGroup, ReceivingStatus string
	ProductID, SKUID, ConsolidationOrderID                        uint
	Page, PageSize                                                int
}

type PageFilter struct {
	Page, PageSize int
}

type Change struct {
	Revision   uint64          `json:"revision"`
	EntityType string          `json:"entity_type"`
	Action     string          `json:"action"`
	EntityID   uint            `json:"entity_id"`
	Payload    json.RawMessage `json:"payload"`
}

type Status struct {
	Ready            bool      `json:"ready"`
	State            string    `json:"state"`
	Revision         uint64    `json:"revision"`
	ActiveGeneration int64     `json:"active_generation"`
	LastFullSyncAt   time.Time `json:"last_full_sync_at,omitempty"`
	LastError        string    `json:"last_error,omitempty"`
	BoxLabelCount    int64     `json:"box_label_count"`
	BoxLabelsReady   bool      `json:"box_labels_ready"`
}

type Store struct{ db *sql.DB }

func Open(path string) (*Store, error) {
	if strings.TrimSpace(path) == "" {
		return nil, fmt.Errorf("catalog database path is required")
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return nil, err
	}
	db, err := sql.Open("sqlite", path+"?_pragma=busy_timeout(5000)&_pragma=journal_mode(WAL)")
	if err != nil {
		return nil, err
	}
	store := &Store{db: db}
	if err := store.migrate(); err != nil {
		_ = db.Close()
		return nil, err
	}
	return store, nil
}

func (s *Store) Close() error { return s.db.Close() }

func (s *Store) migrate() error {
	_, err := s.db.Exec(`
CREATE TABLE IF NOT EXISTS catalog_meta (
  namespace_id INTEGER PRIMARY KEY,
  active_generation INTEGER NOT NULL DEFAULT 0,
  revision INTEGER NOT NULL DEFAULT 0,
  last_full_sync_at TEXT NOT NULL DEFAULT '',
  state TEXT NOT NULL DEFAULT 'empty',
  last_error TEXT NOT NULL DEFAULT ''
  ,box_labels_initialized INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS catalog_products (
  namespace_id INTEGER NOT NULL,
  id INTEGER NOT NULL,
  code TEXT NOT NULL COLLATE NOCASE,
  name TEXT NOT NULL DEFAULT '',
  brand TEXT NOT NULL DEFAULT '',
  media_id TEXT NOT NULL DEFAULT '',
  media_version TEXT NOT NULL DEFAULT '',
  thumbnail_path TEXT NOT NULL DEFAULT '',
  central_url TEXT NOT NULL DEFAULT '',
  generation INTEGER NOT NULL,
  PRIMARY KEY(namespace_id, id, generation)
);
CREATE TABLE IF NOT EXISTS catalog_skus (
  namespace_id INTEGER NOT NULL,
  id INTEGER NOT NULL,
  code TEXT NOT NULL COLLATE NOCASE,
  name TEXT NOT NULL DEFAULT '',
  spec TEXT NOT NULL DEFAULT '',
  color TEXT NOT NULL DEFAULT '',
  product_id INTEGER NOT NULL,
  product_code TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
  media_id TEXT NOT NULL DEFAULT '',
  media_version TEXT NOT NULL DEFAULT '',
  thumbnail_path TEXT NOT NULL DEFAULT '',
  central_url TEXT NOT NULL DEFAULT '',
  generation INTEGER NOT NULL,
  PRIMARY KEY(namespace_id, id, generation)
);
CREATE TABLE IF NOT EXISTS catalog_box_labels (
  namespace_id INTEGER NOT NULL,
  id INTEGER NOT NULL,
  label_code TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
  box_uid TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
  box_no TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
  supplier_order_code TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
  consolidation_order_id INTEGER NOT NULL DEFAULT 0,
  consolidation_order_code TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
  status_group TEXT NOT NULL DEFAULT '',
  receiving_status TEXT NOT NULL DEFAULT '',
  created_at TEXT NOT NULL DEFAULT '',
  payload BLOB NOT NULL,
  generation INTEGER NOT NULL,
  PRIMARY KEY(namespace_id, id, generation)
);
CREATE TABLE IF NOT EXISTS catalog_box_label_skus (
  namespace_id INTEGER NOT NULL,
  box_label_id INTEGER NOT NULL,
  sku_id INTEGER NOT NULL,
  product_id INTEGER NOT NULL,
  sku_code TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
  generation INTEGER NOT NULL,
  PRIMARY KEY(namespace_id, box_label_id, sku_id, generation)
);
CREATE INDEX IF NOT EXISTS idx_catalog_products_prefix ON catalog_products(namespace_id, generation, code COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_catalog_skus_prefix ON catalog_skus(namespace_id, generation, code COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_catalog_skus_product ON catalog_skus(namespace_id, generation, product_id);
CREATE INDEX IF NOT EXISTS idx_catalog_box_labels_code ON catalog_box_labels(namespace_id, generation, label_code COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_catalog_box_labels_consolidation ON catalog_box_labels(namespace_id, generation, consolidation_order_id, consolidation_order_code COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_catalog_box_labels_status ON catalog_box_labels(namespace_id, generation, status_group, receiving_status, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_catalog_box_label_skus_sku ON catalog_box_label_skus(namespace_id, generation, sku_id, box_label_id);
CREATE INDEX IF NOT EXISTS idx_catalog_box_label_skus_product ON catalog_box_label_skus(namespace_id, generation, product_id, box_label_id);`)
	if err != nil {
		return err
	}
	if !s.hasColumn("catalog_meta", "box_labels_initialized") {
		_, err = s.db.Exec(`ALTER TABLE catalog_meta ADD COLUMN box_labels_initialized INTEGER NOT NULL DEFAULT 0`)
	}
	for _, migration := range []struct{ table, column string }{
		{"catalog_products", "media_id"}, {"catalog_products", "media_version"},
		{"catalog_products", "thumbnail_path"}, {"catalog_products", "central_url"},
		{"catalog_skus", "media_id"}, {"catalog_skus", "media_version"},
		{"catalog_skus", "thumbnail_path"}, {"catalog_skus", "central_url"},
	} {
		if !s.hasColumn(migration.table, migration.column) {
			if _, alterErr := s.db.Exec(`ALTER TABLE ` + migration.table + ` ADD COLUMN ` + migration.column + ` TEXT NOT NULL DEFAULT ''`); alterErr != nil {
				return alterErr
			}
		}
	}
	return err
}

func (s *Store) hasColumn(table, column string) bool {
	rows, err := s.db.Query(`PRAGMA table_info(` + table + `)`)
	if err != nil {
		return false
	}
	defer rows.Close()
	for rows.Next() {
		var cid int
		var name, dataType string
		var notNull, primaryKey int
		var defaultValue any
		if rows.Scan(&cid, &name, &dataType, &notNull, &defaultValue, &primaryKey) == nil && name == column {
			return true
		}
	}
	return false
}

func (s *Store) Status(namespaceID uint) (Status, error) {
	if err := s.ensureMeta(namespaceID); err != nil {
		return Status{}, err
	}
	var status Status
	var full string
	var boxLabelsInitialized int
	err := s.db.QueryRow(`SELECT active_generation, revision, last_full_sync_at, state, last_error, box_labels_initialized FROM catalog_meta WHERE namespace_id = ?`, namespaceID).
		Scan(&status.ActiveGeneration, &status.Revision, &full, &status.State, &status.LastError, &boxLabelsInitialized)
	if err != nil {
		return Status{}, err
	}
	status.Ready = status.ActiveGeneration > 0 && status.State != "empty"
	status.BoxLabelsReady = boxLabelsInitialized == 1
	if status.ActiveGeneration > 0 {
		_ = s.db.QueryRow(`SELECT COUNT(*) FROM catalog_box_labels WHERE namespace_id=? AND generation=?`, namespaceID, status.ActiveGeneration).Scan(&status.BoxLabelCount)
	}
	if full != "" {
		status.LastFullSyncAt, _ = time.Parse(time.RFC3339Nano, full)
	}
	return status, nil
}

func (s *Store) BeginFullSync(namespaceID uint) (int64, error) {
	if err := s.ensureMeta(namespaceID); err != nil {
		return 0, err
	}
	status, err := s.Status(namespaceID)
	if err != nil {
		return 0, err
	}
	next := status.ActiveGeneration + 1
	if next < 1 {
		next = 1
	}
	tx, err := s.db.Begin()
	if err != nil {
		return 0, err
	}
	defer func() { _ = tx.Rollback() }()
	if _, err = tx.Exec(`DELETE FROM catalog_products WHERE namespace_id = ? AND generation = ?`, namespaceID, next); err != nil {
		return 0, err
	}
	if _, err = tx.Exec(`DELETE FROM catalog_skus WHERE namespace_id = ? AND generation = ?`, namespaceID, next); err != nil {
		return 0, err
	}
	if _, err = tx.Exec(`DELETE FROM catalog_box_label_skus WHERE namespace_id = ? AND generation = ?`, namespaceID, next); err != nil {
		return 0, err
	}
	if _, err = tx.Exec(`DELETE FROM catalog_box_labels WHERE namespace_id = ? AND generation = ?`, namespaceID, next); err != nil {
		return 0, err
	}
	if _, err = tx.Exec(`UPDATE catalog_meta SET state = 'syncing', last_error = '' WHERE namespace_id = ?`, namespaceID); err != nil {
		return 0, err
	}
	return next, tx.Commit()
}

func (s *Store) UpsertProducts(namespaceID uint, generation int64, items []Product) error {
	if len(items) == 0 {
		return nil
	}
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	statement, err := tx.Prepare(`INSERT INTO catalog_products(namespace_id,id,code,name,brand,media_id,media_version,thumbnail_path,central_url,generation) VALUES(?,?,?,?,?,?,?,?,?,?) ON CONFLICT(namespace_id,id,generation) DO UPDATE SET code=excluded.code,name=excluded.name,brand=excluded.brand,media_id=excluded.media_id,media_version=excluded.media_version,thumbnail_path=excluded.thumbnail_path,central_url=excluded.central_url`)
	if err != nil {
		return err
	}
	defer statement.Close()
	for _, item := range items {
		mediaID, mediaVersion, thumbnailPath, centralURL := mediaRefValues(item.Media)
		if _, err := statement.Exec(namespaceID, item.ID, item.Code, item.Name, item.Brand, mediaID, mediaVersion, thumbnailPath, centralURL, generation); err != nil {
			return err
		}
	}
	return tx.Commit()
}

func (s *Store) UpsertSKUs(namespaceID uint, generation int64, items []SKU) error {
	if len(items) == 0 {
		return nil
	}
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	statement, err := tx.Prepare(`INSERT INTO catalog_skus(namespace_id,id,code,name,spec,color,product_id,product_code,media_id,media_version,thumbnail_path,central_url,generation) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(namespace_id,id,generation) DO UPDATE SET code=excluded.code,name=excluded.name,spec=excluded.spec,color=excluded.color,product_id=excluded.product_id,product_code=excluded.product_code,media_id=excluded.media_id,media_version=excluded.media_version,thumbnail_path=excluded.thumbnail_path,central_url=excluded.central_url`)
	if err != nil {
		return err
	}
	defer statement.Close()
	for _, item := range items {
		mediaID, mediaVersion, thumbnailPath, centralURL := mediaRefValues(item.Media)
		if _, err := statement.Exec(namespaceID, item.ID, item.Code, item.Name, item.Spec, item.Color, item.ProductID, item.ProductCode, mediaID, mediaVersion, thumbnailPath, centralURL, generation); err != nil {
			return err
		}
	}
	return tx.Commit()
}

func (s *Store) UpsertBoxLabels(namespaceID uint, generation int64, items []BoxLabel) error {
	if len(items) == 0 {
		return nil
	}
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	for _, item := range items {
		payload, marshalErr := json.Marshal(item)
		if marshalErr != nil {
			return marshalErr
		}
		consolidationID, consolidationCode := uint(0), ""
		if item.ConsolidationOrder != nil {
			consolidationID, consolidationCode = item.ConsolidationOrder.ID, item.ConsolidationOrder.Code
		}
		if _, err = tx.Exec(`INSERT INTO catalog_box_labels(namespace_id,id,label_code,box_uid,box_no,supplier_order_code,consolidation_order_id,consolidation_order_code,status_group,receiving_status,created_at,payload,generation) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(namespace_id,id,generation) DO UPDATE SET label_code=excluded.label_code,box_uid=excluded.box_uid,box_no=excluded.box_no,supplier_order_code=excluded.supplier_order_code,consolidation_order_id=excluded.consolidation_order_id,consolidation_order_code=excluded.consolidation_order_code,status_group=excluded.status_group,receiving_status=excluded.receiving_status,created_at=excluded.created_at,payload=excluded.payload`, namespaceID, item.ID, item.LabelCode, item.BoxUID, item.BoxNo, item.SupplierOrder.Code, consolidationID, consolidationCode, item.StatusGroup, item.Receiving.Status, item.CreatedAt.UTC().Format(time.RFC3339Nano), payload, generation); err != nil {
			return err
		}
		if _, err = tx.Exec(`DELETE FROM catalog_box_label_skus WHERE namespace_id=? AND box_label_id=? AND generation=?`, namespaceID, item.ID, generation); err != nil {
			return err
		}
		for _, sku := range item.SKUItems {
			if _, err = tx.Exec(`INSERT INTO catalog_box_label_skus(namespace_id,box_label_id,sku_id,product_id,sku_code,generation) VALUES(?,?,?,?,?,?) ON CONFLICT(namespace_id,box_label_id,sku_id,generation) DO UPDATE SET product_id=excluded.product_id,sku_code=excluded.sku_code`, namespaceID, item.ID, sku.SKUID, sku.ProductID, sku.SKUCode, generation); err != nil {
				return err
			}
		}
	}
	return tx.Commit()
}

func (s *Store) CacheBoxLabels(namespaceID uint, items []BoxLabel) error {
	if len(items) == 0 {
		return nil
	}
	status, err := s.Status(namespaceID)
	if err != nil {
		return err
	}
	if status.ActiveGeneration == 0 {
		return fmt.Errorf("catalog is not initialized")
	}
	return s.UpsertBoxLabels(namespaceID, status.ActiveGeneration, items)
}

func (s *Store) CacheProducts(namespaceID uint, items []Product) error {
	if len(items) == 0 {
		return nil
	}
	status, err := s.Status(namespaceID)
	if err != nil {
		return err
	}
	if status.ActiveGeneration == 0 {
		return fmt.Errorf("catalog is not initialized")
	}
	return s.UpsertProducts(namespaceID, status.ActiveGeneration, items)
}

func (s *Store) CacheSKUs(namespaceID uint, items []SKU) error {
	if len(items) == 0 {
		return nil
	}
	status, err := s.Status(namespaceID)
	if err != nil {
		return err
	}
	if status.ActiveGeneration == 0 {
		return fmt.Errorf("catalog is not initialized")
	}
	return s.UpsertSKUs(namespaceID, status.ActiveGeneration, items)
}

func (s *Store) FinishFullSync(namespaceID uint, generation int64, revision uint64) error {
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	now := time.Now().UTC().Format(time.RFC3339Nano)
	if _, err = tx.Exec(`UPDATE catalog_meta SET active_generation=?, revision=?, last_full_sync_at=?, state='ready', last_error='', box_labels_initialized=1 WHERE namespace_id=?`, generation, revision, now, namespaceID); err != nil {
		return err
	}
	if _, err = tx.Exec(`DELETE FROM catalog_products WHERE namespace_id=? AND generation<>?`, namespaceID, generation); err != nil {
		return err
	}
	if _, err = tx.Exec(`DELETE FROM catalog_skus WHERE namespace_id=? AND generation<>?`, namespaceID, generation); err != nil {
		return err
	}
	if _, err = tx.Exec(`DELETE FROM catalog_box_label_skus WHERE namespace_id=? AND generation<>?`, namespaceID, generation); err != nil {
		return err
	}
	if _, err = tx.Exec(`DELETE FROM catalog_box_labels WHERE namespace_id=? AND generation<>?`, namespaceID, generation); err != nil {
		return err
	}
	return tx.Commit()
}

func (s *Store) ApplyChanges(namespaceID uint, changes []Change, revision uint64) error {
	if len(changes) == 0 {
		_, err := s.db.Exec(`UPDATE catalog_meta SET revision=?, state='ready', last_error='' WHERE namespace_id=?`, revision, namespaceID)
		return err
	}
	status, err := s.Status(namespaceID)
	if err != nil {
		return err
	}
	if status.ActiveGeneration == 0 {
		return fmt.Errorf("catalog is not initialized")
	}
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	for _, change := range changes {
		switch change.EntityType {
		case "product":
			if change.Action == "delete" {
				if _, err = tx.Exec(`DELETE FROM catalog_skus WHERE namespace_id=? AND generation=? AND product_id=?`, namespaceID, status.ActiveGeneration, change.EntityID); err != nil {
					return err
				}
				if _, err = tx.Exec(`DELETE FROM catalog_products WHERE namespace_id=? AND generation=? AND id=?`, namespaceID, status.ActiveGeneration, change.EntityID); err != nil {
					return err
				}
				continue
			}
			var item Product
			if err = json.Unmarshal(change.Payload, &item); err != nil {
				return err
			}
			mediaID, mediaVersion, thumbnailPath, centralURL := mediaRefValues(item.Media)
			if _, err = tx.Exec(`INSERT INTO catalog_products(namespace_id,id,code,name,brand,media_id,media_version,thumbnail_path,central_url,generation) VALUES(?,?,?,?,?,?,?,?,?,?) ON CONFLICT(namespace_id,id,generation) DO UPDATE SET code=excluded.code,name=excluded.name,brand=excluded.brand,media_id=excluded.media_id,media_version=excluded.media_version,thumbnail_path=excluded.thumbnail_path,central_url=excluded.central_url`, namespaceID, item.ID, item.Code, item.Name, item.Brand, mediaID, mediaVersion, thumbnailPath, centralURL, status.ActiveGeneration); err != nil {
				return err
			}
		case "sku":
			if change.Action == "delete" {
				if _, err = tx.Exec(`DELETE FROM catalog_skus WHERE namespace_id=? AND generation=? AND id=?`, namespaceID, status.ActiveGeneration, change.EntityID); err != nil {
					return err
				}
				continue
			}
			var item SKU
			if err = json.Unmarshal(change.Payload, &item); err != nil {
				return err
			}
			mediaID, mediaVersion, thumbnailPath, centralURL := mediaRefValues(item.Media)
			if _, err = tx.Exec(`INSERT INTO catalog_skus(namespace_id,id,code,name,spec,color,product_id,product_code,media_id,media_version,thumbnail_path,central_url,generation) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(namespace_id,id,generation) DO UPDATE SET code=excluded.code,name=excluded.name,spec=excluded.spec,color=excluded.color,product_id=excluded.product_id,product_code=excluded.product_code,media_id=excluded.media_id,media_version=excluded.media_version,thumbnail_path=excluded.thumbnail_path,central_url=excluded.central_url`, namespaceID, item.ID, item.Code, item.Name, item.Spec, item.Color, item.ProductID, item.ProductCode, mediaID, mediaVersion, thumbnailPath, centralURL, status.ActiveGeneration); err != nil {
				return err
			}
		case "box_label":
			if change.Action == "delete" {
				if _, err = tx.Exec(`DELETE FROM catalog_box_label_skus WHERE namespace_id=? AND generation=? AND box_label_id=?`, namespaceID, status.ActiveGeneration, change.EntityID); err != nil {
					return err
				}
				if _, err = tx.Exec(`DELETE FROM catalog_box_labels WHERE namespace_id=? AND generation=? AND id=?`, namespaceID, status.ActiveGeneration, change.EntityID); err != nil {
					return err
				}
				continue
			}
			var item BoxLabel
			if err = json.Unmarshal(change.Payload, &item); err != nil {
				return err
			}
			if err = upsertBoxLabelTx(tx, namespaceID, status.ActiveGeneration, item); err != nil {
				return err
			}
		}
	}
	if _, err = tx.Exec(`UPDATE catalog_meta SET revision=?, state='ready', last_error='' WHERE namespace_id=?`, revision, namespaceID); err != nil {
		return err
	}
	return tx.Commit()
}

func upsertBoxLabelTx(tx *sql.Tx, namespaceID uint, generation int64, item BoxLabel) error {
	payload, err := json.Marshal(item)
	if err != nil {
		return err
	}
	consolidationID, consolidationCode := uint(0), ""
	if item.ConsolidationOrder != nil {
		consolidationID, consolidationCode = item.ConsolidationOrder.ID, item.ConsolidationOrder.Code
	}
	if _, err = tx.Exec(`INSERT INTO catalog_box_labels(namespace_id,id,label_code,box_uid,box_no,supplier_order_code,consolidation_order_id,consolidation_order_code,status_group,receiving_status,created_at,payload,generation) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(namespace_id,id,generation) DO UPDATE SET label_code=excluded.label_code,box_uid=excluded.box_uid,box_no=excluded.box_no,supplier_order_code=excluded.supplier_order_code,consolidation_order_id=excluded.consolidation_order_id,consolidation_order_code=excluded.consolidation_order_code,status_group=excluded.status_group,receiving_status=excluded.receiving_status,created_at=excluded.created_at,payload=excluded.payload`, namespaceID, item.ID, item.LabelCode, item.BoxUID, item.BoxNo, item.SupplierOrder.Code, consolidationID, consolidationCode, item.StatusGroup, item.Receiving.Status, item.CreatedAt.UTC().Format(time.RFC3339Nano), payload, generation); err != nil {
		return err
	}
	if _, err = tx.Exec(`DELETE FROM catalog_box_label_skus WHERE namespace_id=? AND box_label_id=? AND generation=?`, namespaceID, item.ID, generation); err != nil {
		return err
	}
	for _, sku := range item.SKUItems {
		if _, err = tx.Exec(`INSERT INTO catalog_box_label_skus(namespace_id,box_label_id,sku_id,product_id,sku_code,generation) VALUES(?,?,?,?,?,?) ON CONFLICT(namespace_id,box_label_id,sku_id,generation) DO UPDATE SET product_id=excluded.product_id,sku_code=excluded.sku_code`, namespaceID, item.ID, sku.SKUID, sku.ProductID, sku.SKUCode, generation); err != nil {
			return err
		}
	}
	return nil
}

func (s *Store) RecordError(namespaceID uint, syncErr error) {
	if syncErr == nil {
		return
	}
	_, _ = s.db.Exec(`UPDATE catalog_meta SET state=CASE WHEN active_generation > 0 THEN 'ready' ELSE 'error' END, last_error=? WHERE namespace_id=?`, syncErr.Error(), namespaceID)
}

func (s *Store) SearchProducts(namespaceID uint, keyword string) ([]Product, error) {
	items, _, err := s.SearchProductsPage(namespaceID, keyword, PageFilter{Page: 1, PageSize: 100})
	return items, err
}

func (s *Store) SearchProductsPage(namespaceID uint, keyword string, page PageFilter) ([]Product, int64, error) {
	status, err := s.Status(namespaceID)
	if err != nil || status.ActiveGeneration == 0 {
		return nil, 0, err
	}
	page = normalizePageFilter(page)
	prefix := normalize(keyword)
	contains := "%" + strings.TrimSpace(keyword) + "%"
	args := []any{namespaceID, status.ActiveGeneration, prefix + "%", contains, contains, prefix + "%", contains, contains}
	where := `(code LIKE ? COLLATE NOCASE OR name LIKE ? COLLATE NOCASE OR brand LIKE ? COLLATE NOCASE OR EXISTS (SELECT 1 FROM catalog_skus s WHERE s.namespace_id=catalog_products.namespace_id AND s.generation=catalog_products.generation AND s.product_id=catalog_products.id AND (s.code LIKE ? COLLATE NOCASE OR s.name LIKE ? COLLATE NOCASE OR s.spec LIKE ? COLLATE NOCASE)))`
	var total int64
	if err = s.db.QueryRow(`SELECT COUNT(*) FROM catalog_products WHERE namespace_id=? AND generation=? AND `+where, args...).Scan(&total); err != nil {
		return nil, 0, err
	}
	args = append(args, page.PageSize, (page.Page-1)*page.PageSize)
	rows, err := s.db.Query(`SELECT id,code,name,brand,media_id,media_version,thumbnail_path,central_url FROM catalog_products WHERE namespace_id=? AND generation=? AND `+where+` ORDER BY code COLLATE NOCASE,id LIMIT ? OFFSET ?`, args...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()
	items := make([]Product, 0)
	for rows.Next() {
		var item Product
		var mediaID, mediaVersion, thumbnailPath, centralURL string
		if err := rows.Scan(&item.ID, &item.Code, &item.Name, &item.Brand, &mediaID, &mediaVersion, &thumbnailPath, &centralURL); err != nil {
			return nil, 0, err
		}
		item.Media = mediaRefFromValues(mediaID, mediaVersion, thumbnailPath, centralURL)
		items = append(items, item)
	}
	return items, total, rows.Err()
}

func (s *Store) SearchSKUs(namespaceID uint, keyword, qrPrefix string, productID *uint) ([]SKU, error) {
	items, _, err := s.SearchSKUsPage(namespaceID, keyword, qrPrefix, productID, PageFilter{Page: 1, PageSize: 100})
	return items, err
}

func (s *Store) SearchSKUsPage(namespaceID uint, keyword, qrPrefix string, productID *uint, page PageFilter) ([]SKU, int64, error) {
	return s.SearchSKUsPageMode(namespaceID, keyword, qrPrefix, productID, "prefix", page)
}

func (s *Store) SearchSKUsPageMode(namespaceID uint, keyword, qrPrefix string, productID *uint, matchMode string, page PageFilter) ([]SKU, int64, error) {
	status, err := s.Status(namespaceID)
	if err != nil || status.ActiveGeneration == 0 {
		return nil, 0, err
	}
	page = normalizePageFilter(page)
	prefixes := skuPrefixes(keyword, qrPrefix)
	if len(prefixes) == 0 {
		prefixes = []string{""}
	}
	conditions, args := make([]string, 0, len(prefixes)), []any{namespaceID, status.ActiveGeneration}
	for _, prefix := range prefixes {
		if strings.EqualFold(matchMode, "exact") {
			conditions = append(conditions, "code = ? COLLATE NOCASE")
			args = append(args, prefix)
		} else {
			conditions = append(conditions, "code LIKE ? COLLATE NOCASE", "product_code LIKE ? COLLATE NOCASE")
			args = append(args, prefix+"%", prefix+"%")
		}
	}
	if text := strings.TrimSpace(keyword); text != "" && !strings.EqualFold(matchMode, "exact") {
		contains := "%" + text + "%"
		conditions = append(conditions, "name LIKE ? COLLATE NOCASE", "spec LIKE ? COLLATE NOCASE", "color LIKE ? COLLATE NOCASE")
		args = append(args, contains, contains, contains)
	}
	where := `namespace_id=? AND generation=? AND (` + strings.Join(conditions, " OR ") + `)`
	if productID != nil {
		where += " AND product_id=?"
		args = append(args, *productID)
	}
	var total int64
	if err = s.db.QueryRow(`SELECT COUNT(*) FROM catalog_skus WHERE `+where, args...).Scan(&total); err != nil {
		return nil, 0, err
	}
	queryArgs := append(append([]any(nil), args...), page.PageSize, (page.Page-1)*page.PageSize)
	rows, err := s.db.Query(`SELECT id,code,name,spec,color,product_id,product_code,media_id,media_version,thumbnail_path,central_url FROM catalog_skus WHERE `+where+` ORDER BY code COLLATE NOCASE,id LIMIT ? OFFSET ?`, queryArgs...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()
	items := make([]SKU, 0)
	for rows.Next() {
		var item SKU
		var mediaID, mediaVersion, thumbnailPath, centralURL string
		if err := rows.Scan(&item.ID, &item.Code, &item.Name, &item.Spec, &item.Color, &item.ProductID, &item.ProductCode, &mediaID, &mediaVersion, &thumbnailPath, &centralURL); err != nil {
			return nil, 0, err
		}
		item.Media = mediaRefFromValues(mediaID, mediaVersion, thumbnailPath, centralURL)
		items = append(items, item)
	}
	return items, total, rows.Err()
}

func (s *Store) GetSKU(namespaceID uint, idOrCode string) (*SKU, error) {
	status, err := s.Status(namespaceID)
	if err != nil || status.ActiveGeneration == 0 {
		return nil, err
	}
	value := strings.TrimSpace(idOrCode)
	var item SKU
	query := `SELECT id,code,name,spec,color,product_id,product_code,media_id,media_version,thumbnail_path,central_url FROM catalog_skus WHERE namespace_id=? AND generation=? AND code=? COLLATE NOCASE LIMIT 1`
	args := []any{namespaceID, status.ActiveGeneration, value}
	if id, parseErr := strconv.ParseUint(value, 10, 32); parseErr == nil && id > 0 {
		query = `SELECT id,code,name,spec,color,product_id,product_code,media_id,media_version,thumbnail_path,central_url FROM catalog_skus WHERE namespace_id=? AND generation=? AND id=? LIMIT 1`
		args = []any{namespaceID, status.ActiveGeneration, uint(id)}
	}
	var mediaID, mediaVersion, thumbnailPath, centralURL string
	if err = s.db.QueryRow(query, args...).Scan(&item.ID, &item.Code, &item.Name, &item.Spec, &item.Color, &item.ProductID, &item.ProductCode, &mediaID, &mediaVersion, &thumbnailPath, &centralURL); err != nil {
		if err == sql.ErrNoRows {
			return nil, nil
		}
		return nil, err
	}
	item.Media = mediaRefFromValues(mediaID, mediaVersion, thumbnailPath, centralURL)
	return &item, nil
}

func mediaRefValues(media *MediaRef) (string, string, string, string) {
	if media == nil {
		return "", "", "", ""
	}
	return media.ID, media.Version, media.ThumbnailPath, media.CentralURL
}

func mediaRefFromValues(id, version, thumbnailPath, centralURL string) *MediaRef {
	if strings.TrimSpace(id) == "" || strings.TrimSpace(thumbnailPath) == "" {
		return nil
	}
	return &MediaRef{ID: id, Version: version, ThumbnailPath: thumbnailPath, CentralURL: centralURL}
}

func normalizePageFilter(page PageFilter) PageFilter {
	if page.Page < 1 {
		page.Page = 1
	}
	if page.PageSize < 1 || page.PageSize > 100 {
		page.PageSize = 20
	}
	return page
}

func (s *Store) SearchBoxLabels(namespaceID uint, filter BoxLabelFilter) ([]BoxLabel, int64, error) {
	status, err := s.Status(namespaceID)
	if err != nil || status.ActiveGeneration == 0 {
		return nil, 0, err
	}
	if filter.Page < 1 {
		filter.Page = 1
	}
	if filter.PageSize < 1 || filter.PageSize > 100 {
		filter.PageSize = 20
	}
	where, args := []string{"namespace_id=?", "generation=?"}, []any{namespaceID, status.ActiveGeneration}
	if keyword := strings.TrimSpace(filter.Keyword); keyword != "" {
		like := "%" + keyword + "%"
		where = append(where, `(label_code LIKE ? COLLATE NOCASE OR box_uid LIKE ? COLLATE NOCASE OR box_no LIKE ? COLLATE NOCASE OR supplier_order_code LIKE ? COLLATE NOCASE OR EXISTS (SELECT 1 FROM catalog_box_label_skus s WHERE s.namespace_id=catalog_box_labels.namespace_id AND s.generation=catalog_box_labels.generation AND s.box_label_id=catalog_box_labels.id AND s.sku_code LIKE ? COLLATE NOCASE))`)
		args = append(args, like, like, like, like, like)
	}
	if filter.ProductID > 0 {
		where = append(where, `EXISTS (SELECT 1 FROM catalog_box_label_skus s WHERE s.namespace_id=catalog_box_labels.namespace_id AND s.generation=catalog_box_labels.generation AND s.box_label_id=catalog_box_labels.id AND s.product_id=?)`)
		args = append(args, filter.ProductID)
	}
	if filter.SKUID > 0 {
		where = append(where, `EXISTS (SELECT 1 FROM catalog_box_label_skus s WHERE s.namespace_id=catalog_box_labels.namespace_id AND s.generation=catalog_box_labels.generation AND s.box_label_id=catalog_box_labels.id AND s.sku_id=?)`)
		args = append(args, filter.SKUID)
	}
	if filter.ConsolidationOrderID > 0 {
		where = append(where, "consolidation_order_id=?")
		args = append(args, filter.ConsolidationOrderID)
	}
	if code := strings.TrimSpace(filter.ConsolidationOrderCode); code != "" {
		where = append(where, "consolidation_order_code=? COLLATE NOCASE")
		args = append(args, code)
	}
	if value := strings.TrimSpace(filter.StatusGroup); value != "" {
		where = append(where, "status_group=?")
		args = append(args, value)
	}
	if value := strings.TrimSpace(filter.ReceivingStatus); value != "" {
		where = append(where, "receiving_status=?")
		args = append(args, value)
	}
	clause := strings.Join(where, " AND ")
	var total int64
	if err = s.db.QueryRow(`SELECT COUNT(*) FROM catalog_box_labels WHERE `+clause, args...).Scan(&total); err != nil {
		return nil, 0, err
	}
	queryArgs := append(append([]any(nil), args...), filter.PageSize, (filter.Page-1)*filter.PageSize)
	rows, err := s.db.Query(`SELECT payload FROM catalog_box_labels WHERE `+clause+` ORDER BY created_at DESC,id DESC LIMIT ? OFFSET ?`, queryArgs...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()
	items := make([]BoxLabel, 0)
	for rows.Next() {
		var payload []byte
		var item BoxLabel
		if err = rows.Scan(&payload); err != nil {
			return nil, 0, err
		}
		if err = json.Unmarshal(payload, &item); err != nil {
			return nil, 0, err
		}
		items = append(items, item)
	}
	return items, total, rows.Err()
}

func (s *Store) GetBoxLabel(namespaceID, id uint) (*BoxLabel, error) {
	status, err := s.Status(namespaceID)
	if err != nil || status.ActiveGeneration == 0 {
		return nil, err
	}
	var payload []byte
	if err = s.db.QueryRow(`SELECT payload FROM catalog_box_labels WHERE namespace_id=? AND generation=? AND id=?`, namespaceID, status.ActiveGeneration, id).Scan(&payload); err != nil {
		return nil, err
	}
	var item BoxLabel
	if err = json.Unmarshal(payload, &item); err != nil {
		return nil, err
	}
	return &item, nil
}

func (s *Store) ResolveBoxLabel(namespaceID uint, raw string) (*BoxLabel, error) {
	status, err := s.Status(namespaceID)
	if err != nil || status.ActiveGeneration == 0 {
		return nil, err
	}
	for _, value := range boxLabelLookupValues(raw) {
		var payload []byte
		err = s.db.QueryRow(`SELECT payload FROM catalog_box_labels WHERE namespace_id=? AND generation=? AND (label_code=? COLLATE NOCASE OR box_uid=? COLLATE NOCASE OR box_no=? COLLATE NOCASE) LIMIT 1`, namespaceID, status.ActiveGeneration, value, value, value).Scan(&payload)
		if err == sql.ErrNoRows {
			continue
		}
		if err != nil {
			return nil, err
		}
		var item BoxLabel
		if err = json.Unmarshal(payload, &item); err != nil {
			return nil, err
		}
		return &item, nil
	}
	return nil, sql.ErrNoRows
}

func boxLabelLookupValues(raw string) []string {
	value := strings.TrimSpace(raw)
	if value == "" {
		return nil
	}
	values := []string{value}
	add := func(candidate string) {
		candidate = strings.TrimSpace(candidate)
		if candidate == "" {
			return
		}
		for _, existing := range values {
			if strings.EqualFold(existing, candidate) {
				return
			}
		}
		values = append(values, candidate)
	}
	if strings.HasPrefix(value, "FSCMBOX:v1:") {
		add(strings.TrimPrefix(value, "FSCMBOX:v1:"))
	}
	if code, _, ok := strings.Cut(value, ","); ok {
		add(code)
	}
	if strings.HasPrefix(value, "FSCMBOX:v2|") || strings.HasPrefix(value, "FSCMBOX:v3|") {
		for _, part := range strings.Split(value, "|")[1:] {
			key, encoded, ok := strings.Cut(part, "=")
			if !ok {
				continue
			}
			key = strings.TrimSpace(key)
			if key != "u" && key != "uid" && key != "c" && key != "code" {
				continue
			}
			decoded, decodeErr := url.QueryUnescape(strings.TrimSpace(encoded))
			if decodeErr != nil {
				decoded = encoded
			}
			add(strings.NewReplacer("／", "|", "＝", "=", "；", ";").Replace(decoded))
		}
	}
	return values
}

func (s *Store) ensureMeta(namespaceID uint) error {
	_, err := s.db.Exec(`INSERT INTO catalog_meta(namespace_id) VALUES(?) ON CONFLICT(namespace_id) DO NOTHING`, namespaceID)
	return err
}

func normalize(value string) string {
	return strings.ToUpper(strings.Join(strings.Fields(strings.TrimSpace(value)), ""))
}

func skuPrefixes(keyword, qrPrefix string) []string {
	raw := normalize(keyword)
	if raw == "" {
		return nil
	}
	prefixes := []string{raw}
	qr := normalize(qrPrefix)
	if qr != "" && strings.HasPrefix(raw, qr) && len(raw) > len(qr) {
		prefixes = append(prefixes, raw[len(qr):])
	}
	return prefixes
}
