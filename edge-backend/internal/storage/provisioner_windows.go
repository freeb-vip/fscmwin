//go:build windows

package storage

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"runtime"
	"strings"
	"unsafe"

	"golang.org/x/sys/windows"
)

const (
	managedMarker    = "FSCM Edge managed NAS"
	firewallRuleName = "FSCM Edge NAS (SMB-In)"
)

type windowsProvisioner struct{}

func NewSystemProvisioner() Provisioner { return windowsProvisioner{} }

func (windowsProvisioner) Configure(ctx context.Context, cfg Config, password string) (string, error) {
	result, err := runProvisionScript(ctx, "configure", cfg, password)
	if err != nil {
		return "", err
	}
	if result.SID == "" {
		return "", fmt.Errorf("Windows did not return the managed NAS account SID")
	}
	if err := denyInteractiveLogon(result.SID); err != nil {
		_ = runDisableScript(ctx, cfg)
		return "", fmt.Errorf("restrict NAS account logon: %w", err)
	}
	return result.SID, nil
}

func (windowsProvisioner) Disable(ctx context.Context, cfg Config) error {
	return runDisableScript(ctx, cfg)
}

func (p windowsProvisioner) Inspect(ctx context.Context, cfg Config) ProvisionStatus {
	result, err := runProvisionScript(ctx, "inspect", cfg, "")
	if err != nil {
		return ProvisionStatus{Error: err.Error()}
	}
	status := ProvisionStatus{ShareReady: result.ShareReady, AccountReady: result.AccountReady, FirewallReady: result.FirewallReady, Error: result.Error}
	policy, policyErr := p.InspectSMBPolicy(ctx)
	if policyErr != nil {
		if status.Error == "" {
			status.Error = policyErr.Error()
		}
		return status
	}
	status.SMB1Enabled, status.SMB2Enabled, status.SMBSigningRequired = policy.SMB1Enabled, policy.SMB2Enabled, policy.SigningRequired
	return status
}

func (windowsProvisioner) Remove(ctx context.Context, cfg Config) error {
	_, err := runProvisionScript(ctx, "remove", cfg, "")
	return err
}

func runDisableScript(ctx context.Context, cfg Config) error {
	_, err := runProvisionScript(ctx, "disable", cfg, "")
	return err
}

type provisionScriptResult struct {
	SID           string `json:"sid"`
	ShareReady    bool   `json:"share_ready"`
	AccountReady  bool   `json:"account_ready"`
	FirewallReady bool   `json:"firewall_ready"`
	Error         string `json:"error"`
}

func runProvisionScript(ctx context.Context, action string, cfg Config, password string) (provisionScriptResult, error) {
	command := exec.CommandContext(ctx, "powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", provisionScript)
	command.Env = append(os.Environ(),
		"FSCM_NAS_ACTION="+action,
		"FSCM_NAS_PATH="+cfg.LocalPath,
		"FSCM_NAS_SHARE="+ShareName,
		"FSCM_NAS_USERNAME="+cfg.Username,
		"FSCM_NAS_EXPECTED_SID="+cfg.UserSID,
		"FSCM_NAS_PASSWORD="+password,
	)
	var stdout, stderr bytes.Buffer
	command.Stdout, command.Stderr = &stdout, &stderr
	if err := command.Run(); err != nil {
		message := strings.TrimSpace(stderr.String())
		if message == "" {
			message = strings.TrimSpace(stdout.String())
		}
		return provisionScriptResult{}, fmt.Errorf("Windows NAS configuration failed: %s", message)
	}
	var result provisionScriptResult
	if err := json.Unmarshal(bytes.TrimSpace(stdout.Bytes()), &result); err != nil {
		return provisionScriptResult{}, fmt.Errorf("decode Windows NAS result: %w (%s)", err, strings.TrimSpace(stdout.String()))
	}
	return result, nil
}

