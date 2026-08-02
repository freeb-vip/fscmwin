package storage

import "time"

const (
	ShareName               = "FscmRecordings"
	SMBCompatibilityDefault = "system_default"
	SMBCompatibilityXiaomi  = "xiaomi_smb2"
	CredentialFormatV2      = "alphanumeric_16_v2"
	DefaultRetentionDays    = 7
	DefaultReserveFreeGB    = 10
	MinimumRetentionDays    = 1
	MaximumRetentionDays    = 90
	MinimumReserveFreeGB    = 1
	MaximumReserveFreeGB    = 1024
	activeFileGracePeriod   = 15 * time.Minute
)

type Config struct {
	Enabled              bool   `json:"enabled"`
	LocalPath            string `json:"local_path"`
	RetentionDays        int    `json:"retention_days"`
	ReserveFreeGB        int    `json:"reserve_free_gb"`
	ShareName            string `json:"share_name"`
	Username             string `json:"username,omitempty"`
	UserSID              string `json:"-"`
	SMBCompatibilityMode string `json:"smb_compatibility_mode"`
	CredentialFormat     string `json:"-"`
}

type Credentials struct {
	Username   string   `json:"username"`
	Password   string   `json:"password"`
	SharePaths []string `json:"share_paths"`
}

type CleanupResult struct {
	StartedAt    time.Time `json:"started_at"`
	FinishedAt   time.Time `json:"finished_at"`
	DeletedFiles int       `json:"deleted_files"`
	FreedBytes   int64     `json:"freed_bytes"`
	Errors       []string  `json:"errors,omitempty"`
}

type Status struct {
	State                  string         `json:"state"`
	Enabled                bool           `json:"enabled"`
	Ready                  bool           `json:"ready"`
	ShareName              string         `json:"share_name"`
	SharePaths             []string       `json:"share_paths"`
	Username               string         `json:"username,omitempty"`
	LocalPath              string         `json:"local_path,omitempty"`
	VolumeTotal            int64          `json:"volume_total_bytes"`
	VolumeFree             int64          `json:"volume_free_bytes"`
	LastWriteAt            *time.Time     `json:"last_write_at,omitempty"`
	LastCleanup            *CleanupResult `json:"last_cleanup,omitempty"`
	ShareReady             bool           `json:"share_ready"`
	AccountReady           bool           `json:"account_ready"`
	FirewallReady          bool           `json:"firewall_ready"`
	SMBCompatibilityMode   string         `json:"smb_compatibility_mode"`
	SMB1Enabled            bool           `json:"smb1_enabled"`
	SMB2Enabled            bool           `json:"smb2_enabled"`
	SMBSigningRequired     bool           `json:"smb_signing_required"`
	SigningOverrideManaged bool           `json:"signing_override_managed"`
	CompatibilityReady     bool           `json:"compatibility_ready"`
	Error                  string         `json:"error,omitempty"`
}

type ApplyResult struct {
	Config      Config       `json:"config"`
	Status      Status       `json:"status"`
	Credentials *Credentials `json:"credentials,omitempty"`
}

type ProvisionStatus struct {
	ShareReady         bool
	AccountReady       bool
	FirewallReady      bool
	SMB1Enabled        bool
	SMB2Enabled        bool
	SMBSigningRequired bool
	Error              string
}

type SMBPolicy struct {
	SMB1Enabled     bool
	SMB2Enabled     bool
	SigningRequired bool
}

type SigningPolicyJournal struct {
	Managed          bool
	PreviousRequired bool
	State            string
}

type VolumeInfo struct {
	TotalBytes int64
	FreeBytes  int64
}
