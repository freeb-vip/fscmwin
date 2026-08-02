package storage

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"fmt"
	"math/big"
	"os"
	"strings"
	"sync"
	"time"
)

type Provisioner interface {
	Configure(context.Context, Config, string) (string, error)
	Disable(context.Context, Config) error
	Inspect(context.Context, Config) ProvisionStatus
	Remove(context.Context, Config) error
}

type SMBPolicyProvisioner interface {
	InspectSMBPolicy(context.Context) (SMBPolicy, error)
	SetSMBSigning(context.Context, bool) error
}

type Manager struct {
	store       *Store
	cleaner     *Cleaner
	provisioner Provisioner
	nodeID      string

	mu            sync.RWMutex
	config        Config
	lastCleanup   *CleanupResult
	lastWrite     *time.Time
	lastError     string
	policyWarning string
	signingPolicy SigningPolicyJournal
	cleaning      bool
	provision     ProvisionStatus
	inspectedAt   time.Time
}

func NewManager(store *Store, nodeID string, provisioner Provisioner) (*Manager, error) {
	cfg, err := store.LoadConfig()
	if err != nil {
		return nil, err
	}
	mode, credentialFormat, err := store.LoadCompatibility()
	if err != nil {
		return nil, err
	}
	if mode == "" {
		mode = SMBCompatibilityDefault
	}
	cfg.SMBCompatibilityMode, cfg.CredentialFormat = mode, credentialFormat
	policy, err := store.LoadSigningPolicy()
	if err != nil {
		return nil, err
	}
	cleanup, lastError, err := store.LoadCleanup()
	if err != nil {
		return nil, err
	}
	return &Manager{store: store, cleaner: NewCleaner(), provisioner: provisioner, nodeID: nodeID, config: cfg, signingPolicy: policy, lastCleanup: cleanup, lastError: lastError}, nil
}

func (m *Manager) Config() Config {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.config
}

