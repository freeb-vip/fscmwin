package storage

import (
	"context"
	"database/sql"
	"errors"
	"path/filepath"
	"testing"
)

type fakeSMBProvisioner struct {
	fakeProvisioner
	policy   SMBPolicy
	setCalls []bool
	setErr   error
}

func (f *fakeSMBProvisioner) InspectSMBPolicy(context.Context) (SMBPolicy, error) {
	return f.policy, nil
}

func (f *fakeSMBProvisioner) SetSMBSigning(_ context.Context, required bool) error {
	f.setCalls = append(f.setCalls, required)
	if f.setErr != nil {
		return f.setErr
	}
	f.policy.SigningRequired = required
	return nil
}

func TestCompatibilityModeOwnsAndRestoresRequiredSigning(t *testing.T) {
	store, err := Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	provisioner := &fakeSMBProvisioner{
		fakeProvisioner: fakeProvisioner{sid: "S-1-test", status: ProvisionStatus{ShareReady: true, AccountReady: true, FirewallReady: true}},
		policy:          SMBPolicy{SMB2Enabled: true, SigningRequired: true},
	}
	manager, err := NewManager(store, "node", provisioner)
	if err != nil {
		t.Fatal(err)
	}
	root := t.TempDir()
	standard, err := manager.Apply(context.Background(), Config{
		Enabled: true, LocalPath: root, RetentionDays: 7, ReserveFreeGB: 1,
		SMBCompatibilityMode: SMBCompatibilityDefault,
	})
	if err != nil {
		t.Fatal(err)
	}
	compatible, err := manager.Apply(context.Background(), Config{
		Enabled: true, LocalPath: root, RetentionDays: 7, ReserveFreeGB: 1,
		SMBCompatibilityMode: SMBCompatibilityXiaomi,
	})
	if err != nil {
		t.Fatal(err)
	}
	if compatible.Credentials == nil || len(compatible.Credentials.Password) != 16 {
		t.Fatalf("mode transition did not return new one-time credentials: %+v", compatible.Credentials)
	}
	if standard.Credentials == nil || standard.Credentials.Password == compatible.Credentials.Password {
		t.Fatal("mode transition did not reset the existing NAS password")
	}
	if provisioner.policy.SigningRequired || !manager.signingPolicy.Managed || manager.signingPolicy.State != "idle" {
		t.Fatalf("signing override was not applied transactionally: policy=%+v journal=%+v", provisioner.policy, manager.signingPolicy)
	}

	_, err = manager.Apply(context.Background(), Config{
		Enabled: true, LocalPath: root, RetentionDays: 7, ReserveFreeGB: 1,
		SMBCompatibilityMode: SMBCompatibilityDefault,
	})
	if err != nil {
		t.Fatal(err)
	}
	if !provisioner.policy.SigningRequired || manager.signingPolicy.Managed {
		t.Fatalf("original signing setting was not restored: policy=%+v journal=%+v", provisioner.policy, manager.signingPolicy)
	}
}

func TestCompatibilityModeDoesNotOwnAnExistingDisabledSigningPolicy(t *testing.T) {
	store, err := Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	provisioner := &fakeSMBProvisioner{
		fakeProvisioner: fakeProvisioner{sid: "S-1-test", status: ProvisionStatus{ShareReady: true, AccountReady: true, FirewallReady: true}},
		policy:          SMBPolicy{SMB2Enabled: true, SigningRequired: false},
	}
	manager, _ := NewManager(store, "node", provisioner)
	result, err := manager.Apply(context.Background(), Config{
		Enabled: true, LocalPath: t.TempDir(), RetentionDays: 7, ReserveFreeGB: 1,
		SMBCompatibilityMode: SMBCompatibilityXiaomi,
	})
	if err != nil {
		t.Fatal(err)
	}
	if result.Status.SigningOverrideManaged || manager.signingPolicy.Managed {
		t.Fatalf("FSCM incorrectly claimed an existing system policy: %+v", result.Status)
	}
	_, err = manager.Apply(context.Background(), Config{
		Enabled: false, RetentionDays: 7, ReserveFreeGB: 1,
		SMBCompatibilityMode: SMBCompatibilityXiaomi,
	})
	if err != nil {
		t.Fatal(err)
	}
	if provisioner.policy.SigningRequired || len(provisioner.setCalls) != 0 {
		t.Fatalf("disable changed an externally managed policy: calls=%v policy=%+v", provisioner.setCalls, provisioner.policy)
	}
}

