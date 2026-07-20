package main

import (
	"errors"
	"testing"
	"time"
)

func TestTerminalCapabilitiesAreNormalized(t *testing.T) {
	values := normalizedCapabilities([]string{" Find-Device ", "find-device", "MOBILE-APP", ""})
	if len(values) != 2 || values[0] != "find-device" || values[1] != "mobile-app" {
		t.Fatalf("unexpected capabilities: %#v", values)
	}
}

func TestTerminalCommandRejectsUnknownOfflineAndUnsupportedTerminals(t *testing.T) {
	store := newTerminalStore()
	if _, err := store.find("missing"); !errors.Is(err, errTerminalNotFound) {
		t.Fatalf("expected terminal not found, got %v", err)
	}

	store.items["offline"] = terminal{TerminalID: "offline", Status: "offline", Capabilities: []string{"find-device"}}
	if _, err := store.find("offline"); !errors.Is(err, errTerminalOffline) {
		t.Fatalf("expected terminal offline, got %v", err)
	}

	store.items["unsupported"] = terminal{TerminalID: "unsupported", Status: "online"}
	store.clients["unsupported"] = &terminalConnection{}
	if _, err := store.find("unsupported"); !errors.Is(err, errFindDeviceDisabled) {
		t.Fatalf("expected find-device disabled, got %v", err)
	}
}

func TestTerminalCommandResultsUpdateFindingStateAndCompleteWaiter(t *testing.T) {
	store := newTerminalStore()
	store.items["android-1"] = terminal{TerminalID: "android-1", Status: "online"}
	waiter := make(chan terminalCommandResult, 1)
	store.pending["command-1"] = waiter

	store.completeCommand("android-1", terminalCommandResult{CommandID: "command-1", Status: "playing"})
	select {
	case result := <-waiter:
		if result.Status != "playing" {
			t.Fatalf("unexpected result: %#v", result)
		}
	case <-time.After(time.Second):
		t.Fatal("command waiter was not completed")
	}
	if value := store.items["android-1"]; !value.Finding || value.CommandID != "command-1" {
		t.Fatalf("terminal not marked as finding: %#v", value)
	}

	store.completeCommand("android-1", terminalCommandResult{CommandID: "stop-command", Status: "stopped"})
	if value := store.items["android-1"]; value.Finding || value.CommandID != "" {
		t.Fatalf("terminal finding state was not cleared: %#v", value)
	}
}
