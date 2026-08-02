package storage

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestCleanerDeletesExpiredFilesAndKeepsActiveFiles(t *testing.T) {
	root := t.TempDir()
	now := time.Date(2026, 7, 23, 10, 0, 0, 0, time.UTC)
	expired := writeTestFile(t, root, "camera-a/expired.mp4", "expired", now.Add(-8*24*time.Hour))
	retained := writeTestFile(t, root, "camera-a/retained.mp4", "retained", now.Add(-6*24*time.Hour))
	active := writeTestFile(t, root, "camera-a/active.mp4", "active", now.Add(-5*time.Minute))
	cleaner := NewCleaner()
	cleaner.now = func() time.Time { return now }
	cleaner.volumeInfo = func(string) (VolumeInfo, error) {
		return VolumeInfo{TotalBytes: 100 << 30, FreeBytes: 20 << 30}, nil
	}

	result, lastWrite, _ := cleaner.Run(Config{LocalPath: root, RetentionDays: 7, ReserveFreeGB: 10})
	if result.DeletedFiles != 1 || result.FreedBytes != int64(len("expired")) || len(result.Errors) != 0 {
		t.Fatalf("unexpected cleanup result: %+v", result)
	}
	if _, err := os.Stat(expired); !os.IsNotExist(err) {
		t.Fatalf("expired file still exists: %v", err)
	}
	for _, path := range []string{retained, active} {
		if _, err := os.Stat(path); err != nil {
			t.Fatalf("retained file was removed: %s: %v", path, err)
		}
	}
	if lastWrite == nil || !lastWrite.Equal(now.Add(-5*time.Minute)) {
		t.Fatalf("last write = %v", lastWrite)
	}
}

func TestCleanerUsesOldestFirstUnderDiskPressure(t *testing.T) {
	root := t.TempDir()
	now := time.Date(2026, 7, 23, 10, 0, 0, 0, time.UTC)
	oldest := writeTestFile(t, root, "oldest.bin", "123", now.Add(-3*time.Hour))
	newer := writeTestFile(t, root, "newer.bin", "456", now.Add(-2*time.Hour))
	active := writeTestFile(t, root, "active.bin", "789", now.Add(-5*time.Minute))
	reserve := int64(1 << 30)
	cleaner := NewCleaner()
	cleaner.now = func() time.Time { return now }
	volumeReads := 0
	cleaner.volumeInfo = func(string) (VolumeInfo, error) {
		volumeReads++
		free := reserve - 4
		if volumeReads == 2 {
			free = reserve - 1
		} else if volumeReads >= 3 {
			free = reserve + 2
		}
		return VolumeInfo{TotalBytes: 10 << 30, FreeBytes: free}, nil
	}

	result, _, volume := cleaner.Run(Config{LocalPath: root, RetentionDays: 90, ReserveFreeGB: 1})
	if result.DeletedFiles != 2 || volume.FreeBytes < reserve {
		t.Fatalf("pressure cleanup did not recover reserve: result=%+v volume=%+v", result, volume)
	}
	for _, path := range []string{oldest, newer} {
		if _, err := os.Stat(path); !os.IsNotExist(err) {
			t.Fatalf("eligible file still exists: %s", path)
		}
	}
	if _, err := os.Stat(active); err != nil {
		t.Fatalf("active file was removed: %v", err)
	}
}

func TestCleanerSkipsReparseDirectory(t *testing.T) {
	root := t.TempDir()
	outside := t.TempDir()
	now := time.Now().UTC()
	outsideFile := writeTestFile(t, outside, "recording.mp4", "keep", now.Add(-10*24*time.Hour))
	link := filepath.Join(root, "linked")
	if err := os.Symlink(outside, link); err != nil {
		t.Skipf("symbolic links are unavailable: %v", err)
	}
	cleaner := NewCleaner()
	cleaner.now = func() time.Time { return now }
	cleaner.volumeInfo = func(string) (VolumeInfo, error) { return VolumeInfo{TotalBytes: 10 << 30, FreeBytes: 2 << 30}, nil }
	result, _, _ := cleaner.Run(Config{LocalPath: root, RetentionDays: 7, ReserveFreeGB: 1})
	if result.DeletedFiles != 0 {
		t.Fatalf("cleanup traversed a reparse point: %+v", result)
	}
	if _, err := os.Stat(outsideFile); err != nil {
		t.Fatalf("outside file was removed: %v", err)
	}
}

func TestManagerFirstEnablePersistsIdentityAndReturnsPasswordOnce(t *testing.T) {
	store, err := Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	provisioner := &fakeProvisioner{sid: "S-1-5-21-test", status: ProvisionStatus{ShareReady: true, AccountReady: true, FirewallReady: true}}
	manager, err := NewManager(store, "node-1", provisioner)
	if err != nil {
		t.Fatal(err)
	}
	root := t.TempDir()
	result, err := manager.Apply(context.Background(), Config{Enabled: true, LocalPath: root, RetentionDays: 7, ReserveFreeGB: 1})
	if err != nil {
		t.Fatal(err)
	}
	if result.Credentials == nil || len(result.Credentials.Password) != 16 || !hasPasswordCategories(result.Credentials.Password) {
		t.Fatalf("missing or weak one-time credentials: %+v", result.Credentials)
	}
	if provisioner.password != result.Credentials.Password || result.Config.UserSID != provisioner.sid {
		t.Fatalf("identity was not provisioned: result=%+v fake=%+v", result.Config, provisioner)
	}
	second, err := manager.Apply(context.Background(), result.Config)
	if err != nil {
		t.Fatal(err)
	}
	if second.Credentials != nil || provisioner.password != "" {
		t.Fatal("an existing account unexpectedly received new credentials")
	}
	saved, err := store.LoadConfig()
	if err != nil || saved.UserSID != provisioner.sid || saved.Username == "" {
		t.Fatalf("identity was not persisted: %+v err=%v", saved, err)
	}
}

