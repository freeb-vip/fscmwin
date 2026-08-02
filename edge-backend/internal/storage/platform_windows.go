//go:build windows

package storage

import (
	"fmt"
	"path/filepath"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"
)

func platformVolumeInfo(path string) (VolumeInfo, error) {
	root := filepath.VolumeName(path) + `\`
	pointer, err := windows.UTF16PtrFromString(root)
	if err != nil {
		return VolumeInfo{}, err
	}
	var freeAvailable, total, totalFree uint64
	if err := windows.GetDiskFreeSpaceEx(pointer, &freeAvailable, &total, &totalFree); err != nil {
		return VolumeInfo{}, fmt.Errorf("read volume space: %w", err)
	}
	return VolumeInfo{TotalBytes: int64(total), FreeBytes: int64(freeAvailable)}, nil
}

func validateFixedLocalPath(path string) error {
	root := filepath.VolumeName(path) + `\`
	pointer, err := windows.UTF16PtrFromString(root)
	if err != nil {
		return err
	}
	const driveFixed = 3
	driveType, _, _ := syscall.NewLazyDLL("kernel32.dll").NewProc("GetDriveTypeW").Call(uintptr(unsafe.Pointer(pointer)))
	if driveType != driveFixed {
		return fmt.Errorf("storage path must be on a fixed local drive")
	}
	return nil
}

func isReparsePoint(path string) (bool, error) {
	pointer, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return false, err
	}
	attributes, err := windows.GetFileAttributes(pointer)
	if err != nil {
		return false, err
	}
	return attributes&windows.FILE_ATTRIBUTE_REPARSE_POINT != 0, nil
}
