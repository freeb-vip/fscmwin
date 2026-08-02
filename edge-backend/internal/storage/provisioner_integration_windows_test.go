//go:build windows && integration

package storage

import (
	"context"
	"os"
	"os/exec"
	"path/filepath"
	"testing"

	"golang.org/x/sys/windows"
)

func TestElevatedWindowsSMBProvisioningAndWrite(t *testing.T) {
	var token windows.Token
	if err := windows.OpenProcessToken(windows.CurrentProcess(), windows.TOKEN_QUERY, &token); err != nil || !token.IsElevated() {
		t.Skip("elevated Windows token required")
	}
	defer token.Close()
	if command := exec.Command("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "if (Get-SmbShare -Name 'FscmRecordings' -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"); command.Run() == nil {
		t.Skip("FscmRecordings already exists")
	}
	store, err := Open(filepath.Join(t.TempDir(), "edge.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()
	manager, err := NewManager(store, "integration-test-node", NewSystemProvisioner())
	if err != nil {
		t.Fatal(err)
	}
	policyProvisioner := manager.provisioner.(SMBPolicyProvisioner)
	originalPolicy, err := policyProvisioner.InspectSMBPolicy(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		if err := policyProvisioner.SetSMBSigning(context.Background(), originalPolicy.SigningRequired); err != nil {
			t.Errorf("restore original SMB signing policy: %v", err)
		}
	})
	if originalPolicy.SMB1Enabled || !originalPolicy.SMB2Enabled {
		t.Fatalf("integration host must have SMB1 disabled and SMB2/3 enabled: %+v", originalPolicy)
	}
	root := t.TempDir()
	result, err := manager.Apply(context.Background(), Config{
		Enabled: true, LocalPath: root, RetentionDays: 7, ReserveFreeGB: 1,
		SMBCompatibilityMode: SMBCompatibilityXiaomi,
	})
	if err != nil {
		t.Fatal(err)
	}
	defer func() {
		if err := manager.RemoveSystemObjects(context.Background()); err != nil {
			t.Errorf("remove managed objects: %v", err)
		}
	}()
	if result.Credentials == nil || len(result.Credentials.Password) != 16 || len(result.Credentials.SharePaths) == 0 {
		t.Fatal("first enable did not return 16-character credentials")
	}
	compatiblePolicy, err := policyProvisioner.InspectSMBPolicy(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if compatiblePolicy.SigningRequired || compatiblePolicy.SMB1Enabled || !compatiblePolicy.SMB2Enabled {
		t.Fatalf("xiaomi_smb2 policy is not ready: %+v", compatiblePolicy)
	}
	remote := result.Credentials.SharePaths[0]
	command := exec.Command("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", `$password = ConvertTo-SecureString $env:FSCM_TEST_PASSWORD -AsPlainText -Force; $credential = New-Object System.Management.Automation.PSCredential($env:FSCM_TEST_USERNAME, $password); New-SmbMapping -RemotePath $env:FSCM_TEST_REMOTE -UserName $credential.UserName -Password $env:FSCM_TEST_PASSWORD -Persistent $false | Out-Null; try { Set-Content -LiteralPath (Join-Path $env:FSCM_TEST_REMOTE 'integration-write.txt') -Value 'ok' } finally { Remove-SmbMapping -RemotePath $env:FSCM_TEST_REMOTE -Force -UpdateProfile -ErrorAction SilentlyContinue }`)
	command.Env = append(os.Environ(),
		"FSCM_TEST_USERNAME="+result.Credentials.Username,
		"FSCM_TEST_PASSWORD="+result.Credentials.Password,
		"FSCM_TEST_REMOTE="+remote,
	)
	if output, err := command.CombinedOutput(); err != nil {
		t.Fatalf("SMB write failed: %v: %s", err, output)
	}
	if _, err := os.Stat(filepath.Join(root, "integration-write.txt")); err != nil {
		t.Fatalf("SMB write did not reach local storage: %v", err)
	}
	if err := manager.RemoveSystemObjects(context.Background()); err != nil {
		t.Fatal(err)
	}
	restoredPolicy, err := policyProvisioner.InspectSMBPolicy(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if restoredPolicy.SigningRequired != originalPolicy.SigningRequired {
		t.Fatalf("SMB signing policy was not restored: original=%+v restored=%+v", originalPolicy, restoredPolicy)
	}
}
