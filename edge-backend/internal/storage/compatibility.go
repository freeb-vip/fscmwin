package storage

import (
	"context"
	"fmt"
)

func normalizeCompatibilityMode(mode string) string {
	if mode == "" {
		return SMBCompatibilityDefault
	}
	return mode
}

func validateCompatibilityMode(mode string) error {
	if mode != SMBCompatibilityDefault && mode != SMBCompatibilityXiaomi {
		return fmt.Errorf("smb_compatibility_mode must be %q or %q", SMBCompatibilityDefault, SMBCompatibilityXiaomi)
	}
	return nil
}

func (m *Manager) inspectSMBPolicy(ctx context.Context) (*SMBPolicy, error) {
	provisioner, ok := m.provisioner.(SMBPolicyProvisioner)
	if !ok {
		return nil, nil
	}
	policy, err := provisioner.InspectSMBPolicy(ctx)
	if err != nil {
		return nil, fmt.Errorf("inspect Windows SMB policy: %w", err)
	}
	return &policy, nil
}

func (m *Manager) applyCompatibilityPolicy(ctx context.Context, target bool, snapshot *SMBPolicy) (SigningPolicyJournal, error) {
	provisioner, ok := m.provisioner.(SMBPolicyProvisioner)
	if !ok {
		if target {
			return m.signingPolicy, fmt.Errorf("xiaomi_smb2 compatibility is only supported on Windows")
		}
		return SigningPolicyJournal{State: "idle"}, nil
	}
	policy := snapshot
	if policy == nil {
		var err error
		policy, err = m.inspectSMBPolicy(ctx)
		if err != nil {
			return m.signingPolicy, err
		}
	}
	if target {
		if policy.SMB1Enabled {
			return m.signingPolicy, fmt.Errorf("xiaomi_smb2 requires SMB1 to remain disabled")
		}
		if !policy.SMB2Enabled {
			return m.signingPolicy, fmt.Errorf("xiaomi_smb2 requires SMB2/3 to be enabled")
		}
		journal := m.signingPolicy
		if policy.SigningRequired {
			alreadyActive := m.config.Enabled && m.config.SMBCompatibilityMode == SMBCompatibilityXiaomi
			if !journal.Managed && !alreadyActive {
				journal = SigningPolicyJournal{Managed: true, PreviousRequired: true, State: "applying"}
			} else if journal.Managed {
				journal.State = "applying"
			}
			if journal.Managed {
				if err := m.store.SaveSigningPolicy(journal); err != nil {
					return m.signingPolicy, err
				}
				m.signingPolicy = journal
			}
			if err := provisioner.SetSMBSigning(ctx, false); err != nil {
				return m.signingPolicy, fmt.Errorf("disable mandatory SMB signing: %w", err)
			}
		}
		if journal.Managed {
			journal.State = "idle"
		}
		return journal, nil
	}
	if !m.signingPolicy.Managed {
		return SigningPolicyJournal{State: "idle"}, nil
	}
	restoring := m.signingPolicy
	restoring.State = "restoring"
	if err := m.store.SaveSigningPolicy(restoring); err != nil {
		return m.signingPolicy, err
	}
	m.signingPolicy = restoring
	if policy.SigningRequired != restoring.PreviousRequired {
		if err := provisioner.SetSMBSigning(ctx, restoring.PreviousRequired); err != nil {
			return m.signingPolicy, fmt.Errorf("restore Windows SMB signing policy: %w", err)
		}
	}
	return SigningPolicyJournal{State: "idle"}, nil
}

func (m *Manager) rollbackCompatibilityPolicy(ctx context.Context, journal SigningPolicyJournal, snapshot *SMBPolicy) {
	if provisioner, ok := m.provisioner.(SMBPolicyProvisioner); ok && snapshot != nil {
		_ = provisioner.SetSMBSigning(ctx, snapshot.SigningRequired)
	}
	_ = m.store.SaveSigningPolicy(journal)
	m.signingPolicy = journal
}

