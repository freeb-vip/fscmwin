//go:build windows

package main

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/eventlog"
)

const windowsServiceName = "FscmEdge"

type edgeService struct {
	configPath string
	run        func(context.Context, string) error
}

func runPlatformService(configPath string, run func(context.Context, string) error) error {
	isService, err := svc.IsWindowsService()
	if err != nil {
		return err
	}
	if !isService {
		return run(context.Background(), configPath)
	}
	return svc.Run(windowsServiceName, &edgeService{configPath: configPath, run: run})
}

// logServiceFailure preserves the original startup error that SCM otherwise reduces to a generic exit code.
func logServiceFailure(configPath string, err error) {
	if err == nil {
		return
	}
	message := fmt.Sprintf("%s service startup failed: %v\r\n", time.Now().Format(time.RFC3339), err)
	logPath := filepath.Join(filepath.Dir(configPath), "logs", "edge.stderr.log")
	if mkdirErr := os.MkdirAll(filepath.Dir(logPath), 0755); mkdirErr == nil {
		if file, openErr := os.OpenFile(logPath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644); openErr == nil {
			_, _ = file.WriteString(message)
			_ = file.Close()
		}
	}
	if eventLog, openErr := eventlog.Open(windowsServiceName); openErr == nil {
		_ = eventLog.Error(1, message)
		_ = eventLog.Close()
	}
}

func (s *edgeService) Execute(_ []string, requests <-chan svc.ChangeRequest, changes chan<- svc.Status) (bool, uint32) {
	changes <- svc.Status{State: svc.StartPending}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- s.run(ctx, s.configPath) }()
	changes <- svc.Status{State: svc.Running, Accepts: svc.AcceptStop | svc.AcceptShutdown}
	for {
		select {
		case request := <-requests:
			switch request.Cmd {
			case svc.Interrogate:
				changes <- request.CurrentStatus
			case svc.Stop, svc.Shutdown:
				changes <- svc.Status{State: svc.StopPending}
				cancel()
				if err := <-done; err != nil {
					logServiceFailure(s.configPath, err)
					return true, 1
				}
				return false, 0
			}
		case err := <-done:
			if err != nil {
				logServiceFailure(s.configPath, err)
				return true, 1
			}
			return false, 0
		}
	}
}
