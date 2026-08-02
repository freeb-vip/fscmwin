//go:build !windows

package main

import "context"

func runPlatformService(configPath string, run func(context.Context, string) error) error {
	return run(context.Background(), configPath)
}

func controlPlatformService(string, string) error { return nil }
