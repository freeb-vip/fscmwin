package config

import (
	"os"
	"path/filepath"
	"testing"
)

func TestLoadFlatCacheSettingsAndWhitelist(t *testing.T) {
	path := filepath.Join(t.TempDir(), "edge.yaml")
	content := []byte("server:\n  port: \"9090\"\nedge:\n  center_url: \"http://center\"\n  namespace_id: 7\n  cache_mode: aggressive\n  cache_max_memory_mb: 128\n  cache_stale_if_error: false\n  cache:\n    whitelist:\n      - /api/products\n")
	if err := os.WriteFile(path, content, 0o600); err != nil {
		t.Fatal(err)
	}
	cfg, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Port != "9090" || cfg.NamespaceID != 7 || cfg.Cache.Mode != "aggressive" || cfg.Cache.MaxMemoryMB != 128 || cfg.Cache.StaleIfError || len(cfg.Cache.Whitelist) != 1 {
		t.Fatalf("unexpected config: %#v", cfg)
	}
}

func TestLoadCanDisableNestedStaleFallback(t *testing.T) {
	path := filepath.Join(t.TempDir(), "edge.yaml")
	content := []byte("edge:\n  center_url: \"http://center\"\n  cache:\n    stale_if_error: false\n")
	if err := os.WriteFile(path, content, 0o600); err != nil {
		t.Fatal(err)
	}
	cfg, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Cache.StaleIfError {
		t.Fatal("nested stale_if_error=false was ignored")
	}
}

func TestLoadMediaCacheDefaultsAndOverrides(t *testing.T) {
	directory := t.TempDir()
	path := filepath.Join(directory, "edge.yaml")
	content := []byte("database:\n  sqlite_path: data/edge.db\nedge:\n  media_cache:\n    path: cached-images\n    max_disk_mb: 512\n    max_object_mb: 6\n")
	if err := os.WriteFile(path, content, 0o600); err != nil {
		t.Fatal(err)
	}
	cfg, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.MediaCache.Path != filepath.Join(directory, "cached-images") || cfg.MediaCache.MaxDiskMB != 512 || cfg.MediaCache.MaxObjectMB != 6 {
		t.Fatalf("unexpected media cache config: %#v", cfg.MediaCache)
	}

	defaultPath := filepath.Join(directory, "default.yaml")
	if err := os.WriteFile(defaultPath, []byte("edge: {}\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	defaults, err := Load(defaultPath)
	if err != nil {
		t.Fatal(err)
	}
	if defaults.MediaCache.MaxDiskMB != 2048 || defaults.MediaCache.MaxObjectMB != 10 || filepath.Base(defaults.MediaCache.Path) != "media-cache" {
		t.Fatalf("unexpected media cache defaults: %#v", defaults.MediaCache)
	}
}