const provisionScript = `
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$marker = 'FSCM Edge managed NAS'
$firewallName = 'FSCM Edge NAS (SMB-In)'
$action = $env:FSCM_NAS_ACTION
$shareName = $env:FSCM_NAS_SHARE
$username = $env:FSCM_NAS_USERNAME
$path = $env:FSCM_NAS_PATH

function Get-ManagedUser {
  if ([string]::IsNullOrWhiteSpace($username)) { return $null }
  return Get-LocalUser -Name $username -ErrorAction SilentlyContinue
}

function Assert-ManagedObjects {
  $share = Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue
  if ($share -and $share.Description -ne $marker) { throw 'SHARE_CONFLICT: an unmanaged SMB share already uses FscmRecordings' }
  $user = Get-ManagedUser
  if ($user -and $user.Description -ne $marker) { throw 'ACCOUNT_CONFLICT: an unmanaged local account already uses the generated NAS username' }
  $rules = @(Get-NetFirewallRule -DisplayName $firewallName -ErrorAction SilentlyContinue)
  foreach ($rule in $rules) {
    if ($rule.Description -ne $marker) { throw 'FIREWALL_CONFLICT: an unmanaged firewall rule already uses the FSCM Edge NAS name' }
  }
}

function Remove-ManagedShareAndFirewall {
  $share = Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue
  if ($share -and $share.Description -eq $marker) { Remove-SmbShare -Name $shareName -Force -Confirm:$false }
  $rules = Get-NetFirewallRule -DisplayName $firewallName -ErrorAction SilentlyContinue
  foreach ($rule in @($rules)) {
    if ($rule.Description -eq $marker) { Remove-NetFirewallRule -Name $rule.Name }
  }
}

if ($action -eq 'inspect') {
  $share = Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue
  $user = Get-ManagedUser
  $rules = @(Get-NetFirewallRule -DisplayName $firewallName -ErrorAction SilentlyContinue | Where-Object { $_.Description -eq $marker -and $_.Enabled -eq 'True' })
  $sidMatches = $user -and ([string]::IsNullOrWhiteSpace($env:FSCM_NAS_EXPECTED_SID) -or $user.SID.Value -eq $env:FSCM_NAS_EXPECTED_SID)
  $accountReady = [bool]($user -and $user.Description -eq $marker -and $user.Enabled -and $sidMatches)
  $shareReady = [bool]($share -and $share.Description -eq $marker -and $share.Path -eq $path)
  $firewallReady = $false
  foreach ($rule in $rules) {
    $portFilter = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $rule
    $addressFilter = Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $rule
    $profileText = [string]$rule.Profile
    $protocolText = [string]$portFilter.Protocol
    if (($protocolText -eq 'TCP' -or $protocolText -eq '6') -and @($portFilter.LocalPort) -contains '445' -and
        @($addressFilter.RemoteAddress) -contains 'LocalSubnet' -and
        $profileText.Contains('Domain') -and $profileText.Contains('Private') -and -not $profileText.Contains('Public')) {
      $firewallReady = $true
    }
  }
  $errors = @()
  if (-not $accountReady) { $errors += 'managed NAS account is missing or invalid' }
  if (-not $shareReady) { $errors += 'SMB share is missing or points to another path' }
  if (-not $firewallReady) { $errors += 'private LAN firewall rule is missing' }
  [ordered]@{ sid = $(if ($user) { $user.SID.Value } else { '' }); share_ready = $shareReady; account_ready = $accountReady; firewall_ready = $firewallReady; error = ($errors -join '; ') } | ConvertTo-Json -Compress
  exit 0
}

Assert-ManagedObjects
if ($action -eq 'disable' -or $action -eq 'remove') {
  Remove-ManagedShareAndFirewall
  if ($action -eq 'remove') {
    $user = Get-ManagedUser
    if ($user -and $user.Description -eq $marker) { Remove-LocalUser -Name $username }
  }
  [ordered]@{ sid = ''; share_ready = $false; account_ready = $false; firewall_ready = $false; error = '' } | ConvertTo-Json -Compress
  exit 0
}

if ($action -ne 'configure') { throw "unsupported NAS action: $action" }
if ([string]::IsNullOrWhiteSpace($username)) { throw 'managed NAS username is required' }
$user = Get-ManagedUser
if (-not $user -and [string]::IsNullOrWhiteSpace($env:FSCM_NAS_PASSWORD)) { throw 'ACCOUNT_MISSING: reset NAS credentials to recreate the managed account' }
if (-not [string]::IsNullOrWhiteSpace($env:FSCM_NAS_PASSWORD)) {
  $securePassword = ConvertTo-SecureString $env:FSCM_NAS_PASSWORD -AsPlainText -Force
  if ($user) {
    Set-LocalUser -Name $username -Password $securePassword -PasswordNeverExpires $true -UserMayChangePassword $false -Description $marker
  } else {
    $user = New-LocalUser -Name $username -Password $securePassword -AccountNeverExpires -PasswordNeverExpires -UserMayNotChangePassword -Description $marker
  }
}
$user = Get-ManagedUser
if (-not $user) { throw 'managed NAS account is unavailable' }

$inheritance = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
$propagation = [System.Security.AccessControl.PropagationFlags]::None
$allow = [System.Security.AccessControl.AccessControlType]::Allow
$userIdentity = New-Object System.Security.Principal.SecurityIdentifier($user.SID.Value)
$systemIdentity = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
$administratorsIdentity = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
$acl = Get-Acl -LiteralPath $path
$acl.SetAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($userIdentity, 'Modify', $inheritance, $propagation, $allow)))
$acl.SetAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($systemIdentity, 'FullControl', $inheritance, $propagation, $allow)))
$acl.SetAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($administratorsIdentity, 'FullControl', $inheritance, $propagation, $allow)))
Set-Acl -LiteralPath $path -AclObject $acl

Remove-ManagedShareAndFirewall
$shareSecurityDescriptor = "O:BAG:BAD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1301bf;;;$($user.SID.Value))"
New-SmbShare -Name $shareName -Path $path -Description $marker -SecurityDescriptor $shareSecurityDescriptor | Out-Null
New-NetFirewallRule -DisplayName $firewallName -Description $marker -Direction Inbound -Action Allow -Protocol TCP -LocalPort 445 -RemoteAddress LocalSubnet -Profile Domain,Private | Out-Null
[ordered]@{ sid = $user.SID.Value; share_ready = $true; account_ready = $true; firewall_ready = $true; error = '' } | ConvertTo-Json -Compress
`

