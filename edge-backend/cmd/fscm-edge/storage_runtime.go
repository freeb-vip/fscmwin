package main

import (
	"context"
	"strings"

	"fscm-edge/internal/storage"
)

// storageRuntime keeps NAS failures isolated from the edge's core services.
type storageRuntime struct {
	manager      *storage.Manager
	startupError string
}

func unavailableStorageRuntime(err error) storageRuntime {
	return storageRuntime{startupError: strings.TrimSpace(err.Error())}
}

func (r storageRuntime) Available() bool { return r.manager != nil }

func (r storageRuntime) Status(ctx context.Context) storage.Status {
	if r.manager != nil {
		return r.manager.Status(ctx)
	}
	return storage.Status{
		State:                "degraded",
		Error:                r.startupError,
		ShareName:            storage.ShareName,
		SMBCompatibilityMode: storage.SMBCompatibilityDefault,
	}
}
