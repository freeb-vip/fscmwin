//go:build !windows

package storage

import (
	"fmt"
	"os"
	"syscall"
)

func platformVolumeInfo(path string) (VolumeInfo, error) {
	var stat syscall.Statfs_t
	if err := syscall.Statfs(path, &stat); err != nil {
		return VolumeInfo{}, err
	}
	return VolumeInfo{TotalBytes: int64(stat.Blocks) * int64(stat.Bsize), FreeBytes: int64(stat.Bavail) * int64(stat.Bsize)}, nil
}

func validateFixedLocalPath(string) error { return nil }

func isReparsePoint(path string) (bool, error) {
	info, err := os.Lstat(path)
	if err != nil {
		return false, fmt.Errorf("inspect path: %w", err)
	}
	return info.Mode()&os.ModeSymlink != 0, nil
}
