//go:build !windows

package storage

import (
	"context"
	"fmt"
)

type unsupportedProvisioner struct{}

func NewSystemProvisioner() Provisioner { return unsupportedProvisioner{} }
func (unsupportedProvisioner) Configure(context.Context, Config, string) (string, error) {
	return "", fmt.Errorf("SMB provisioning is only supported on Windows")
}
func (unsupportedProvisioner) Disable(context.Context, Config) error { return nil }
func (unsupportedProvisioner) Inspect(context.Context, Config) ProvisionStatus {
	return ProvisionStatus{Error: "SMB provisioning is only supported on Windows"}
}
func (unsupportedProvisioner) Remove(context.Context, Config) error { return nil }
