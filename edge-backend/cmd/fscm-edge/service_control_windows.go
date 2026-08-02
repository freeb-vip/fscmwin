//go:build windows

package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"syscall"
	"time"

	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/eventlog"
	"golang.org/x/sys/windows/svc/mgr"
)

func controlPlatformService(action, configPath string) error {
	manager, err := mgr.Connect()
	if err != nil {
		return err
	}
	defer manager.Disconnect()
	switch strings.ToLower(strings.TrimSpace(action)) {
	case "install":
		return installWindowsService(manager, configPath)
	case "uninstall":
		return uninstallWindowsService(manager)
	default:
		return fmt.Errorf("unsupported service-control action %q", action)
	}
}

func installWindowsService(manager *mgr.Mgr, configPath string) error {
	executable, err := os.Executable()
	if err != nil {
		return err
	}
	executable, err = filepath.Abs(executable)
	if err != nil {
		return err
	}
	configPath, err = filepath.Abs(configPath)
	if err != nil {
		return err
	}
	serviceConfig := mgr.Config{
		StartType:        mgr.StartAutomatic,
		ErrorControl:     mgr.ErrorNormal,
		DisplayName:      "FSCM Edge Backend",
		Description:      "Provides the FSCM edge proxy, SMB NAS storage, and retention cleanup.",
		DelayedAutoStart: true,
		BinaryPathName: syscall.EscapeArg(executable) +
			" --mode=edge --config=" + syscall.EscapeArg(configPath),
	}
	service, err := manager.OpenService(windowsServiceName)
	if err == nil {
		defer service.Close()
		if err := stopWindowsService(service); err != nil {
			return err
		}
		if err := service.UpdateConfig(serviceConfig); err != nil {
			return err
		}
	} else {
		service, err = manager.CreateService(windowsServiceName, executable, serviceConfig, "--mode=edge", "--config="+configPath)
		if err != nil {
			return err
		}
		defer service.Close()
	}
	if err := service.SetRecoveryActions([]mgr.RecoveryAction{
		{Type: mgr.ServiceRestart, Delay: 5 * time.Second},
		{Type: mgr.ServiceRestart, Delay: 15 * time.Second},
		{Type: mgr.ServiceRestart, Delay: time.Minute},
	}, 86400); err != nil {
		return err
	}
	if err := service.SetRecoveryActionsOnNonCrashFailures(true); err != nil {
		return err
	}
	if err := eventlog.InstallAsEventCreate(windowsServiceName, eventlog.Error|eventlog.Warning|eventlog.Info); err != nil && !os.IsExist(err) {
		return err
	}
	return service.Start()
}

func uninstallWindowsService(manager *mgr.Mgr) error {
	service, err := manager.OpenService(windowsServiceName)
	if err != nil {
		return nil
	}
	defer service.Close()
	if err := stopWindowsService(service); err != nil {
		return err
	}
	deleteErr := service.Delete()
	if eventErr := eventlog.Remove(windowsServiceName); eventErr != nil && !os.IsNotExist(eventErr) {
		return eventErr
	}
	return deleteErr
}

func stopWindowsService(service *mgr.Service) error {
	status, err := service.Query()
	if err != nil || status.State == svc.Stopped {
		return err
	}
	if _, err := service.Control(svc.Stop); err != nil {
		return err
	}
	deadline := time.Now().Add(20 * time.Second)
	for time.Now().Before(deadline) {
		status, err = service.Query()
		if err != nil || status.State == svc.Stopped {
			return err
		}
		time.Sleep(250 * time.Millisecond)
	}
	return fmt.Errorf("timed out stopping %s", windowsServiceName)
}