func TestCompatibilityTransitionRecoveryUsesCommittedConfig(t *testing.T) {
	store, err := Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	cfg := Config{
		Enabled: true, LocalPath: t.TempDir(), RetentionDays: 7, ReserveFreeGB: 1,
		ShareName: ShareName, Username: "FscmNas_test", UserSID: "S-1-test",
		SMBCompatibilityMode: SMBCompatibilityXiaomi, CredentialFormat: CredentialFormatV2,
	}
	journal := SigningPolicyJournal{Managed: true, PreviousRequired: true, State: "applying"}
	if err := store.SaveConfigAndSigningPolicy(cfg, journal); err != nil {
		t.Fatal(err)
	}
	provisioner := &fakeSMBProvisioner{
		fakeProvisioner: fakeProvisioner{sid: cfg.UserSID},
		policy:          SMBPolicy{SMB2Enabled: true, SigningRequired: true},
	}
	manager, err := NewManager(store, "node", provisioner)
	if err != nil {
		t.Fatal(err)
	}
	manager.recoverSigningTransition(context.Background())
	if provisioner.policy.SigningRequired || manager.signingPolicy.State != "idle" || !manager.signingPolicy.Managed {
		t.Fatalf("startup did not finish the committed transition: policy=%+v journal=%+v", provisioner.policy, manager.signingPolicy)
	}
}

func TestCompatibilityModeRejectsUnsafeProtocolState(t *testing.T) {
	store, err := Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	provisioner := &fakeSMBProvisioner{
		fakeProvisioner: fakeProvisioner{sid: "S-1-test"},
		policy:          SMBPolicy{SMB1Enabled: true, SMB2Enabled: true, SigningRequired: true},
	}
	manager, _ := NewManager(store, "node", provisioner)
	_, err = manager.Apply(context.Background(), Config{
		Enabled: true, LocalPath: t.TempDir(), RetentionDays: 7, ReserveFreeGB: 1,
		SMBCompatibilityMode: SMBCompatibilityXiaomi,
	})
	if err == nil {
		t.Fatal("xiaomi_smb2 accepted a host with SMB1 enabled")
	}
}

func TestCompatibilityMigrationOpensLegacyStorageDatabase(t *testing.T) {
	path := filepath.Join(t.TempDir(), "legacy.db")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	_, err = db.Exec(`
CREATE TABLE nas_storage_settings (
  singleton INTEGER PRIMARY KEY, enabled INTEGER NOT NULL, local_path TEXT NOT NULL,
  retention_days INTEGER NOT NULL, reserve_free_gb INTEGER NOT NULL, share_name TEXT NOT NULL,
  username TEXT NOT NULL, user_sid TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE TABLE nas_storage_state (
  singleton INTEGER PRIMARY KEY, cleanup_json BLOB, last_error TEXT NOT NULL DEFAULT '',
  updated_at TEXT NOT NULL
);`)
	if err != nil {
		t.Fatal(err)
	}
	if err := db.Close(); err != nil {
		t.Fatal(err)
	}
	store, err := Open(path)
	if err != nil {
		t.Fatalf("open migrated database: %v", err)
	}
	defer store.Close()
	mode, format, err := store.LoadCompatibility()
	if err != nil || mode != SMBCompatibilityDefault || format != "" {
		t.Fatalf("legacy defaults were not migrated: mode=%q format=%q err=%v", mode, format, err)
	}
}

func TestCompatibilitySigningFailureRollsBackJournalImmediately(t *testing.T) {
	store, err := Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	provisioner := &fakeSMBProvisioner{
		fakeProvisioner: fakeProvisioner{sid: "S-1-test"},
		policy:          SMBPolicy{SMB2Enabled: true, SigningRequired: true},
		setErr:          errors.New("access denied"),
	}
	manager, _ := NewManager(store, "node", provisioner)
	_, err = manager.Apply(context.Background(), Config{
		Enabled: true, LocalPath: t.TempDir(), RetentionDays: 7, ReserveFreeGB: 1,
		SMBCompatibilityMode: SMBCompatibilityXiaomi,
	})
	if err == nil {
		t.Fatal("expected signing configuration failure")
	}
	journal, loadErr := store.LoadSigningPolicy()
	if loadErr != nil {
		t.Fatal(loadErr)
	}
	if journal.Managed || journal.State != "idle" || manager.signingPolicy.Managed {
		t.Fatalf("failed policy change left an owned transaction: persisted=%+v manager=%+v", journal, manager.signingPolicy)
	}
}