func (m *Manager) Apply(ctx context.Context, requested Config) (ApplyResult, error) {
	requested.ShareName = ShareName
	requested.LocalPath = strings.TrimSpace(requested.LocalPath)
	requested.SMBCompatibilityMode = normalizeCompatibilityMode(requested.SMBCompatibilityMode)
	if requested.RetentionDays < MinimumRetentionDays || requested.RetentionDays > MaximumRetentionDays {
		return ApplyResult{}, fmt.Errorf("retention_days must be between %d and %d", MinimumRetentionDays, MaximumRetentionDays)
	}
	if requested.ReserveFreeGB < MinimumReserveFreeGB || requested.ReserveFreeGB > MaximumReserveFreeGB {
		return ApplyResult{}, fmt.Errorf("reserve_free_gb must be between %d and %d", MinimumReserveFreeGB, MaximumReserveFreeGB)
	}
	if err := validateCompatibilityMode(requested.SMBCompatibilityMode); err != nil {
		return ApplyResult{}, err
	}

	m.mu.Lock()
	defer m.mu.Unlock()
	current := m.config
	journalBefore := m.signingPolicy
	requested.Username, requested.UserSID = current.Username, current.UserSID
	requested.CredentialFormat = current.CredentialFormat
	policyBefore, err := m.inspectSMBPolicy(ctx)
	if err != nil {
		return ApplyResult{}, err
	}

	if !requested.Enabled {
		if requested.LocalPath == "" {
			requested.LocalPath = current.LocalPath
		}
		if err := m.provisioner.Disable(ctx, current); err != nil {
			return ApplyResult{}, err
		}
		finalJournal, err := m.applyCompatibilityPolicy(ctx, false, policyBefore)
		if err != nil {
			if current.Enabled {
				_, _ = m.provisioner.Configure(ctx, current, "")
			}
			m.rollbackCompatibilityPolicy(ctx, journalBefore, policyBefore)
			return ApplyResult{}, err
		}
		if err := m.store.SaveConfigAndSigningPolicy(requested, finalJournal); err != nil {
			if current.Enabled {
				_, _ = m.provisioner.Configure(ctx, current, "")
			}
			m.rollbackCompatibilityPolicy(ctx, journalBefore, policyBefore)
			return ApplyResult{}, err
		}
		m.config, m.signingPolicy = requested, finalJournal
		m.provision, m.inspectedAt, m.policyWarning = ProvisionStatus{}, time.Time{}, ""
		return ApplyResult{Config: requested, Status: m.statusLocked(ctx)}, nil
	}

	cleaned, _, err := m.cleaner.ValidatePath(requested.LocalPath, requested.ReserveFreeGB)
	if err != nil {
		return ApplyResult{}, err
	}
	requested.LocalPath = cleaned
	targetCompatibility := requested.SMBCompatibilityMode == SMBCompatibilityXiaomi
	currentCompatibility := current.Enabled && current.SMBCompatibilityMode == SMBCompatibilityXiaomi
	finalJournal := m.signingPolicy
	if targetCompatibility {
		finalJournal, err = m.applyCompatibilityPolicy(ctx, true, policyBefore)
		if err != nil {
			m.rollbackCompatibilityPolicy(ctx, journalBefore, policyBefore)
			return ApplyResult{}, err
		}
	}

	password := ""
	resetForCompatibility := targetCompatibility && requested.Username != "" &&
		((current.Enabled && current.SMBCompatibilityMode != SMBCompatibilityXiaomi) || current.CredentialFormat != CredentialFormatV2)
	if requested.Username == "" {
		requested.Username = managedUsername(m.nodeID)
	}
	if current.Username == "" || resetForCompatibility {
		password, err = generatePassword(16)
		if err != nil {
			m.rollbackCompatibilityPolicy(ctx, journalBefore, policyBefore)
			return ApplyResult{}, err
		}
		requested.CredentialFormat = CredentialFormatV2
	}
	sid, err := m.provisioner.Configure(ctx, requested, password)
	if err != nil {
		if current.Enabled {
			_, _ = m.provisioner.Configure(ctx, current, "")
		} else {
			_ = m.provisioner.Disable(ctx, requested)
		}
		m.rollbackCompatibilityPolicy(ctx, journalBefore, policyBefore)
		return ApplyResult{}, err
	}
	requested.UserSID = sid

	if !targetCompatibility && currentCompatibility {
		finalJournal, err = m.applyCompatibilityPolicy(ctx, false, policyBefore)
		if err != nil {
			_, _ = m.provisioner.Configure(ctx, current, "")
			m.rollbackCompatibilityPolicy(ctx, journalBefore, policyBefore)
			return ApplyResult{}, err
		}
	}
	if targetCompatibility && finalJournal.Managed {
		finalJournal.State = "idle"
	}
	if err := m.store.SaveConfigAndSigningPolicy(requested, finalJournal); err != nil {
		if current.Enabled {
			_, _ = m.provisioner.Configure(ctx, current, "")
		} else {
			_ = m.provisioner.Disable(ctx, requested)
		}
		m.rollbackCompatibilityPolicy(ctx, journalBefore, policyBefore)
		return ApplyResult{}, err
	}

	m.config, m.signingPolicy = requested, finalJournal
	m.provision = ProvisionStatus{ShareReady: true, AccountReady: true, FirewallReady: true}
	m.inspectedAt, m.policyWarning = time.Now(), ""
	var credentials *Credentials
	if password != "" {
		credentials = &Credentials{Username: requested.Username, Password: password, SharePaths: sharePaths()}
	}
	return ApplyResult{Config: requested, Status: m.statusLocked(ctx), Credentials: credentials}, nil
}
func (m *Manager) ResetCredentials(ctx context.Context) (Credentials, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if !m.config.Enabled || m.config.LocalPath == "" {
		return Credentials{}, fmt.Errorf("NAS storage is not enabled")
	}
	if m.config.Username == "" {
		m.config.Username = managedUsername(m.nodeID)
	}
	password, err := generatePassword(16)
	if err != nil {
		return Credentials{}, err
	}
	sid, err := m.provisioner.Configure(ctx, m.config, password)
	if err != nil {
		return Credentials{}, err
	}
	m.config.UserSID = sid
	m.config.CredentialFormat = CredentialFormatV2
	if err := m.store.SaveConfigAndSigningPolicy(m.config, m.signingPolicy); err != nil {
		return Credentials{}, err
	}
	m.provision = ProvisionStatus{ShareReady: true, AccountReady: true, FirewallReady: true}
	m.inspectedAt = time.Now()
	return Credentials{Username: m.config.Username, Password: password, SharePaths: sharePaths()}, nil
}

