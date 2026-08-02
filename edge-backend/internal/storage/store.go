package storage

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"

	_ "modernc.org/sqlite"
)

type Store struct {
	db *sql.DB
}

func Open(path string) (*Store, error) {
	if path == "" {
		return nil, fmt.Errorf("storage database path is required")
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
	if err := store.migrateCompatibility(); err != nil {
		_ = db.Close()
		return nil, err
	}
	return store, nil
}

func (s *Store) Close() error { return s.db.Close() }

func (s *Store) migrate() error {
	_, err := s.db.Exec(`
CREATE TABLE IF NOT EXISTS nas_storage_settings (
  singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
  enabled INTEGER NOT NULL,
  local_path TEXT NOT NULL,
  retention_days INTEGER NOT NULL,
  reserve_free_gb INTEGER NOT NULL,
  share_name TEXT NOT NULL,
  username TEXT NOT NULL,
  user_sid TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS nas_storage_state (
  singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
  cleanup_json BLOB,
  last_error TEXT NOT NULL DEFAULT '',
  updated_at TEXT NOT NULL
);`)
	return err
}

func (s *Store) LoadConfig() (Config, error) {
	cfg := Config{RetentionDays: DefaultRetentionDays, ReserveFreeGB: DefaultReserveFreeGB, ShareName: ShareName}
	var enabled int
	err := s.db.QueryRow(`SELECT enabled, local_path, retention_days, reserve_free_gb, share_name, username, user_sid
FROM nas_storage_settings WHERE singleton = 1`).Scan(&enabled, &cfg.LocalPath, &cfg.RetentionDays, &cfg.ReserveFreeGB, &cfg.ShareName, &cfg.Username, &cfg.UserSID)
	if err == sql.ErrNoRows {
		return cfg, nil
	}
	if err != nil {
		return Config{}, err
	}
	cfg.Enabled = enabled != 0
	return cfg, nil
}

func (s *Store) SaveConfig(cfg Config) error {
	_, err := s.db.Exec(`INSERT INTO nas_storage_settings(singleton, enabled, local_path, retention_days, reserve_free_gb, share_name, username, user_sid, updated_at)
VALUES(1, ?, ?, ?, ?, ?, ?, ?, ?)
ON CONFLICT(singleton) DO UPDATE SET enabled=excluded.enabled, local_path=excluded.local_path,
retention_days=excluded.retention_days, reserve_free_gb=excluded.reserve_free_gb, share_name=excluded.share_name,
username=excluded.username, user_sid=excluded.user_sid, updated_at=excluded.updated_at`,
		boolInt(cfg.Enabled), cfg.LocalPath, cfg.RetentionDays, cfg.ReserveFreeGB, cfg.ShareName, cfg.Username, cfg.UserSID, time.Now().UTC().Format(time.RFC3339Nano))
	return err
}

func (s *Store) LoadCleanup() (*CleanupResult, string, error) {
	var raw []byte
	var lastError string
	err := s.db.QueryRow(`SELECT cleanup_json, last_error FROM nas_storage_state WHERE singleton = 1`).Scan(&raw, &lastError)
	if err == sql.ErrNoRows {
		return nil, "", nil
	}
	if err != nil {
		return nil, "", err
	}
	if len(raw) == 0 {
		return nil, lastError, nil
	}
	var result CleanupResult
	if err := json.Unmarshal(raw, &result); err != nil {
		return nil, lastError, err
	}
	return &result, lastError, nil
}

func (s *Store) SaveCleanup(result CleanupResult, lastError string) error {
	raw, err := json.Marshal(result)
	if err != nil {
		return err
	}
	_, err = s.db.Exec(`INSERT INTO nas_storage_state(singleton, cleanup_json, last_error, updated_at)
VALUES(1, ?, ?, ?)
ON CONFLICT(singleton) DO UPDATE SET cleanup_json=excluded.cleanup_json, last_error=excluded.last_error, updated_at=excluded.updated_at`,
		raw, lastError, time.Now().UTC().Format(time.RFC3339Nano))
	return err
}

func boolInt(value bool) int {
	if value {
		return 1
	}
	return 0
}