func TestManagerRollsBackShareWhenPathChangeFails(t *testing.T) {
	store, err := Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	provisioner := &fakeProvisioner{sid: "S-1-test", status: ProvisionStatus{ShareReady: true, AccountReady: true, FirewallReady: true}}
	manager, _ := NewManager(store, "node-2", provisioner)
	first, err := manager.Apply(context.Background(), Config{Enabled: true, LocalPath: t.TempDir(), RetentionDays: 7, ReserveFreeGB: 1})
	if err != nil {
		t.Fatal(err)
	}
	provisioner.failPath = filepath.Clean(t.TempDir())
	_, err = manager.Apply(context.Background(), Config{Enabled: true, LocalPath: provisioner.failPath, RetentionDays: 7, ReserveFreeGB: 1})
	if err == nil || len(provisioner.paths) < 3 || provisioner.paths[len(provisioner.paths)-1] != first.Config.LocalPath {
		t.Fatalf("old share was not restored: paths=%v err=%v", provisioner.paths, err)
	}
	if manager.Config().LocalPath != first.Config.LocalPath {
		t.Fatal("failed configuration replaced persisted settings")
	}
}

func TestManagerReconcilesShareButDoesNotRecreateMissingAccount(t *testing.T) {
	store, err := Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	root := t.TempDir()
	cfg := Config{Enabled: true, LocalPath: root, RetentionDays: 7, ReserveFreeGB: 1, ShareName: ShareName, Username: "FscmNas_test", UserSID: "S-1-test"}
	if err := store.SaveConfig(cfg); err != nil {
		t.Fatal(err)
	}
	provisioner := &fakeProvisioner{sid: cfg.UserSID, status: ProvisionStatus{AccountReady: true}}
	manager, _ := NewManager(store, "node", provisioner)
	manager.reconcile(context.Background())
	if len(provisioner.paths) != 1 || !manager.provision.ShareReady || !manager.provision.FirewallReady {
		t.Fatalf("share was not reconciled: paths=%v status=%+v", provisioner.paths, manager.provision)
	}

	provisioner.paths = nil
	provisioner.status = ProvisionStatus{Error: "managed NAS account is missing"}
	manager.inspectedAt = time.Time{}
	manager.reconcile(context.Background())
	if len(provisioner.paths) != 0 || manager.provision.Error == "" {
		t.Fatalf("missing account was silently recreated: paths=%v status=%+v", provisioner.paths, manager.provision)
	}
}

func TestManagerStatusDoesNotReportReadyWhenCleanupFailed(t *testing.T) {
	store, err := Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	cfg := Config{Enabled: true, LocalPath: t.TempDir(), RetentionDays: 7, ReserveFreeGB: 1, ShareName: ShareName, Username: "FscmNas_test", UserSID: "S-1-test"}
	if err := store.SaveConfig(cfg); err != nil {
		t.Fatal(err)
	}
	manager, _ := NewManager(store, "node", &fakeProvisioner{})
	manager.provision = ProvisionStatus{ShareReady: true, AccountReady: true, FirewallReady: true}
	manager.inspectedAt = time.Now()
	manager.lastError = "recording.mp4 is still open"
	status := manager.Status(context.Background())
	if status.Ready || status.State == "ready" || !strings.Contains(status.Error, "still open") {
		t.Fatalf("cleanup error was hidden: %+v", status)
	}
}

func writeTestFile(t *testing.T, root, relative, content string, modified time.Time) string {
	t.Helper()
	path := filepath.Join(root, filepath.FromSlash(relative))
	if err := os.MkdirAll(filepath.Dir(path), 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(content), 0o640); err != nil {
		t.Fatal(err)
	}
	if err := os.Chtimes(path, modified, modified); err != nil {
		t.Fatal(err)
	}
	return path
}

func hasPasswordCategories(value string) bool {
	return strings.IndexFunc(value, func(r rune) bool { return r >= 'A' && r <= 'Z' }) >= 0 &&
		strings.IndexFunc(value, func(r rune) bool { return r >= 'a' && r <= 'z' }) >= 0 &&
		strings.IndexFunc(value, func(r rune) bool { return r >= '0' && r <= '9' }) >= 0
}

type fakeProvisioner struct {
	sid      string
	status   ProvisionStatus
	password string
	failPath string
	paths    []string
}

func (f *fakeProvisioner) Configure(_ context.Context, cfg Config, password string) (string, error) {
	f.paths = append(f.paths, cfg.LocalPath)
	f.password = password
	if cfg.LocalPath == f.failPath {
		return "", errors.New("provision failed")
	}
	return f.sid, nil
}
func (f *fakeProvisioner) Disable(context.Context, Config) error           { return nil }
func (f *fakeProvisioner) Inspect(context.Context, Config) ProvisionStatus { return f.status }
func (f *fakeProvisioner) Remove(context.Context, Config) error            { return nil }
