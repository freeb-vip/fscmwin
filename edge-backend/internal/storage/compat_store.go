package storage

import (
	"database/sql"
	"time"
)

func (s *Store) migrateCompatibility() error {
	_, err := s.db.Exec(`
CREATE TABLE IF NOT EXISTS nas_storage_compatibility (
  singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
  smb_compatibility_mode TEXT NOT NULL DEFAULT 'system_default',
  credential_format TEXT NOT NULL DEFAULT '',
  updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS nas_smb_signing_policy (
  singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
  managed INTEGER NOT NULL DEFAULT 0,
  previous_required INTEGER NOT NULL DEFAULT 0,
  transaction_state TEXT NOT NULL DEFAULT 'idle',
  updated_at TEXT NOT NULL
);`)
	return err
}

func (s *Store) LoadCompatibility() (string, string, error) {
	mode, format := SMBCompatibilityDefault, ""
	err := s.db.QueryRow(`SELECT smb_compatibility_mode, credential_format
FROM nas_storage_compatibility WHERE singleton = 1`).Scan(&mode, &format)
	if err == sql.ErrNoRows {
		return mode, format, nil
	}
	return mode, format, err
}

func (s *Store) LoadSigningPolicy() (SigningPolicyJournal, error) {
	var policy SigningPolicyJournal
	var managed, previous int
	err := s.db.QueryRow(`SELECT managed, previous_required, transaction_state
FROM nas_smb_signing_policy WHERE singleton = 1`).Scan(&managed, &previous, &policy.State)
	if err == sql.ErrNoRows {
		return SigningPolicyJournal{State: "idle"}, nil
	}
	if err != nil {
		return SigningPolicyJournal{}, err
	}
	policy.Managed = managed != 0
	policy.PreviousRequired = previous != 0
	return policy, nil
}

func (s *Store) SaveSigningPolicy(policy SigningPolicyJournal) error {
	return saveSigningPolicy(s.db, policy)
}

func (s *Store) SaveConfigAndSigningPolicy(cfg Config, policy SigningPolicyJournal) error {
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	if err := saveConfigTx(tx, cfg); err != nil {
		return err
	}
	if err := saveCompatibilityTx(tx, cfg); err != nil {
		return err
	}
	if err := saveSigningPolicy(tx, policy); err != nil {
		return err
	}
	return tx.Commit()
}

func saveConfigTx(tx *sql.Tx, cfg Config) error {
	_, err := tx.Exec(`INSERT INTO nas_storage_settings(singleton, enabled, local_path, retention_days, reserve_free_gb, share_name, username, user_sid, updated_at)
VALUES(1, ?, ?, ?, ?, ?, ?, ?, ?)
ON CONFLICT(singleton) DO UPDATE SET enabled=excluded.enabled, local_path=excluded.local_path,
retention_days=excluded.retention_days, reserve_free_gb=excluded.reserve_free_gb, share_name=excluded.share_name,
username=excluded.username, user_sid=excluded.user_sid, updated_at=excluded.updated_at`,
		boolInt(cfg.Enabled), cfg.LocalPath, cfg.RetentionDays, cfg.ReserveFreeGB, cfg.ShareName, cfg.Username, cfg.UserSID, time.Now().UTC().Format(time.RFC3339Nano))
	return err
}

func saveCompatibilityTx(tx *sql.Tx, cfg Config) error {
	mode := cfg.SMBCompatibilityMode
	if mode == "" {
		mode = SMBCompatibilityDefault
	}
	_, err := tx.Exec(`INSERT INTO nas_storage_compatibility(singleton, smb_compatibility_mode, credential_format, updated_at)
VALUES(1, ?, ?, ?)
ON CONFLICT(singleton) DO UPDATE SET smb_compatibility_mode=excluded.smb_compatibility_mode,
credential_format=excluded.credential_format, updated_at=excluded.updated_at`,
		mode, cfg.CredentialFormat, time.Now().UTC().Format(time.RFC3339Nano))
	return err
}

type sqlExecer interface {
	Exec(string, ...interface{}) (sql.Result, error)
}

func saveSigningPolicy(exec sqlExecer, policy SigningPolicyJournal) error {
	state := policy.State
	if state == "" {
		state = "idle"
	}
	_, err := exec.Exec(`INSERT INTO nas_smb_signing_policy(singleton, managed, previous_required, transaction_state, updated_at)
VALUES(1, ?, ?, ?, ?)
ON CONFLICT(singleton) DO UPDATE SET managed=excluded.managed, previous_required=excluded.previous_required,
transaction_state=excluded.transaction_state, updated_at=excluded.updated_at`,
		boolInt(policy.Managed), boolInt(policy.PreviousRequired), state, time.Now().UTC().Format(time.RFC3339Nano))
	return err
}
