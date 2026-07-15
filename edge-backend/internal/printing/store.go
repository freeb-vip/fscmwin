package printing

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

type persistedJob struct {
	Job              Job    `json:"job"`
	RemoteJobID      uint   `json:"remote_job_id"`
	RemoteLeaseToken string `json:"remote_lease_token"`
}

func OpenStore(path string) (*Store, []Job, error) {
	if path == "" {
		return nil, nil, fmt.Errorf("print history database path is required")
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return nil, nil, err
	}
	db, err := sql.Open("sqlite", path+"?_pragma=busy_timeout(5000)&_pragma=journal_mode(WAL)")
	if err != nil {
		return nil, nil, err
	}
	store := &Store{db: db}
	if err := store.migrate(); err != nil {
		_ = db.Close()
		return nil, nil, err
	}
	jobs, err := store.loadJobs()
	if err != nil {
		_ = db.Close()
		return nil, nil, err
	}
	for index := range jobs {
		if jobs[index].Status == "printing" {
			jobs[index].Status = "interrupted"
			jobs[index].Error = "edge service restarted while this job was printing"
			now := time.Now()
			jobs[index].FinishedAt = &now
			if err := store.Save(jobs[index], jobs[index].RemoteJobID == 0); err != nil {
				_ = db.Close()
				return nil, nil, err
			}
		}
	}
	return store, jobs, nil
}

func (s *Store) Close() error { return s.db.Close() }

func (s *Store) migrate() error {
	_, err := s.db.Exec(`
CREATE TABLE IF NOT EXISTS print_jobs (
  id TEXT PRIMARY KEY,
  idempotency_key TEXT,
  status TEXT NOT NULL,
  submitted_at TEXT NOT NULL,
  payload BLOB NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_print_jobs_idempotency ON print_jobs(idempotency_key) WHERE idempotency_key <> '';
CREATE INDEX IF NOT EXISTS idx_print_jobs_status_submitted ON print_jobs(status, submitted_at DESC);
CREATE TABLE IF NOT EXISTS print_audit_outbox (
  job_id TEXT PRIMARY KEY,
  payload BLOB NOT NULL,
  created_at TEXT NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  last_error TEXT NOT NULL DEFAULT ''
);`)
	return err
}

func (s *Store) loadJobs() ([]Job, error) {
	rows, err := s.db.Query(`SELECT payload FROM print_jobs ORDER BY submitted_at DESC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	jobs := make([]Job, 0)
	for rows.Next() {
		var payload []byte
		if err := rows.Scan(&payload); err != nil {
			return nil, err
		}
		var saved persistedJob
		if err := json.Unmarshal(payload, &saved); err != nil {
			return nil, err
		}
		saved.Job.RemoteJobID = saved.RemoteJobID
		saved.Job.RemoteLeaseToken = saved.RemoteLeaseToken
		jobs = append(jobs, saved.Job)
	}
	return jobs, rows.Err()
}

func (s *Store) Save(job Job, enqueueAudit bool) error {
	saved := persistedJob{Job: job, RemoteJobID: job.RemoteJobID, RemoteLeaseToken: job.RemoteLeaseToken}
	payload, err := json.Marshal(saved)
	if err != nil {
		return err
	}
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	if _, err = tx.Exec(`INSERT INTO print_jobs(id, idempotency_key, status, submitted_at, payload) VALUES(?, ?, ?, ?, ?)
ON CONFLICT(id) DO UPDATE SET status=excluded.status, payload=excluded.payload`, job.ID, job.IdempotencyKey, job.Status, job.SubmittedAt.UTC().Format(time.RFC3339Nano), payload); err != nil {
		return err
	}
	if enqueueAudit {
		if _, err = tx.Exec(`INSERT INTO print_audit_outbox(job_id, payload, created_at, attempts, last_error) VALUES(?, ?, ?, 0, '')
ON CONFLICT(job_id) DO UPDATE SET payload=excluded.payload`, job.ID, payload, time.Now().UTC().Format(time.RFC3339Nano)); err != nil {
			return err
		}
	}
	return tx.Commit()
}

func (s *Store) PendingAudits(limit int) ([]Job, error) {
	rows, err := s.db.Query(`SELECT payload FROM print_audit_outbox ORDER BY created_at ASC LIMIT ?`, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	jobs := make([]Job, 0)
	for rows.Next() {
		var payload []byte
		if err := rows.Scan(&payload); err != nil {
			return nil, err
		}
		var saved persistedJob
		if err := json.Unmarshal(payload, &saved); err != nil {
			return nil, err
		}
		saved.Job.RemoteJobID = saved.RemoteJobID
		saved.Job.RemoteLeaseToken = saved.RemoteLeaseToken
		jobs = append(jobs, saved.Job)
	}
	return jobs, rows.Err()
}

func (s *Store) MarkAuditSynced(jobID string) error {
	_, err := s.db.Exec(`DELETE FROM print_audit_outbox WHERE job_id = ?`, jobID)
	return err
}

func (s *Store) MarkAuditFailed(jobID string, syncErr error) error {
	_, err := s.db.Exec(`UPDATE print_audit_outbox SET attempts = attempts + 1, last_error = ? WHERE job_id = ?`, syncErr.Error(), jobID)
	return err
}