func (m *Manager) Status(ctx context.Context) Status {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.statusLocked(ctx)
}

func (m *Manager) statusLocked(ctx context.Context) Status {
	cfg := m.config
	status := Status{
		State: "disabled", Enabled: cfg.Enabled, ShareName: ShareName, SharePaths: sharePaths(),
		Username: cfg.Username, LocalPath: cfg.LocalPath, LastCleanup: m.lastCleanup, LastWriteAt: m.lastWrite,
		SMBCompatibilityMode:   normalizeCompatibilityMode(cfg.SMBCompatibilityMode),
		SigningOverrideManaged: m.signingPolicy.Managed,
	}
	m.fillSMBPolicyStatus(ctx, &status)
	if !cfg.Enabled {
		return status
	}
	status.State = "degraded"
	volume, err := m.cleaner.volumeInfo(cfg.LocalPath)
	if err == nil {
		status.VolumeTotal, status.VolumeFree = volume.TotalBytes, volume.FreeBytes
	} else {
		status.Error = err.Error()
	}
	if m.inspectedAt.IsZero() || time.Since(m.inspectedAt) >= 30*time.Second {
		m.provision = m.provisioner.Inspect(ctx, cfg)
		m.inspectedAt = time.Now()
	}
	provision := m.provision
	status.ShareReady, status.AccountReady, status.FirewallReady = provision.ShareReady, provision.AccountReady, provision.FirewallReady
	if provision.SMB1Enabled || provision.SMB2Enabled {
		status.SMB1Enabled = provision.SMB1Enabled
		status.SMB2Enabled = provision.SMB2Enabled
		status.SMBSigningRequired = provision.SMBSigningRequired
	}
	status.CompatibilityReady = cfg.SMBCompatibilityMode == SMBCompatibilityXiaomi &&
		!status.SMB1Enabled && status.SMB2Enabled && !status.SMBSigningRequired
	status.Ready = provision.ShareReady && provision.AccountReady && provision.FirewallReady && provision.Error == "" &&
		m.lastError == "" && status.Error == "" &&
		(cfg.SMBCompatibilityMode != SMBCompatibilityXiaomi || status.CompatibilityReady)
	if provision.Error != "" {
		status.Error = provision.Error
	}
	if m.lastError != "" {
		status.Error = m.lastError
	}
	if m.policyWarning != "" {
		status.Error = m.policyWarning
		status.Ready = false
	}
	if status.Ready {
		status.State = "ready"
	}
	if status.VolumeFree > 0 && status.VolumeFree < int64(cfg.ReserveFreeGB)<<30 {
		status.State = "critical"
		status.Ready = false
		if status.Error == "" {
			status.Error = "free space is below the configured reserve"
		}
	}
	return status
}
func (m *Manager) TriggerCleanup() bool {
	m.mu.Lock()
	if m.cleaning || !m.config.Enabled {
		m.mu.Unlock()
		return false
	}
	m.cleaning = true
	cfg := m.config
	m.mu.Unlock()
	go m.runCleanup(cfg)
	return true
}

