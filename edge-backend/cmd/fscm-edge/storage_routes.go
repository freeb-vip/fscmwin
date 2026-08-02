package main

import (
	"net/http"
	"strings"

	"fscm-edge/internal/storage"

	"github.com/gin-gonic/gin"
)

type storageConfigRequest struct {
	Enabled              bool   `json:"enabled"`
	LocalPath            string `json:"local_path"`
	RetentionDays        int    `json:"retention_days"`
	ReserveFreeGB        int    `json:"reserve_free_gb"`
	SMBCompatibilityMode string `json:"smb_compatibility_mode"`
}

func registerStorageRoutes(admin *gin.RouterGroup, runtime storageRuntime) {
	manager := runtime.manager
	unavailable := func(c *gin.Context) bool {
		if manager != nil {
			return false
		}
		c.JSON(http.StatusServiceUnavailable, gin.H{"code": "STORAGE_UNAVAILABLE", "message": runtime.startupError})
		return true
	}
	admin.GET("/storage/config", func(c *gin.Context) {
		if unavailable(c) {
			return
		}
		cfg := manager.Config()
		status := manager.Status(c.Request.Context())
		c.JSON(http.StatusOK, gin.H{"config": cfg, "share_paths": status.SharePaths, "username": cfg.Username})
	})
	admin.PUT("/storage/config", func(c *gin.Context) {
		if unavailable(c) {
			return
		}
		var request storageConfigRequest
		if err := c.ShouldBindJSON(&request); err != nil {
			c.JSON(http.StatusBadRequest, gin.H{"code": "INVALID_STORAGE_CONFIG", "message": err.Error()})
			return
		}
		result, err := manager.Apply(c.Request.Context(), storage.Config{
			Enabled: request.Enabled, LocalPath: request.LocalPath,
			RetentionDays: request.RetentionDays, ReserveFreeGB: request.ReserveFreeGB,
			SMBCompatibilityMode: request.SMBCompatibilityMode,
		})
		if err != nil {
			writeStorageError(c, err)
			return
		}
		c.JSON(http.StatusOK, result)
	})
	admin.GET("/storage/status", func(c *gin.Context) {
		c.JSON(http.StatusOK, gin.H{"storage": runtime.Status(c.Request.Context())})
	})
	admin.POST("/storage/credentials/reset", func(c *gin.Context) {
		if unavailable(c) {
			return
		}
		credentials, err := manager.ResetCredentials(c.Request.Context())
		if err != nil {
			writeStorageError(c, err)
			return
		}
		c.JSON(http.StatusOK, gin.H{"credentials": credentials})
	})
	admin.POST("/storage/cleanup", func(c *gin.Context) {
		if unavailable(c) {
			return
		}
		if !manager.TriggerCleanup() {
			c.JSON(http.StatusConflict, gin.H{"code": "STORAGE_CLEANUP_UNAVAILABLE"})
			return
		}
		c.JSON(http.StatusAccepted, gin.H{"status": "cleaning"})
	})
}

func writeStorageError(c *gin.Context, err error) {
	message := err.Error()
	status, code := http.StatusInternalServerError, "STORAGE_CONFIG_FAILED"
	switch {
	case strings.Contains(message, "must be"), strings.Contains(message, "cannot be"), strings.Contains(message, "not writable"):
		status, code = http.StatusBadRequest, "INVALID_STORAGE_CONFIG"
	case strings.Contains(message, "_CONFLICT"), strings.Contains(message, "ACCOUNT_MISSING"):
		status, code = http.StatusConflict, "STORAGE_PROVISION_CONFLICT"
	}
	c.JSON(status, gin.H{"code": code, "message": message})
}
