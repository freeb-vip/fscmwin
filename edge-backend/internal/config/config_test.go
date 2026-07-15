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
