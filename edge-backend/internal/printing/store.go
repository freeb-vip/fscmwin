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

type RemoteCompletion struct {
	RemoteJobID  uint            `json:"remote_job_id"`
	LocalJobID   string          `json:"local_job_id,omitempty"`
	LeaseToken   string          `json:"lease_token"`
	Status       string          `json:"status"`
	ErrorCode    string          `json:"error_code,omitempty"`
	ErrorMessage string          `json:"error_message,omitempty"`
	Result       json.RawMessage `json:"result,omitempty"`
	Attempts     int             `json:"attempts"`
	LastError    string          `json:"last_error,omitempty"`
}

type RemoteCompletionStatus struct {
	Pending   int    `json:"pending"`
	LastError string `json:"last_error,omitempty"`
}

const sqliteSortableTime = "2006-01-02T15:04:05.000000000Z07:00"

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
			var completion *RemoteCompletion
			if jobs[index].RemoteJobID > 0 {
				value := remoteCompletionForJob(jobs[index])
				completion = &value
			}
			if err := store.save(jobs[index], jobs[index].RemoteJobID == 0, completion); err != nil {
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
);
CREATE TABLE IF NOT EXISTS remote_print_completion_outbox (
  remote_job_id INTEGER PRIMARY KEY,
  local_job_id TEXT NOT NULL DEFAULT '',
  lease_token TEXT NOT NULL,
  status TEXT NOT NULL,
  error_code TEXT NOT NULL DEFAULT '',
  error_message TEXT NOT NULL DEFAULT '',
  result BLOB NOT NULL,
  created_at TEXT NOT NULL,
  next_attempt_at TEXT NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  last_error TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_remote_print_completion_due ON remote_print_completion_outbox(next_attempt_at, created_at);
`)
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
	return s.save(job, enqueueAudit, nil)
}

func (s *Store) SaveWithRemoteCompletion(job Job, completion RemoteCompletion) error {
	return s.save(job, false, &completion)
}

func (s *Store) save(job Job, enqueueAudit bool, completion *RemoteCompletion) error {
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
	if completion != nil {
		if err = enqueueRemoteCompletion(tx, *completion); err != nil {
			return err
		}
	}
	return tx.Commit()
}

func (s *Store) EnqueueRemoteCompletion(completion RemoteCompletion) error {
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	if err := enqueueRemoteCompletion(tx, completion); err != nil {
		return err
	}
	return tx.Commit()
}

func enqueueRemoteCompletion(tx *sql.Tx, completion RemoteCompletion) error {
	if len(completion.Result) == 0 {
		completion.Result = json.RawMessage(`{}`)
	}
	now := formatSQLiteTime(time.Now())
	_, err := tx.Exec(`INSERT INTO remote_print_completion_outbox(
remote_job_id, local_job_id, lease_token, status, error_code, error_message, result, created_at, next_attempt_at, attempts, last_error)
VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, 0, '')
ON CONFLICT(remote_job_id) DO UPDATE SET
local_job_id=excluded.local_job_id, lease_token=excluded.lease_token, status=excluded.status,
error_code=excluded.error_code, error_message=excluded.error_message, result=excluded.result,
next_attempt_at=excluded.next_attempt_at, attempts=0, last_error=''`,
		completion.RemoteJobID, completion.LocalJobID, completion.LeaseToken, completion.Status,
		completion.ErrorCode, completion.ErrorMessage, []byte(completion.Result), now, now)
	return err
}

func (s *Store) PendingRemoteCompletions(limit int) ([]RemoteCompletion, error) {
	if limit < 1 {
		limit = 50
	}
	rows, err := s.db.Query(`SELECT remote_job_id, local_job_id, lease_token, status, error_code, error_message, result, attempts, last_error
FROM remote_print_completion_outbox WHERE next_attempt_at <= ? ORDER BY created_at ASC LIMIT ?`, formatSQLiteTime(time.Now()), limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	values := make([]RemoteCompletion, 0)
	for rows.Next() {
		var value RemoteCompletion
		var result []byte
		if err := rows.Scan(&value.RemoteJobID, &value.LocalJobID, &value.LeaseToken, &value.Status, &value.ErrorCode, &value.ErrorMessage, &result, &value.Attempts, &value.LastError); err != nil {
			return nil, err
		}
		value.Result = append(json.RawMessage(nil), result...)
		values = append(values, value)
	}
	return values, rows.Err()
}

func (s *Store) MarkRemoteCompletionSynced(remoteJobID uint) error {
	_, err := s.db.Exec(`DELETE FROM remote_print_completion_outbox WHERE remote_job_id = ?`, remoteJobID)
	return err
}

func (s *Store) MarkRemoteCompletionFailed(remoteJobID uint, syncErr error, retryAt time.Time) error {
	_, err := s.db.Exec(`UPDATE remote_print_completion_outbox
SET attempts = attempts + 1, last_error = ?, next_attempt_at = ? WHERE remote_job_id = ?`,
		syncErr.Error(), formatSQLiteTime(retryAt), remoteJobID)
	return err
}

func (s *Store) RemoteCompletionStatus() (RemoteCompletionStatus, error) {
	var status RemoteCompletionStatus
	if err := s.db.QueryRow(`SELECT COUNT(*) FROM remote_print_completion_outbox`).Scan(&status.Pending); err != nil {
		return status, err
	}
	_ = s.db.QueryRow(`SELECT last_error FROM remote_print_completion_outbox WHERE last_error <> '' ORDER BY next_attempt_at DESC LIMIT 1`).Scan(&status.LastError)
	return status, nil
}

func remoteCompletionForJob(job Job) RemoteCompletion {
	status := "succeeded"
	errorCode := ""
	if job.Status != "completed" && job.Status != "done" && job.Status != "success" {
		status = "failed"
		errorCode = "PRINT_EXECUTION_FAILED"
		if job.Status == "interrupted" {
			errorCode = "EDGE_RESTART_INTERRUPTED"
		}
	}
	result, _ := json.Marshal(map[string]interface{}{"local_job_id": job.ID, "printer": job.Printer})
	return RemoteCompletion{RemoteJobID: job.RemoteJobID, LocalJobID: job.ID, LeaseToken: job.RemoteLeaseToken, Status: status, ErrorCode: errorCode, ErrorMessage: job.Error, Result: result}
}

func formatSQLiteTime(value time.Time) string {
	return value.UTC().Format(sqliteSortableTime)
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