type smbPolicyScriptResult struct {
	SMB1Enabled     bool `json:"smb1_enabled"`
	SMB2Enabled     bool `json:"smb2_enabled"`
	SigningRequired bool `json:"smb_signing_required"`
}

func (windowsProvisioner) InspectSMBPolicy(ctx context.Context) (SMBPolicy, error) {
	result, err := runSMBPolicyScript(ctx, "inspect", false)
	if err != nil {
		return SMBPolicy{}, err
	}
	return SMBPolicy{SMB1Enabled: result.SMB1Enabled, SMB2Enabled: result.SMB2Enabled, SigningRequired: result.SigningRequired}, nil
}

func (windowsProvisioner) SetSMBSigning(ctx context.Context, required bool) error {
	_, err := runSMBPolicyScript(ctx, "set_signing", required)
	return err
}

func runSMBPolicyScript(ctx context.Context, action string, required bool) (smbPolicyScriptResult, error) {
	command := exec.CommandContext(ctx, "powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", smbPolicyScript)
	command.Env = append(os.Environ(),
		"FSCM_SMB_POLICY_ACTION="+action,
		fmt.Sprintf("FSCM_SMB_SIGNING_REQUIRED=%t", required),
	)
	var stdout, stderr bytes.Buffer
	command.Stdout, command.Stderr = &stdout, &stderr
	if err := command.Run(); err != nil {
		message := strings.TrimSpace(stderr.String())
		if message == "" {
			message = strings.TrimSpace(stdout.String())
		}
		return smbPolicyScriptResult{}, fmt.Errorf("Windows SMB policy configuration failed: %s", message)
	}
	var result smbPolicyScriptResult
	if err := json.Unmarshal(bytes.TrimSpace(stdout.Bytes()), &result); err != nil {
		return smbPolicyScriptResult{}, fmt.Errorf("decode Windows SMB policy result: %w (%s)", err, strings.TrimSpace(stdout.String()))
	}
	return result, nil
}