func (m *Manager) runCleanup(cfg Config) {
	result, lastWrite, _ := m.cleaner.Run(cfg)
	lastError := ""
	if len(result.Errors) > 0 {
		lastError = strings.Join(result.Errors, "; ")
	}
	_ = m.store.SaveCleanup(result, lastError)
	m.mu.Lock()
	m.lastCleanup, m.lastWrite, m.lastError, m.cleaning = &result, lastWrite, lastError, false
	m.mu.Unlock()
}

func (m *Manager) Start(ctx context.Context) {
	m.recoverSigningTransition(ctx)
	m.TriggerCleanup()
	go m.reconcile(ctx)
	go func() {
		pressure := time.NewTicker(5 * time.Minute)
		retention := time.NewTicker(time.Hour)
		inspection := time.NewTicker(time.Minute)
		defer pressure.Stop()
		defer retention.Stop()
		defer inspection.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-retention.C:
				m.TriggerCleanup()
			case <-inspection.C:
				m.reconcile(ctx)
			case <-pressure.C:
				cfg := m.Config()
				if cfg.Enabled {
					if volume, err := m.cleaner.volumeInfo(cfg.LocalPath); err == nil && volume.FreeBytes < int64(cfg.ReserveFreeGB)<<30 {
						m.TriggerCleanup()
					}
				}
			}
		}
	}()
}

func (m *Manager) reconcile(ctx context.Context) {
	m.mu.Lock()
	defer m.mu.Unlock()
	cfg := m.config
	if !cfg.Enabled {
		return
	}
	m.reconcileCompatibilityLocked(ctx)
	provision := m.provisioner.Inspect(ctx, cfg)
	if provision.AccountReady && (!provision.ShareReady || !provision.FirewallReady) {
		sid, err := m.provisioner.Configure(ctx, cfg, "")
		if err != nil {
			provision.Error = err.Error()
		} else {
			provision = ProvisionStatus{ShareReady: true, AccountReady: true, FirewallReady: true}
			if cfg.UserSID == "" && sid != "" {
				cfg.UserSID = sid
				if err := m.store.SaveConfigAndSigningPolicy(cfg, m.signingPolicy); err == nil {
					m.config.UserSID = sid
				}
			}
		}
	}
	m.provision, m.inspectedAt = provision, time.Now()
}
func (m *Manager) RemoveSystemObjects(ctx context.Context) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	removeErr := m.provisioner.Remove(ctx, m.config)
	restoreErr := m.restoreSigningOverrideLocked(ctx)
	if removeErr != nil && restoreErr != nil {
		return fmt.Errorf("remove NAS objects: %v; restore SMB signing: %w", removeErr, restoreErr)
	}
	if removeErr != nil {
		return removeErr
	}
	return restoreErr
}

func managedUsername(nodeID string) string {
	sum := sha256.Sum256([]byte(strings.TrimSpace(nodeID)))
	return fmt.Sprintf("FscmNas_%x", sum[:4])
}

func generatePassword(length int) (string, error) {
	const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789"
	characters := []byte{'A', 'z', '7'}
	for len(characters) < length {
		index, err := rand.Int(rand.Reader, big.NewInt(int64(len(alphabet))))
		if err != nil {
			return "", err
		}
		characters = append(characters, alphabet[index.Int64()])
	}
	for index := len(characters) - 1; index > 0; index-- {
		other, err := rand.Int(rand.Reader, big.NewInt(int64(index+1)))
		if err != nil {
			return "", err
		}
		characters[index], characters[other.Int64()] = characters[other.Int64()], characters[index]
	}
	return string(characters), nil
}

func sharePaths() []string {
	hostname, _ := os.Hostname()
	paths := make([]string, 0, 2)
	if strings.TrimSpace(hostname) != "" {
		paths = append(paths, `\\`+hostname+`\`+ShareName)
	}
	if ip := localPrivateIPv4(); ip != "" {
		paths = append(paths, `\\`+ip+`\`+ShareName)
	}
	return paths
}
