//go:build windows

package main

import (
	"context"
	"strings"
	"testing"
)

func TestRunServiceSafelyRecoversStartupPanic(t *testing.T) {
	err := runServiceSafely(context.Background(), "edge.config.yaml", func(context.Context, string) error {
		panic("startup failed")
	})
	if err == nil {
		t.Fatal("expected panic to be returned as an error")
	}
	if !strings.Contains(err.Error(), "panic: startup failed") {
		t.Fatalf("missing panic detail: %v", err)
	}
	if !strings.Contains(err.Error(), "goroutine") {
		t.Fatalf("missing stack trace: %v", err)
	}
}