const smbPolicyScript = `
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$action = $env:FSCM_SMB_POLICY_ACTION
if ($action -eq 'set_signing') {
  $required = $env:FSCM_SMB_SIGNING_REQUIRED -eq 'true'
  Set-SmbServerConfiguration -RequireSecuritySignature $required -Force -Confirm:$false | Out-Null
} elseif ($action -ne 'inspect') {
  throw "unsupported SMB policy action: $action"
}
$config = Get-SmbServerConfiguration
[ordered]@{
  smb1_enabled = [bool]$config.EnableSMB1Protocol
  smb2_enabled = [bool]$config.EnableSMB2Protocol
  smb_signing_required = [bool]$config.RequireSecuritySignature
} | ConvertTo-Json -Compress
`

type lsaUnicodeString struct {
	Length        uint16
	MaximumLength uint16
	Buffer        *uint16
}

type lsaObjectAttributes struct {
	Length                   uint32
	RootDirectory            windows.Handle
	ObjectName               *lsaUnicodeString
	Attributes               uint32
	SecurityDescriptor       uintptr
	SecurityQualityOfService uintptr
}

var (
	advapi32              = windows.NewLazySystemDLL("advapi32.dll")
	procLsaOpenPolicy     = advapi32.NewProc("LsaOpenPolicy")
	procLsaAddRights      = advapi32.NewProc("LsaAddAccountRights")
	procLsaClose          = advapi32.NewProc("LsaClose")
	procLsaStatusToWinErr = advapi32.NewProc("LsaNtStatusToWinError")
)

var lsaAddAccountRights = func(policy windows.Handle, sid *windows.SID, right *lsaUnicodeString, count uint32) uintptr {
	status, _, _ := procLsaAddRights.Call(
		uintptr(policy),
		uintptr(unsafe.Pointer(sid)),
		uintptr(unsafe.Pointer(right)),
		uintptr(count),
	)
	runtime.KeepAlive(sid)
	runtime.KeepAlive(right)
	return status
}

func denyInteractiveLogon(sidText string) error {
	sid, err := windows.StringToSid(sidText)
	if err != nil {
		return err
	}
	const policyLookupNames = 0x00000800
	const policyCreateAccount = 0x00000010
	attributes := lsaObjectAttributes{Length: uint32(unsafe.Sizeof(lsaObjectAttributes{}))}
	var policy windows.Handle
	status, _, _ := procLsaOpenPolicy.Call(0, uintptr(unsafe.Pointer(&attributes)), policyLookupNames|policyCreateAccount, uintptr(unsafe.Pointer(&policy)))
	if status != 0 {
		return lsaError(status)
	}
	defer procLsaClose.Call(uintptr(policy))
	for _, name := range []string{"SeDenyInteractiveLogonRight", "SeDenyRemoteInteractiveLogonRight"} {
		if err := addLSAAccountRight(policy, sid, name); err != nil {
			return err
		}
	}
	return nil
}

func addLSAAccountRight(policy windows.Handle, sid *windows.SID, name string) error {
	value, err := newLSAString(name)
	if err != nil {
		return err
	}
	status := lsaAddAccountRights(policy, sid, &value, 1)
	runtime.KeepAlive(value)
	if status != 0 {
		return lsaError(status)
	}
	return nil
}

func newLSAString(value string) (lsaUnicodeString, error) {
	buffer, err := windows.UTF16FromString(value)
	if err != nil {
		return lsaUnicodeString{}, err
	}
	length := uint16((len(buffer) - 1) * 2)
	return lsaUnicodeString{Length: length, MaximumLength: uint16(len(buffer) * 2), Buffer: &buffer[0]}, nil
}

func lsaError(status uintptr) error {
	code, _, _ := procLsaStatusToWinErr.Call(status)
	return windows.Errno(code)
}
