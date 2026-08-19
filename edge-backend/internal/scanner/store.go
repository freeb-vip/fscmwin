package scanner

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"

	_ "modernc.org/sqlite"
)

type outboxItem struct {
	ID        string
	EventType string
	Payload   json.RawMessage
	Attempts  int
}

type Store struct{ db *sql.DB }

func OpenStore(path string) (*Store, error) {
	if path == "" {
		return nil, fmt.Errorf("scanner database path is required")
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
CREATE TABLE IF NOT EXISTS scanner_binding_cache (
  device_fingerprint TEXT PRIMARY KEY, payload BLOB NOT NULL, updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS scanner_work_session_cache (
  binding_id INTEGER PRIMARY KEY, payload BLOB NOT NULL, updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS scanner_capture_results (
  capture_id TEXT PRIMARY KEY, payload BLOB NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS scanner_outbox (
  id TEXT PRIMARY KEY, event_type TEXT NOT NULL, payload BLOB NOT NULL,
  created_at TEXT NOT NULL, next_attempt_at TEXT NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0, last_error TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_scanner_outbox_due ON scanner_outbox(next_attempt_at, created_at);
CREATE TABLE IF NOT EXISTS scanner_dead_letters (
  id TEXT PRIMARY KEY, event_type TEXT NOT NULL, payload BLOB NOT NULL,
  reason TEXT NOT NULL, failed_at TEXT NOT NULL
);`)
	return err
}

func (s *Store) replaceBindings(items map[string]Binding) error {
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	if _, err = tx.Exec(`DELETE FROM scanner_binding_cache`); err != nil {
		return err
	}
	now := time.Now().UTC().Format(time.RFC3339Nano)
	for fingerprint, item := range items {
		payload, marshalErr := json.Marshal(item)
		if marshalErr != nil {
			return marshalErr
		}
		if _, err = tx.Exec(`INSERT INTO scanner_binding_cache(device_fingerprint,payload,updated_at) VALUES(?,?,?)`, fingerprint, payload, now); err != nil {
			return err
		}
	}
	return tx.Commit()
}

func (s *Store) loadBindings() (map[string]Binding, error) {
	rows, err := s.db.Query(`SELECT device_fingerprint,payload FROM scanner_binding_cache`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	items := map[string]Binding{}
	for rows.Next() {
		var key string
		var payload []byte
		var item Binding
		if err := rows.Scan(&key, &payload); err != nil {
			return nil, err
		}
		if err := json.Unmarshal(payload, &item); err != nil {
			return nil, err
		}
		items[key] = item
	}
	return items, rows.Err()
}

func (s *Store) replaceWorkSessions(items map[uint]WorkSession) error {
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	if _, err = tx.Exec(`DELETE FROM scanner_work_session_cache`); err != nil {
		return err
	}
	now := time.Now().UTC().Format(time.RFC3339Nano)
	for id, item := range items {
		payload, marshalErr := json.Marshal(item)
		if marshalErr != nil {
			return marshalErr
		}
		if _, err = tx.Exec(`INSERT INTO scanner_work_session_cache(binding_id,payload,updated_at) VALUES(?,?,?)`, id, payload, now); err != nil {
			return err
		}
	}
	return tx.Commit()
}

func (s *Store) loadWorkSessions() (map[uint]WorkSession, error) {
	rows, err := s.db.Query(`SELECT binding_id,payload FROM scanner_work_session_cache`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	items := map[uint]WorkSession{}
	for rows.Next() {
		var id uint
		var payload []byte
		var item WorkSession
		if err := rows.Scan(&id, &payload); err != nil {
			return nil, err
		}
		if err := json.Unmarshal(payload, &item); err != nil {
			return nil, err
		}
		items[id] = item
	}
	return items, rows.Err()
}

func (s *Store) captureResult(id string) (InputResult, bool, error) {
	var payload []byte
	err := s.db.QueryRow(`SELECT payload FROM scanner_capture_results WHERE capture_id=?`, id).Scan(&payload)
	if err == sql.ErrNoRows {
		return InputResult{}, false, nil
	}
	if err != nil {
		return InputResult{}, false, err
	}
	var result InputResult
	if err := json.Unmarshal(payload, &result); err != nil {
		return InputResult{}, false, err
	}
	return result, true, nil
}

func (s *Store) saveCapture(id string, result InputResult) error {
	payload, err := json.Marshal(result)
	if err != nil {
		return err
	}
	_, err = s.db.Exec(`INSERT OR IGNORE INTO scanner_capture_results(capture_id,payload,created_at) VALUES(?,?,?)`, id, payload, time.Now().UTC().Format(time.RFC3339Nano))
	return err
}

func (s *Store) enqueue(id, eventType string, payload interface{}) error {
	body, err := json.Marshal(payload)
	if err != nil {
		return err
	}
	var count int
	if err := s.db.QueryRow(`SELECT COUNT(*) FROM scanner_outbox`).Scan(&count); err != nil {
		return err
	}
	if count >= 10000 {
		return fmt.Errorf("scanner outbox capacity reached")
	}
	now := time.Now().UTC().Format(time.RFC3339Nano)
	_, err = s.db.Exec(`INSERT OR IGNORE INTO scanner_outbox(id,event_type,payload,created_at,next_attempt_at) VALUES(?,?,?,?,?)`, id, eventType, body, now, now)
	return err
}

func (s *Store) due(limit int) ([]outboxItem, error) {
	rows, err := s.db.Query(`SELECT id,event_type,payload,attempts FROM scanner_outbox WHERE next_attempt_at<=? ORDER BY created_at LIMIT ?`, time.Now().UTC().Format(time.RFC3339Nano), limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	items := []outboxItem{}
	for rows.Next() {
		var item outboxItem
		var payload []byte
		if err := rows.Scan(&item.ID, &item.EventType, &payload, &item.Attempts); err != nil {
			return nil, err
		}
		item.Payload = append(json.RawMessage(nil), payload...)
		items = append(items, item)
	}
	return items, rows.Err()
}

func (s *Store) delivered(id string) error {
	_, err := s.db.Exec(`DELETE FROM scanner_outbox WHERE id=?`, id)
	return err
}

func (s *Store) retry(item outboxItem, reason string, at time.Time) error {
	_, err := s.db.Exec(`UPDATE scanner_outbox SET attempts=attempts+1,last_error=?,next_attempt_at=? WHERE id=?`, reason, at.UTC().Format(time.RFC3339Nano), item.ID)
	return err
}

func (s *Store) deadLetter(item outboxItem, reason string) error {
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	if _, err = tx.Exec(`INSERT OR REPLACE INTO scanner_dead_letters(id,event_type,payload,reason,failed_at) VALUES(?,?,?,?,?)`, item.ID, item.EventType, []byte(item.Payload), reason, time.Now().UTC().Format(time.RFC3339Nano)); err != nil {
		return err
	}
	if _, err = tx.Exec(`DELETE FROM scanner_outbox WHERE id=?`, item.ID); err != nil {
		return err
	}
	return tx.Commit()
}

func (s *Store) queueStatus() (pending, dead int, lastError string) {
	_ = s.db.QueryRow(`SELECT COUNT(*) FROM scanner_outbox`).Scan(&pending)
	_ = s.db.QueryRow(`SELECT COUNT(*) FROM scanner_dead_letters`).Scan(&dead)
	_ = s.db.QueryRow(`SELECT reason FROM scanner_dead_letters ORDER BY failed_at DESC LIMIT 1`).Scan(&lastError)
	if lastError == "" {
		_ = s.db.QueryRow(`SELECT last_error FROM scanner_outbox WHERE last_error<>'' ORDER BY next_attempt_at DESC LIMIT 1`).Scan(&lastError)
	}
	return
}