func (m *Manager) recoverSigningTransition(ctx context.Context) {
	m.mu.Lock()
	defer m.mu.Unlock()
	provisioner, ok := m.provisioner.(SMBPolicyProvisioner)
	if !ok {
		return
	}
	policy, err := provisioner.InspectSMBPolicy(ctx)
	if err != nil {
		m.policyWarning = err.Error()
		return
	}
	active := m.config.Enabled && m.config.SMBCompatibilityMode == SMBCompatibilityXiaomi
	if active {
		if policy.SMB1Enabled || !policy.SMB2Enabled {
			m.policyWarning = "xiaomi_smb2 requires SMB1 disabled and SMB2/3 enabled"
			return
		}
		if policy.SigningRequired {
			if err := provisioner.SetSMBSigning(ctx, false); err != nil {
				m.policyWarning = fmt.Sprintf("restore xiaomi_smb2 compatibility: %v", err)
				return
			}
		}
		if m.signingPolicy.Managed {
			m.signingPolicy.State = "idle"
			_ = m.store.SaveSigningPolicy(m.signingPolicy)
		}
		return
	}
	if m.signingPolicy.Managed && policy.SigningRequired != m.signingPolicy.PreviousRequired {
		if err := provisioner.SetSMBSigning(ctx, m.signingPolicy.PreviousRequired); err != nil {
			m.policyWarning = fmt.Sprintf("restore Windows SMB signing policy: %v", err)
			return
		}
	}
	if m.signingPolicy.Managed || m.signingPolicy.State != "idle" {
		m.signingPolicy = SigningPolicyJournal{State: "idle"}
		_ = m.store.SaveSigningPolicy(m.signingPolicy)
	}
}

func (m *Manager) reconcileCompatibilityLocked(ctx context.Context) {
	m.policyWarning = ""
	if !m.config.Enabled || m.config.SMBCompatibilityMode != SMBCompatibilityXiaomi {
		return
	}
	provisioner, ok := m.provisioner.(SMBPolicyProvisioner)
	if !ok {
		m.policyWarning = "xiaomi_smb2 compatibility is only supported on Windows"
		return
	}
	policy, err := provisioner.InspectSMBPolicy(ctx)
	if err != nil {
		m.policyWarning = err.Error()
		return
	}
	if policy.SMB1Enabled || !policy.SMB2Enabled {
		m.policyWarning = "xiaomi_smb2 requires SMB1 disabled and SMB2/3 enabled"
		return
	}
	if policy.SigningRequired {
		if err := provisioner.SetSMBSigning(ctx, false); err != nil {
			m.policyWarning = fmt.Sprintf("reapply xiaomi_smb2 compatibility: %v", err)
		} else {
			m.policyWarning = "mandatory SMB signing was changed externally and has been disabled again"
		}
	}
}

func (m *Manager) fillSMBPolicyStatus(ctx context.Context, status *Status) {
	policy, err := m.inspectSMBPolicy(ctx)
	if err != nil {
		if m.config.Enabled && status.Error == "" {
			status.Error = err.Error()
		}
		return
	}
	if policy == nil {
		return
	}
	status.SMB1Enabled = policy.SMB1Enabled
	status.SMB2Enabled = policy.SMB2Enabled
	status.SMBSigningRequired = policy.SigningRequired
	status.SigningOverrideManaged = m.signingPolicy.Managed
	status.CompatibilityReady = m.config.Enabled && m.config.SMBCompatibilityMode == SMBCompatibilityXiaomi &&
		!policy.SMB1Enabled && policy.SMB2Enabled && !policy.SigningRequired
}

func (m *Manager) restoreSigningOverrideLocked(ctx context.Context) error {
	if !m.signingPolicy.Managed {
		return nil
	}
	provisioner, ok := m.provisioner.(SMBPolicyProvisioner)
	if !ok {
		return fmt.Errorf("cannot restore Windows SMB signing policy on this platform")
	}
	restoring := m.signingPolicy
	restoring.State = "restoring"
	if err := m.store.SaveSigningPolicy(restoring); err != nil {
		return err
	}
	if err := provisioner.SetSMBSigning(ctx, restoring.PreviousRequired); err != nil {
		return err
	}
	m.signingPolicy = SigningPolicyJournal{State: "idle"}
	return m.store.SaveSigningPolicy(m.signingPolicy)
}
