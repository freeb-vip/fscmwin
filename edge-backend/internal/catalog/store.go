package catalog

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	_ "modernc.org/sqlite"
)

type Product struct {
	ID    uint   `json:"id"`
	Code  string `json:"code"`
	Name  string `json:"name"`
	Brand string `json:"brand"`
}

type SKU struct {
	ID          uint   `json:"id"`
	Code        string `json:"code"`
	Name        string `json:"name"`
	Spec        string `json:"spec"`
	Color       string `json:"color"`
	ProductID   uint   `json:"product_id"`
	ProductCode string `json:"product_code"`
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
);
CREATE TABLE IF NOT EXISTS catalog_products (
  namespace_id INTEGER NOT NULL,
  id INTEGER NOT NULL,
  code TEXT NOT NULL COLLATE NOCASE,
  name TEXT NOT NULL DEFAULT '',
  brand TEXT NOT NULL DEFAULT '',
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
  generation INTEGER NOT NULL,
  PRIMARY KEY(namespace_id, id, generation)
);
CREATE INDEX IF NOT EXISTS idx_catalog_products_prefix ON catalog_products(namespace_id, generation, code COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_catalog_skus_prefix ON catalog_skus(namespace_id, generation, code COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_catalog_skus_product ON catalog_skus(namespace_id, generation, product_id);`)
	return err
}

func (s *Store) Status(namespaceID uint) (Status, error) {
	if err := s.ensureMeta(namespaceID); err != nil {
		return Status{}, err
	}
	var status Status
	var full string
	err := s.db.QueryRow(`SELECT active_generation, revision, last_full_sync_at, state, last_error FROM catalog_meta WHERE namespace_id = ?`, namespaceID).
		Scan(&status.ActiveGeneration, &status.Revision, &full, &status.State, &status.LastError)
	if err != nil {
		return Status{}, err
	}
	status.Ready = status.ActiveGeneration > 0 && status.State != "empty"
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
	statement, err := tx.Prepare(`INSERT INTO catalog_products(namespace_id,id,code,name,brand,generation) VALUES(?,?,?,?,?,?) ON CONFLICT(namespace_id,id,generation) DO UPDATE SET code=excluded.code,name=excluded.name,brand=excluded.brand`)
	if err != nil {
		return err
	}
	defer statement.Close()
	for _, item := range items {
		if _, err := statement.Exec(namespaceID, item.ID, item.Code, item.Name, item.Brand, generation); err != nil {
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
	statement, err := tx.Prepare(`INSERT INTO catalog_skus(namespace_id,id,code,name,spec,color,product_id,product_code,generation) VALUES(?,?,?,?,?,?,?,?,?) ON CONFLICT(namespace_id,id,generation) DO UPDATE SET code=excluded.code,name=excluded.name,spec=excluded.spec,color=excluded.color,product_id=excluded.product_id,product_code=excluded.product_code`)
	if err != nil {
		return err
	}
	defer statement.Close()
	for _, item := range items {
		if _, err := statement.Exec(namespaceID, item.ID, item.Code, item.Name, item.Spec, item.Color, item.ProductID, item.ProductCode, generation); err != nil {
			return err
		}
	}
	return tx.Commit()
}

func (s *Store) FinishFullSync(namespaceID uint, generation int64, revision uint64) error {
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	now := time.Now().UTC().Format(time.RFC3339Nano)
	if _, err = tx.Exec(`UPDATE catalog_meta SET active_generation=?, revision=?, last_full_sync_at=?, state='ready', last_error='' WHERE namespace_id=?`, generation, revision, now, namespaceID); err != nil {
		return err
	}
	if _, err = tx.Exec(`DELETE FROM catalog_products WHERE namespace_id=? AND generation<>?`, namespaceID, generation); err != nil {
		return err
	}
	if _, err = tx.Exec(`DELETE FROM catalog_skus WHERE namespace_id=? AND generation<>?`, namespaceID, generation); err != nil {
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
			if _, err = tx.Exec(`INSERT INTO catalog_products(namespace_id,id,code,name,brand,generation) VALUES(?,?,?,?,?,?) ON CONFLICT(namespace_id,id,generation) DO UPDATE SET code=excluded.code,name=excluded.name,brand=excluded.brand`, namespaceID, item.ID, item.Code, item.Name, item.Brand, status.ActiveGeneration); err != nil {
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
			if _, err = tx.Exec(`INSERT INTO catalog_skus(namespace_id,id,code,name,spec,color,product_id,product_code,generation) VALUES(?,?,?,?,?,?,?,?,?) ON CONFLICT(namespace_id,id,generation) DO UPDATE SET code=excluded.code,name=excluded.name,spec=excluded.spec,color=excluded.color,product_id=excluded.product_id,product_code=excluded.product_code`, namespaceID, item.ID, item.Code, item.Name, item.Spec, item.Color, item.ProductID, item.ProductCode, status.ActiveGeneration); err != nil {
				return err
			}
		}
	}
	if _, err = tx.Exec(`UPDATE catalog_meta SET revision=?, state='ready', last_error='' WHERE namespace_id=?`, revision, namespaceID); err != nil {
		return err
	}
	return tx.Commit()
}

func (s *Store) RecordError(namespaceID uint, syncErr error) {
	if syncErr == nil {
		return
	}
	_, _ = s.db.Exec(`UPDATE catalog_meta SET state=CASE WHEN active_generation > 0 THEN 'ready' ELSE 'error' END, last_error=? WHERE namespace_id=?`, syncErr.Error(), namespaceID)
}

func (s *Store) SearchProducts(namespaceID uint, keyword string) ([]Product, error) {
	status, err := s.Status(namespaceID)
	if err != nil || status.ActiveGeneration == 0 {
		return nil, err
	}
	prefix := normalize(keyword)
	rows, err := s.db.Query(`SELECT id,code,name,brand FROM catalog_products WHERE namespace_id=? AND generation=? AND (code LIKE ? COLLATE NOCASE OR name LIKE ? COLLATE NOCASE) ORDER BY code COLLATE NOCASE,id`, namespaceID, status.ActiveGeneration, prefix+"%", "%"+prefix+"%")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	items := make([]Product, 0)
	for rows.Next() {
		var item Product
		if err := rows.Scan(&item.ID, &item.Code, &item.Name, &item.Brand); err != nil {
			return nil, err
		}
		items = append(items, item)
	}
	return items, rows.Err()
}

func (s *Store) SearchSKUs(namespaceID uint, keyword, qrPrefix string, productID *uint) ([]SKU, error) {
	status, err := s.Status(namespaceID)
	if err != nil || status.ActiveGeneration == 0 {
		return nil, err
	}
	prefixes := skuPrefixes(keyword, qrPrefix)
	if len(prefixes) == 0 {
		prefixes = []string{""}
	}
	conditions, args := make([]string, 0, len(prefixes)), []any{namespaceID, status.ActiveGeneration}
	for _, prefix := range prefixes {
		conditions, args = append(conditions, "code LIKE ? COLLATE NOCASE"), append(args, prefix+"%")
	}
	query := `SELECT id,code,name,spec,color,product_id,product_code FROM catalog_skus WHERE namespace_id=? AND generation=? AND (` + strings.Join(conditions, " OR ") + `)`
	if productID != nil {
		query += " AND product_id=?"
		args = append(args, *productID)
	}
	query += " ORDER BY code COLLATE NOCASE,id"
	rows, err := s.db.Query(query, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	items := make([]SKU, 0)
	for rows.Next() {
		var item SKU
		if err := rows.Scan(&item.ID, &item.Code, &item.Name, &item.Spec, &item.Color, &item.ProductID, &item.ProductCode); err != nil {
			return nil, err
		}
		items = append(items, item)
	}
	return items, rows.Err()
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
