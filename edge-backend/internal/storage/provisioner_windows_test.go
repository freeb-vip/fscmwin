//go:build windows

package storage

import (
	"context"
	"path/filepath"
	"strings"
	"testing"

	"golang.org/x/sys/windows"
)

func TestWindowsProvisionerUsesSupportedLocalUserPasswordFlags(t *testing.T) {
	setLocalUser := "Set-LocalUser -Name $username -Password $securePassword -PasswordNeverExpires $true -UserMayChangePassword $false -Description $marker"
	if !strings.Contains(provisionScript, setLocalUser) {
		t.Fatalf("provisioning script does not use the supported Set-LocalUser password flag")
	}
	if strings.Contains(provisionScript, "Set-LocalUser -Name $username -Password $securePassword -PasswordNeverExpires $true -UserMayNotChangePassword") {
		t.Fatalf("provisioning script uses unsupported Set-LocalUser -UserMayNotChangePassword flag")
	}
	if !strings.Contains(provisionScript, "New-LocalUser -Name $username -Password $securePassword -AccountNeverExpires -PasswordNeverExpires -UserMayNotChangePassword") {
		t.Fatalf("provisioning script must keep New-LocalUser -UserMayNotChangePassword")
	}
}

func TestWindowsProvisionerUsesSIDObjectsForACLAndSharePermissions(t *testing.T) {
	for _, expected := range []string{
		"$userIdentity = New-Object System.Security.Principal.SecurityIdentifier($user.SID.Value)",
		"$systemIdentity = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')",
		"$administratorsIdentity = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')",
		"FileSystemAccessRule($userIdentity, 'Modify'",
		"FileSystemAccessRule($systemIdentity, 'FullControl'",
		"FileSystemAccessRule($administratorsIdentity, 'FullControl'",
		"$shareSecurityDescriptor = \"O:BAG:BAD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1301bf;;;$($user.SID.Value))\"",
		"-SecurityDescriptor $shareSecurityDescriptor",
	} {
		if !strings.Contains(provisionScript, expected) {
			t.Fatalf("provisioning script does not use SID-based permissions: missing %q", expected)
		}
	}
	for _, forbidden := range []string{
		"FileSystemAccessRule($account",
		"FileSystemAccessRule('S-1-5-18'",
		"FileSystemAccessRule('S-1-5-32-544'",
		"-ChangeAccess",
		"-FullAccess",
	} {
		if strings.Contains(provisionScript, forbidden) {
			t.Fatalf("provisioning script still relies on account-name translation: %q", forbidden)
		}
	}
}

func TestAddLSAAccountRightPassesPolicyHandleAndSID(t *testing.T) {
	sid, err := windows.StringToSid("S-1-5-21-1-2-3-1001")
	if err != nil {
		t.Fatal(err)
	}
	policy := windows.Handle(0x1234)
	original := lsaAddAccountRights
	t.Cleanup(func() { lsaAddAccountRights = original })
	called := false
	lsaAddAccountRights = func(gotPolicy windows.Handle, gotSID *windows.SID, right *lsaUnicodeString, count uint32) uintptr {
		called = true
		if gotPolicy != policy {
			t.Fatalf("policy handle=%#x want %#x", gotPolicy, policy)
		}
		if gotSID != sid {
			t.Fatal("account SID pointer was not passed to LsaAddAccountRights")
		}
		if right == nil || windows.UTF16PtrToString(right.Buffer) != "SeDenyInteractiveLogonRight" {
			t.Fatalf("unexpected account right: %+v", right)
		}
		if count != 1 {
			t.Fatalf("right count=%d want 1", count)
		}
		return 0
	}
	if err := addLSAAccountRight(policy, sid, "SeDenyInteractiveLogonRight"); err != nil {
		t.Fatal(err)
	}
	if !called {
		t.Fatal("LsaAddAccountRights was not called")
	}
}

func TestWindowsProvisionerInspectIsReadOnlyAndReportsMissingObjects(t *testing.T) {
	cfg := Config{
		Enabled:   true,
		LocalPath: filepath.Join(t.TempDir(), "recordings"),
		ShareName: ShareName,
		Username:  "FscmNas_testmissing",
		UserSID:   "S-1-5-21-0-0-0-9999",
	}
	result, err := runProvisionScript(context.Background(), "inspect", cfg, "")
	if err != nil {
		t.Fatal(err)
	}
	if result.ShareReady || result.AccountReady || result.Error == "" {
		t.Fatalf("unexpected inspection result: %+v", result)
	}
}

func TestWindowsSMBPolicyScriptOnlyChangesSigning(t *testing.T) {
	if !strings.Contains(smbPolicyScript, "Set-SmbServerConfiguration -RequireSecuritySignature $required -Force") {
		t.Fatal("SMB policy script does not configure mandatory signing")
	}
	for _, forbidden := range []string{"EnableSMB1Protocol", "EnableSecuritySignature", "EnableInsecureGuestLogons", "LmCompatibilityLevel"} {
		if forbidden == "EnableSMB1Protocol" {
			continue
		}
		if strings.Contains(smbPolicyScript, "-"+forbidden) {
			t.Fatalf("compatibility mode changes forbidden policy %s", forbidden)
		}
	}
	if strings.Contains(smbPolicyScript, "Set-SmbServerConfiguration -EnableSMB1Protocol") {
		t.Fatal("compatibility mode must never enable SMB1")
	}
}
