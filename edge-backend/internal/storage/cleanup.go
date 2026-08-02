package storage

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

type cleanupFile struct {
	path    string
	modTime time.Time
	size    int64
}

type Cleaner struct {
	now        func() time.Time
	volumeInfo func(string) (VolumeInfo, error)
}

func NewCleaner() *Cleaner {
	return &Cleaner{now: time.Now, volumeInfo: platformVolumeInfo}
}

func (c *Cleaner) ValidatePath(path string, reserveFreeGB int) (string, VolumeInfo, error) {
	cleaned := filepath.Clean(strings.TrimSpace(path))
	if cleaned == "." || !filepath.IsAbs(cleaned) || isUNCPath(cleaned) {
		return "", VolumeInfo{}, fmt.Errorf("storage path must be an absolute local path")
	}
	if reserveFreeGB < MinimumReserveFreeGB || reserveFreeGB > MaximumReserveFreeGB {
		return "", VolumeInfo{}, fmt.Errorf("reserve_free_gb must be between %d and %d", MinimumReserveFreeGB, MaximumReserveFreeGB)
	}
	if err := validateFixedLocalPath(cleaned); err != nil {
		return "", VolumeInfo{}, err
	}
	if err := os.MkdirAll(cleaned, 0o750); err != nil {
		return "", VolumeInfo{}, fmt.Errorf("create storage path: %w", err)
	}
	if reparsePath, err := firstReparsePoint(cleaned); err != nil || reparsePath != "" {
		if err != nil {
			return "", VolumeInfo{}, err
		}
		return "", VolumeInfo{}, fmt.Errorf("storage path cannot contain a reparse point: %s", reparsePath)
	}
	probe, err := os.CreateTemp(cleaned, ".fscm-storage-probe-")
	if err != nil {
		return "", VolumeInfo{}, fmt.Errorf("storage path is not writable: %w", err)
	}
	probeName := probe.Name()
	_ = probe.Close()
	_ = os.Remove(probeName)
	volume, err := c.volumeInfo(cleaned)
	if err != nil {
		return "", VolumeInfo{}, err
	}
	if int64(reserveFreeGB)<<30 >= volume.TotalBytes {
		return "", VolumeInfo{}, fmt.Errorf("reserve_free_gb must be smaller than the volume capacity")
	}
	return cleaned, volume, nil
}

func firstReparsePoint(path string) (string, error) {
	volume := filepath.VolumeName(path)
	for current := filepath.Clean(path); ; current = filepath.Dir(current) {
		reparse, err := isReparsePoint(current)
		if err != nil {
			return "", err
		}
		if reparse {
			return current, nil
		}
		parent := filepath.Dir(current)
		if parent == current || strings.EqualFold(strings.TrimRight(current, `\/`), strings.TrimRight(volume, `\/`)) {
			return "", nil
		}
	}
}

func (c *Cleaner) Run(cfg Config) (CleanupResult, *time.Time, VolumeInfo) {
	now := c.now().UTC()
	result := CleanupResult{StartedAt: now}
	volume, err := c.volumeInfo(cfg.LocalPath)
	if err != nil {
		result.Errors = append(result.Errors, err.Error())
		result.FinishedAt = c.now().UTC()
		return result, nil, volume
	}
	files, directories, lastWrite, scanErrors := scanStorageTree(cfg.LocalPath)
	result.Errors = append(result.Errors, scanErrors...)
	sort.Slice(files, func(i, j int) bool { return files[i].modTime.Before(files[j].modTime) })
	cutoff := now.Add(-time.Duration(cfg.RetentionDays) * 24 * time.Hour)
	activeCutoff := now.Add(-activeFileGracePeriod)
	reserveBytes := int64(cfg.ReserveFreeGB) << 30
	for _, file := range files {
		expired := file.modTime.Before(cutoff)
		underPressure := volume.FreeBytes < reserveBytes
		if (!expired && !underPressure) || !file.modTime.Before(activeCutoff) {
			continue
		}
		if err := os.Remove(file.path); err != nil {
			result.Errors = append(result.Errors, fmt.Sprintf("delete %s: %v", file.path, err))
			continue
		}
		result.DeletedFiles++
		result.FreedBytes += file.size
		if underPressure {
			updated, err := c.volumeInfo(cfg.LocalPath)
			if err != nil {
				result.Errors = append(result.Errors, fmt.Sprintf("refresh volume space: %v", err))
				volume.FreeBytes += file.size
			} else {
				volume = updated
			}
		} else {
			volume.FreeBytes += file.size
		}
	}
	for index := len(directories) - 1; index >= 0; index-- {
		_ = os.Remove(directories[index])
	}
	if volume.FreeBytes < reserveBytes {
		result.Errors = append(result.Errors, "free space remains below the configured reserve after eligible files were cleaned")
	}
	result.FinishedAt = c.now().UTC()
	return result, lastWrite, volume
}

func scanStorageTree(root string) ([]cleanupFile, []string, *time.Time, []string) {
	files := make([]cleanupFile, 0)
	directories := make([]string, 0)
	errorsFound := make([]string, 0)
	var lastWrite *time.Time
	err := filepath.WalkDir(root, func(path string, entry os.DirEntry, walkErr error) error {
		if walkErr != nil {
			errorsFound = append(errorsFound, fmt.Sprintf("scan %s: %v", path, walkErr))
			if entry != nil && entry.IsDir() {
				return filepath.SkipDir
			}
			return nil
		}
		if path == root {
			return nil
		}
		reparse, err := isReparsePoint(path)
		if err != nil {
			errorsFound = append(errorsFound, fmt.Sprintf("inspect %s: %v", path, err))
			return nil
		}
		if reparse {
			if entry.IsDir() {
				return filepath.SkipDir
			}
			return nil
		}
		if entry.IsDir() {
			directories = append(directories, path)
			return nil
		}
		info, err := entry.Info()
		if err != nil {
			errorsFound = append(errorsFound, fmt.Sprintf("inspect %s: %v", path, err))
			return nil
		}
		if !info.Mode().IsRegular() {
			return nil
		}
		modified := info.ModTime().UTC()
		files = append(files, cleanupFile{path: path, modTime: modified, size: info.Size()})
		if lastWrite == nil || modified.After(*lastWrite) {
			value := modified
			lastWrite = &value
		}
		return nil
	})
	if err != nil {
		errorsFound = append(errorsFound, err.Error())
	}
	return files, directories, lastWrite, errorsFound
}

func isUNCPath(path string) bool {
	return strings.HasPrefix(path, `\\`) || strings.HasPrefix(path, `//`)
}
