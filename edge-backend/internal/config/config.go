package config

import (
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"gopkg.in/yaml.v3"
)

type CacheConfig struct {
	Mode          string   `yaml:"mode"`
	MaxEntries    int      `yaml:"max_entries"`
	MaxMemoryMB   int64    `yaml:"max_memory_mb"`
	MaxObjectMB   int64    `yaml:"max_object_mb"`
	StaleIfError  bool     `yaml:"stale_if_error"`
	MaxStaleHours int      `yaml:"max_stale_hours"`
	Whitelist     []string `yaml:"whitelist"`
}

type cacheYAMLConfig struct {
	Mode          string   `yaml:"mode"`
	MaxEntries    int      `yaml:"max_entries"`
	MaxMemoryMB   int64    `yaml:"max_memory_mb"`
	MaxObjectMB   int64    `yaml:"max_object_mb"`
	StaleIfError  *bool    `yaml:"stale_if_error"`
	MaxStaleHours int      `yaml:"max_stale_hours"`
	Whitelist     []string `yaml:"whitelist"`
}

type Config struct {
	Port             string
	NodeID           string
	NodeName         string
	LANBaseURL       string
	CenterURL        string
	APIToken         string
	AdminToken       string
	NamespaceID      uint
	HeartbeatSeconds int
	PrintPollSeconds int
	Cache            CacheConfig
	DefaultPrinter   string
	PrintTemplate    string
	PrintWidthMM     float64
	PrintHeightMM    float64
	PrintOrientation string
	PrintMode        string
	PrintCopies      int
	SkuQRPrefix      string
	JobsPath         string
}

type yamlConfig struct {
	Database struct {
		SQLitePath string `yaml:"sqlite_path"`
	} `yaml:"database"`
	Server struct {
		Port string `yaml:"port"`
	} `yaml:"server"`
	Edge struct {
		NodeID           string          `yaml:"node_id"`
		NodeName         string          `yaml:"node_name"`
		LANBaseURL       string          `yaml:"lan_base_url"`
		CenterURL        string          `yaml:"center_url"`
		APIToken         string          `yaml:"api_token"`
		AdminToken       string          `yaml:"admin_token"`
		NamespaceID      uint            `yaml:"namespace_id"`
		HeartbeatSeconds int             `yaml:"heartbeat_seconds"`
		PrintPollSeconds int             `yaml:"print_poll_seconds"`
		Cache            cacheYAMLConfig `yaml:"cache"`
		CacheMode        string          `yaml:"cache_mode"`
		CacheMaxEntries  int             `yaml:"cache_max_entries"`
		CacheMaxMemoryMB int64           `yaml:"cache_max_memory_mb"`
		CacheMaxObjectMB int64           `yaml:"cache_max_object_mb"`
		CacheStale       *bool           `yaml:"cache_stale_if_error"`
		CacheMaxStale    int             `yaml:"cache_max_stale_hours"`
		DefaultPrinter   string          `yaml:"default_printer"`
		PrintTemplate    string          `yaml:"print_template"`
		PrintWidthMM     float64         `yaml:"print_width_mm"`
		PrintHeightMM    float64         `yaml:"print_height_mm"`
		PrintOrientation string          `yaml:"print_orientation"`
		PrintMode        string          `yaml:"print_mode"`
		PrintCopies      int             `yaml:"print_copies"`
		SkuQRPrefix      string          `yaml:"sku_qr_prefix"`
	} `yaml:"edge"`
}

func Load(path string) (*Config, error) {
	hostname, _ := os.Hostname()
	cfg := &Config{
		Port: "8089", NodeName: hostname, HeartbeatSeconds: 15, PrintPollSeconds: 5,
		Cache: CacheConfig{
			Mode: "standard", MaxEntries: 5000, MaxMemoryMB: 256, MaxObjectMB: 5,
			StaleIfError: true, MaxStaleHours: 24,
			Whitelist: []string{"/api/products", "/api/skus", "/api/case-specs", "/api/purchase-orders"},
		},
		PrintTemplate: "label_60x40mm", PrintWidthMM: 60, PrintHeightMM: 40,
		PrintOrientation: "portrait", PrintMode: "fit", PrintCopies: 1,
		SkuQRPrefix: "T",
		JobsPath:    "data/edge.db",
	}
	if data, err := os.ReadFile(path); err == nil {
		var raw yamlConfig
		if err := yaml.Unmarshal(data, &raw); err != nil {
			return nil, err
		}
		if raw.Server.Port != "" {
			cfg.Port = raw.Server.Port
		}
		if raw.Database.SQLitePath != "" {
			cfg.JobsPath = raw.Database.SQLitePath
		}
		cfg.NodeID, cfg.NodeName, cfg.LANBaseURL = raw.Edge.NodeID, first(raw.Edge.NodeName, cfg.NodeName), raw.Edge.LANBaseURL
		cfg.CenterURL, cfg.APIToken, cfg.AdminToken = raw.Edge.CenterURL, raw.Edge.APIToken, raw.Edge.AdminToken
		cfg.NamespaceID, cfg.DefaultPrinter = raw.Edge.NamespaceID, raw.Edge.DefaultPrinter
		cfg.SkuQRPrefix = first(raw.Edge.SkuQRPrefix, cfg.SkuQRPrefix)
		if raw.Edge.HeartbeatSeconds > 0 {
			cfg.HeartbeatSeconds = raw.Edge.HeartbeatSeconds
		}
		if raw.Edge.PrintPollSeconds > 0 {
			cfg.PrintPollSeconds = raw.Edge.PrintPollSeconds
		}
		applyCache(&cfg.Cache, raw.Edge)
		cfg.PrintTemplate = first(raw.Edge.PrintTemplate, cfg.PrintTemplate)
		if raw.Edge.PrintWidthMM > 0 {
			cfg.PrintWidthMM = raw.Edge.PrintWidthMM
		}
		if raw.Edge.PrintHeightMM > 0 {
			cfg.PrintHeightMM = raw.Edge.PrintHeightMM
		}
		cfg.PrintOrientation = first(raw.Edge.PrintOrientation, cfg.PrintOrientation)
		cfg.PrintMode = first(raw.Edge.PrintMode, cfg.PrintMode)
		if raw.Edge.PrintCopies > 0 {
			cfg.PrintCopies = raw.Edge.PrintCopies
		}
	} else if !os.IsNotExist(err) {
		return nil, err
	}
	if !filepath.IsAbs(cfg.JobsPath) {
		cfg.JobsPath = filepath.Join(filepath.Dir(path), cfg.JobsPath)
	}
	applyEnv(cfg)
	cfg.CenterURL = strings.TrimRight(strings.TrimSpace(cfg.CenterURL), "/")
	cfg.Cache.Mode = normalizeCacheMode(cfg.Cache.Mode)
	return cfg, nil
}

func applyCache(dst *CacheConfig, edge struct {
	NodeID           string          `yaml:"node_id"`
	NodeName         string          `yaml:"node_name"`
	LANBaseURL       string          `yaml:"lan_base_url"`
	CenterURL        string          `yaml:"center_url"`
	APIToken         string          `yaml:"api_token"`
	AdminToken       string          `yaml:"admin_token"`
	NamespaceID      uint            `yaml:"namespace_id"`
	HeartbeatSeconds int             `yaml:"heartbeat_seconds"`
	PrintPollSeconds int             `yaml:"print_poll_seconds"`
	Cache            cacheYAMLConfig `yaml:"cache"`
	CacheMode        string          `yaml:"cache_mode"`
	CacheMaxEntries  int             `yaml:"cache_max_entries"`
	CacheMaxMemoryMB int64           `yaml:"cache_max_memory_mb"`
	CacheMaxObjectMB int64           `yaml:"cache_max_object_mb"`
	CacheStale       *bool           `yaml:"cache_stale_if_error"`
	CacheMaxStale    int             `yaml:"cache_max_stale_hours"`
	DefaultPrinter   string          `yaml:"default_printer"`
	PrintTemplate    string          `yaml:"print_template"`
	PrintWidthMM     float64         `yaml:"print_width_mm"`
	PrintHeightMM    float64         `yaml:"print_height_mm"`
	PrintOrientation string          `yaml:"print_orientation"`
	PrintMode        string          `yaml:"print_mode"`
	PrintCopies      int             `yaml:"print_copies"`
	SkuQRPrefix      string          `yaml:"sku_qr_prefix"`
}) {
	if edge.Cache.Mode != "" {
		dst.Mode = edge.Cache.Mode
	}
	if edge.Cache.MaxEntries > 0 {
		dst.MaxEntries = edge.Cache.MaxEntries
	}
	if edge.Cache.MaxMemoryMB > 0 {
		dst.MaxMemoryMB = edge.Cache.MaxMemoryMB
	}
	if edge.Cache.MaxObjectMB > 0 {
		dst.MaxObjectMB = edge.Cache.MaxObjectMB
	}
	if edge.Cache.MaxStaleHours > 0 {
		dst.MaxStaleHours = edge.Cache.MaxStaleHours
	}
	if edge.Cache.StaleIfError != nil {
		dst.StaleIfError = *edge.Cache.StaleIfError
	}
	if len(edge.Cache.Whitelist) > 0 {
		dst.Whitelist = edge.Cache.Whitelist
	}
	if edge.CacheMode != "" {
		dst.Mode = edge.CacheMode
	}
	if edge.CacheMaxEntries > 0 {
		dst.MaxEntries = edge.CacheMaxEntries
	}
	if edge.CacheMaxMemoryMB > 0 {
		dst.MaxMemoryMB = edge.CacheMaxMemoryMB
	}
	if edge.CacheMaxObjectMB > 0 {
		dst.MaxObjectMB = edge.CacheMaxObjectMB
	}
	if edge.CacheStale != nil {
		dst.StaleIfError = *edge.CacheStale
	}
	if edge.CacheMaxStale > 0 {
		dst.MaxStaleHours = edge.CacheMaxStale
	}
}

func applyEnv(cfg *Config) {
	if value := os.Getenv("EDGE_CENTER_URL"); value != "" {
		cfg.CenterURL = value
	}
	if value := os.Getenv("EDGE_API_TOKEN"); value != "" {
		cfg.APIToken = value
	}
	if value := os.Getenv("EDGE_ADMIN_TOKEN"); value != "" {
		cfg.AdminToken = value
	}
	if value := os.Getenv("EDGE_CACHE_MODE"); value != "" {
		cfg.Cache.Mode = value
	}
	if value := os.Getenv("EDGE_NAMESPACE_ID"); value != "" {
		if parsed, err := strconv.ParseUint(value, 10, 32); err == nil {
			cfg.NamespaceID = uint(parsed)
		}
	}
}

func normalizeCacheMode(value string) string {
	switch strings.ToLower(strings.TrimSpace(value)) {
	case "disabled":
		return "disabled"
	case "aggressive":
		return "aggressive"
	default:
		return "standard"
	}
}

func first(values ...string) string {
	for _, value := range values {
		if strings.TrimSpace(value) != "" {
			return strings.TrimSpace(value)
		}
	}
	return ""
}
